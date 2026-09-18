using Microsoft.Extensions.Time.Testing;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Core.Classification;
using NetworkLimiter.Core.Rules;
using NetworkLimiter.Core.Shaping;
using NetworkLimiter.Service.Health;
using NetworkLimiter.Service.Interception;

namespace NetworkLimiter.Service.Tests.Interception;

/// <summary>
/// Propagation des règles au pipeline vivant — FR-002, FR-024, FR-026.
/// </summary>
/// <remarks>
/// <para>
/// Pivot entre ce que l'utilisateur a demandé et ce qui s'applique réellement au trafic. C'est
/// le composant qui rend FR-002 vrai : une règle modifiée prend effet sans redémarrer quoi que
/// ce soit, ni le service, ni l'application visée.
/// </para>
/// <para>
/// Il porte aussi la réponse à « pourquoi cette application n'est pas limitée ? » (FR-026).
/// Une règle définie mais inactive sans raison exploitable rendrait le produit imprévisible,
/// donc inutilisable.
/// </para>
/// </remarks>
public sealed class ShapingCoordinatorTests
{
    private sealed class FakePathProbe : IPathExistenceProbe
    {
        private readonly HashSet<string> _missing = new(StringComparer.Ordinal);

        public void MarkMissing(string path) => _missing.Add(path);

        public bool Exists(string executablePath) => !_missing.Contains(executablePath);
    }

    private static readonly Guid RuleId = new("11111111-1111-1111-1111-111111111111");

    private static RuleDto Rule(
        Guid? id = null,
        string path = @"c:\app\app.exe",
        long? download = 1_048_576,
        bool enabled = true) =>
        new()
        {
            Id = id ?? RuleId,
            Target = new AppIdentityDto
            {
                ExecutablePath = path,
                ExecutableName = path[(path.LastIndexOf('\\') + 1)..],
                DisplayName = "App",
            },
            DownloadBytesPerSecond = download,
            UploadBytesPerSecond = null,
            Enabled = enabled,
            ExemptFromGlobal = false,
        };

    private static (ShapingCoordinator Coordinator, PacketShaper Shaper, RuleResolver Resolver,
                    FakePathProbe Probe, FakeTimeProvider Clock) Create()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var shaper = new PacketShaper(clock);
        var resolver = new RuleResolver();
        var probe = new FakePathProbe();

        return (new ShapingCoordinator(shaper, resolver, probe), shaper, resolver, probe, clock);
    }

    private static ShapingRequest Packet(Guid? ruleId, int size = 1500) =>
        new(1, ruleId, PacketDirection.Inbound, TransportProtocol.Tcp, NetworkScope.Internet, size);

    // -- Application des règles -----------------------------------------------

    [Fact]
    public void AppliquerUneRegle_LaRendActiveDansLeResolveurEtLeShaper()
    {
        (ShapingCoordinator coordinator, PacketShaper shaper, RuleResolver resolver, _, _) = Create();

        coordinator.Apply([Rule()]);

        resolver.ActiveRuleCount.Should().Be(1);
        shaper.ShapedRuleCount.Should().Be(1);
        shaper.Evaluate(Packet(RuleId)).Should().Be(ShapingOutcome.Send);
    }

    [Fact]
    public void RegleDesactivee_NEstNiResolueNiMiseEnForme()
    {
        (ShapingCoordinator coordinator, PacketShaper shaper, RuleResolver resolver, _, _) = Create();

        coordinator.Apply([Rule(enabled: false)]);

        resolver.ActiveRuleCount.Should().Be(0);
        shaper.ShapedRuleCount.Should().Be(0);
    }

    [Fact]
    public void RetirerUneRegle_LaFaitDisparaitreDuPipeline()
    {
        (ShapingCoordinator coordinator, PacketShaper shaper, RuleResolver resolver, _, _) = Create();
        coordinator.Apply([Rule()]);

        coordinator.Apply([]);

        resolver.ActiveRuleCount.Should().Be(0);
        shaper.ShapedRuleCount.Should().Be(0);
        shaper.Evaluate(Packet(RuleId)).Should().Be(ShapingOutcome.PassThrough);
    }

    [Fact]
    public void ModifierUnPlafond_NeRepartPasDUnSeauPlein()
    {
        // Si le seau etait recree a chaque modification, l'utilisateur qui ajuste son plafond
        // verrait une rafale a chaque frappe.
        (ShapingCoordinator coordinator, PacketShaper shaper, _, _, _) = Create();
        coordinator.Apply([Rule(download: 10_240)]);

        while (shaper.Evaluate(Packet(RuleId)) == ShapingOutcome.Send)
        {
        }

        coordinator.Apply([Rule(download: 20_480)]);

        shaper.Evaluate(Packet(RuleId)).Should().NotBe(ShapingOutcome.Send);
    }

    // -- Chemin disparu et repli ----------------------------------------------

    [Fact]
    public void CheminDisparu_ActiveLeRepliDansLeResolveur()
    {
        (ShapingCoordinator coordinator, _, RuleResolver resolver, FakePathProbe probe, _) = Create();
        probe.MarkMissing(@"c:\app\app-1.2.3\app.exe");

        coordinator.Apply([Rule(path: @"c:\app\app-1.2.3\app.exe")]);

        RuleMatch? match = resolver.Resolve(
            new AppIdentity(@"c:\app\app-1.2.4\app.exe", "app.exe", "App"));

        match.Should().NotBeNull();
        match!.Mode.Should().Be(RuleMatchMode.FallbackName);
    }

    [Fact]
    public void CheminPresent_NActivePasLeRepli()
    {
        (ShapingCoordinator coordinator, _, RuleResolver resolver, _, _) = Create();

        coordinator.Apply([Rule(path: @"c:\outils\updater.exe")]);

        resolver.Resolve(new AppIdentity(@"c:\autre\updater.exe", "updater.exe", "Autre"))
                .Should().BeNull();
    }

    // -- Suspension (FR-024) --------------------------------------------------

    [Fact]
    public void Suspension_ArreteToutePlafonnementSansPerdreLesRegles()
    {
        (ShapingCoordinator coordinator, PacketShaper shaper, _, _, _) = Create();
        coordinator.Apply([Rule()]);

        coordinator.Suspended = true;

        shaper.ShapedRuleCount.Should().Be(0);
        shaper.Evaluate(Packet(RuleId)).Should().Be(ShapingOutcome.PassThrough);
    }

    [Fact]
    public void ReprendreApresSuspension_ReappliqueLesRegles()
    {
        (ShapingCoordinator coordinator, PacketShaper shaper, _, _, _) = Create();
        coordinator.Apply([Rule()]);
        coordinator.Suspended = true;

        coordinator.Suspended = false;

        shaper.ShapedRuleCount.Should().Be(1);
        shaper.Evaluate(Packet(RuleId)).Should().Be(ShapingOutcome.Send);
    }

    [Fact]
    public void Suspension_LibereLesPaquetsEnAttente()
    {
        // Les garder dans une file dont plus personne ne s'occupe les perdrait
        // definitivement (principe IV).
        (ShapingCoordinator coordinator, PacketShaper shaper, _, _, _) = Create();
        coordinator.Apply([Rule(download: 10_240)]);

        while (shaper.Evaluate(Packet(RuleId)) != ShapingOutcome.Delay)
        {
        }

        coordinator.Suspended = true;

        coordinator.ReleasedPackets.Should().NotBeEmpty();
    }

    // -- Interception indisponible --------------------------------------------

    [Fact]
    public void InterceptionIndisponible_AucuneRegleNeSApplique()
    {
        (ShapingCoordinator coordinator, PacketShaper shaper, _, _, _) = Create();
        coordinator.Apply([Rule()]);

        coordinator.InterceptionAvailable = false;

        shaper.ShapedRuleCount.Should().Be(0);
    }

    // -- Raisons d'inactivité (FR-026) ----------------------------------------

    [Fact]
    public void ApplicationNonLancee_EstLaRaisonRendue()
    {
        (ShapingCoordinator coordinator, _, _, _, _) = Create();
        coordinator.Apply([Rule()]);

        IReadOnlyList<InactiveRule> inactive = coordinator.GetInactiveRules(new HashSet<string>());

        inactive.Should().ContainSingle()
                .Which.Reason.Should().Be(RuleInactiveReason.ApplicationNotRunning);
    }

    [Fact]
    public void ApplicationLancee_NEstPasSignaleeInactive()
    {
        (ShapingCoordinator coordinator, _, _, _, _) = Create();
        coordinator.Apply([Rule()]);

        coordinator.GetInactiveRules(new HashSet<string>(StringComparer.Ordinal) { @"c:\app\app.exe" })
                   .Should().BeEmpty();
    }

    [Fact]
    public void RegleDesactivee_EstSignaleeCommeTelle()
    {
        (ShapingCoordinator coordinator, _, _, _, _) = Create();
        coordinator.Apply([Rule(enabled: false)]);

        coordinator.GetInactiveRules(new HashSet<string>())
                   .Should().ContainSingle()
                   .Which.Reason.Should().Be(RuleInactiveReason.RuleDisabled);
    }

    [Fact]
    public void CheminIntrouvableEtAucunHomonyme_EstSignale()
    {
        (ShapingCoordinator coordinator, _, _, FakePathProbe probe, _) = Create();
        probe.MarkMissing(@"c:\app\app.exe");
        coordinator.Apply([Rule()]);

        coordinator.GetInactiveRules(new HashSet<string>())
                   .Should().ContainSingle()
                   .Which.Reason.Should().Be(RuleInactiveReason.ExecutablePathNotFound);
    }

    [Fact]
    public void AppariementParRepli_EstSignaleCommeTel()
    {
        // FR-039a : l'utilisateur doit savoir que sa regle ne vise plus l'executable exact
        // qu'il a designe, mais tout programme portant ce nom de fichier.
        (ShapingCoordinator coordinator, _, _, FakePathProbe probe, _) = Create();
        probe.MarkMissing(@"c:\app\app-1.2.3\app.exe");
        coordinator.Apply([Rule(path: @"c:\app\app-1.2.3\app.exe")]);

        coordinator.GetInactiveRules(
                new HashSet<string>(StringComparer.Ordinal) { @"c:\app\app-1.2.4\app.exe" })
            .Should().ContainSingle()
            .Which.Reason.Should().Be(RuleInactiveReason.MatchedByFallbackName);
    }

    [Fact]
    public void SuspensionGlobale_PrimeSurLesAutresRaisons()
    {
        // Dire « application non lancee » alors que toute la limitation est suspendue
        // enverrait l'utilisateur chercher au mauvais endroit.
        (ShapingCoordinator coordinator, _, _, _, _) = Create();
        coordinator.Apply([Rule(enabled: false)]);
        coordinator.Suspended = true;

        coordinator.GetInactiveRules(new HashSet<string>())
                   .Should().ContainSingle()
                   .Which.Reason.Should().Be(RuleInactiveReason.GloballySuspended);
    }

    [Fact]
    public void InterceptionIndisponible_PrimeSurToutLeReste()
    {
        (ShapingCoordinator coordinator, _, _, _, _) = Create();
        coordinator.Apply([Rule()]);
        coordinator.Suspended = true;
        coordinator.InterceptionAvailable = false;

        coordinator.GetInactiveRules(new HashSet<string>())
                   .Should().ContainSingle()
                   .Which.Reason.Should().Be(RuleInactiveReason.InterceptionUnavailable);
    }

    [Fact]
    public void ChaqueRegleInactive_PorteExactementUneRaison()
    {
        // Verrouille l'invariant de data-model.md : une regle inactive sans raison est un
        // defaut, pas un cas particulier.
        (ShapingCoordinator coordinator, _, _, FakePathProbe probe, _) = Create();
        probe.MarkMissing(@"c:\c\c.exe");

        coordinator.Apply([
            Rule(id: Guid.NewGuid(), path: @"c:\a\a.exe"),
            Rule(id: Guid.NewGuid(), path: @"c:\b\b.exe", enabled: false),
            Rule(id: Guid.NewGuid(), path: @"c:\c\c.exe"),
        ]);

        IReadOnlyList<InactiveRule> inactive = coordinator.GetInactiveRules(new HashSet<string>());

        inactive.Should().HaveCount(3);
        inactive.Select(rule => rule.RuleId).Should().OnlyHaveUniqueItems();
    }

    // -- Robustesse -----------------------------------------------------------

    [Fact]
    public void Constructeur_SansDependance_Leve()
    {
        var clock = new FakeTimeProvider();

        Action noShaper = () => _ = new ShapingCoordinator(null!, new RuleResolver(), new FakePathProbe());
        Action noResolver = () => _ = new ShapingCoordinator(new PacketShaper(clock), null!, new FakePathProbe());
        Action noProbe = () => _ = new ShapingCoordinator(new PacketShaper(clock), new RuleResolver(), null!);

        noShaper.Should().Throw<ArgumentNullException>();
        noResolver.Should().Throw<ArgumentNullException>();
        noProbe.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Apply_SansRegles_Leve()
    {
        (ShapingCoordinator coordinator, _, _, _, _) = Create();

        Action act = () => coordinator.Apply(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
