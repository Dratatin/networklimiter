using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NetworkLimiter.Core.Classification;
using NetworkLimiter.Core.Rules;
using NetworkLimiter.Core.Shaping;
using NetworkLimiter.Core.Units;
using NetworkLimiter.Service.FlowTable;
using NetworkLimiter.Service.ProcessIdentity;
using Xunit;

namespace NetworkLimiter.Service.Tests.Interception;

/// <summary>
/// Vérifie que l'état partagé par les boucles d'interception supporte l'accès simultané.
/// </summary>
/// <remarks>
/// <para>
/// Écrits après un plantage en production. La boucle des flux est morte sur
/// <c>« Operations that change non-concurrent collections must have exclusive access »</c> :
/// le cache d'identités de <see cref="ProcessIdentityResolver"/> était un
/// <c>Dictionary</c> ordinaire, sollicité par la boucle des flux et celle du réseau en même
/// temps.
/// </para>
/// <para>
/// Le pipeline a été écrit avec deux threads dédiés, et aucune des quatre structures qu'ils
/// partagent n'avait été auditée. Les tests unitaires existants les exerçaient tous sur un
/// seul thread : ils prouvaient la logique, jamais la sûreté d'accès. Ces tests-ci comblent
/// exactement cet écart.
/// </para>
/// <para>
/// Un test de concurrence ne prouve pas l'absence de course — il la rend probable sous charge.
/// C'est suffisant ici : sans verrou, ces quatre cas échouent de façon fiable.
/// </para>
/// </remarks>
public sealed class ConcurrentAccessTests
{
    private const int Threads = 8;
    private const int IterationsPerThread = 4_000;

    [Fact]
    public void LeCacheDIdentites_SupporteDeuxBouclesSimultanees()
    {
        var resolver = new ProcessIdentityResolver(
            new StubProcessInfoProvider(),
            new Core.Classification.PathNormalizer(new StubVolumeResolver()),
            maxEntries: 64);

        // Plus d'identifiants que d'entrees : force l'eviction a s'executer en concurrence,
        // qui est le moment ou une collection non protegee casse le plus surement.
        RunInParallel(index =>
        {
            uint processId = (uint)(index % 256);

            resolver.Resolve(processId);

            if (index % 32 == 0)
            {
                resolver.Forget(processId);
            }

            _ = resolver.CacheCount;
        });

        resolver.CacheCount.Should().BeLessThanOrEqualTo(64);
    }

    [Fact]
    public void LaTableDeFlux_SupporteAlimentationEtConsultationSimultanees()
    {
        var clock = new FakeTimeProvider();
        var table = new Service.FlowTable.FlowTable(clock, maxEntries: 128);

        // La consultation ECRIT — elle rafraichit la date de derniere vue. Deux threads qui
        // « lisent » mutent donc la meme table.
        RunInParallel(index =>
        {
            FlowKey key = BuildKey(index % 512);

            if (index % 3 == 0)
            {
                table.OnFlowEstablished(key, (ulong)index, (uint)(index % 64));
            }
            else if (index % 7 == 0)
            {
                table.OnFlowDeleted(key);
            }
            else
            {
                table.TryGetProcessId(key, out _);
            }
        });

        table.Count.Should().BeLessThanOrEqualTo(128);
    }

    [Fact]
    public void LeMetteurEnForme_SupporteEvaluationPendantChangementDeRegles()
    {
        var clock = new FakeTimeProvider();
        var shaper = new PacketShaper(clock);
        Guid[] ruleIds = [.. Enumerable.Range(0, 8).Select(_ => Guid.NewGuid())];

        shaper.ApplyRules([.. ruleIds.Select(id => new ShaperRule(id, Rate(65_536), Rate(65_536)))]);

        var drained = new List<PendingPacket>();

        RunInParallel(index =>
        {
            if (index % 200 == 0)
            {
                // Modification de regles depuis un autre thread, comme le fait le
                // coordinateur des qu'une regle change : c'est le scenario FR-002.
                shaper.ApplyRules(
                    [.. ruleIds.Take(1 + (index % ruleIds.Length))
                        .Select(id => new ShaperRule(id, Rate(65_536), null))]);

                return;
            }

            shaper.Evaluate(new ShapingRequest(
                Token: index,
                RuleId: ruleIds[index % ruleIds.Length],
                Direction: PacketDirection.Inbound,
                Protocol: TransportProtocol.Tcp,
                Scope: NetworkScope.Internet,
                SizeBytes: 1400));

            if (index % 500 == 0)
            {
                lock (drained)
                {
                    shaper.DrainReady(drained);
                }
            }
        });

        shaper.ShapedRuleCount.Should().BePositive();
    }

    [Fact]
    public void LeResolveurDeRegles_NeVoitJamaisUnJeuADemiConstruit()
    {
        var resolver = new RuleResolver();
        var identity = new AppIdentity(@"c:\app\x.exe", "x.exe", "x");

        RuleTarget[] full =
        [
            new(Guid.NewGuid(), identity, Enabled: true, TargetPathExists: true),
            .. Enumerable.Range(0, 64).Select(index => new RuleTarget(
                Guid.NewGuid(),
                new AppIdentity($@"c:\app\{index}.exe", $"{index}.exe", $"{index}"),
                Enabled: true,
                TargetPathExists: true)),
        ];

        resolver.Update(full);

        int missed = 0;

        RunInParallel(index =>
        {
            if (index % 100 == 0)
            {
                resolver.Update(full);
                return;
            }

            if (resolver.Resolve(identity) is null)
            {
                Interlocked.Increment(ref missed);
            }
        });

        // Le point essentiel : une regle presente avant ET apres la mise a jour ne doit
        // JAMAIS disparaitre pendant. Avec des dictionnaires vides puis remplis en place, ce
        // compteur monte — et en production, cela signifie une limite qui saute par
        // intermittence, sans trace.
        missed.Should().Be(0,
            "une règle inchangée ne doit jamais cesser d'apparier pendant une mise à jour");
    }

    private static void RunInParallel(Action<int> body)
    {
        using var barrier = new Barrier(Threads);
        var failures = new List<Exception>();

        // Une barriere plutot qu'un demarrage echelonne : sans elle, les threads se succedent
        // au lieu de se chevaucher, et le test ne prouve plus rien.
        var threads = new Thread[Threads];

        for (int t = 0; t < Threads; t++)
        {
            int offset = t * IterationsPerThread;

            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();

                try
                {
                    for (int i = 0; i < IterationsPerThread; i++)
                    {
                        body(offset + i);
                    }
                }
                catch (Exception exception)
                {
                    lock (failures)
                    {
                        failures.Add(exception);
                    }
                }
            })
            {
                IsBackground = true,
            };

            threads[t].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        failures.Should().BeEmpty(
            "l'état partagé par les deux boucles d'interception doit supporter l'accès simultané");
    }

    private static ByteRate? Rate(long bytesPerSecond) =>
        ByteRate.FromBytesPerSecond(bytesPerSecond);

    private static FlowKey BuildKey(int seed) => new(
        6,
        System.Net.IPAddress.Parse("192.168.1.10"),
        (ushort)(10_000 + seed),
        System.Net.IPAddress.Parse("104.16.0.1"),
        443);

    private sealed class StubProcessInfoProvider : IProcessInfoProvider
    {
        public bool TryGetProcessInfo(uint processId, out ProcessInfo? info)
        {
            info = new ProcessInfo($@"C:\app\{processId}.exe", processId, IsPackaged: false);
            return true;
        }
    }

    private sealed class StubVolumeResolver : Core.Classification.IDeviceVolumeResolver
    {
        public bool TryResolve(string devicePath, out string resolved)
        {
            resolved = string.Empty;
            return false;
        }
    }
}
