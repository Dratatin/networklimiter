using Microsoft.Extensions.Time.Testing;

namespace NetworkLimiter.Core.Tests.Time;

/// <summary>
/// Caractérise le contrat d'horloge dont dépend toute la mise en forme de trafic.
/// </summary>
/// <remarks>
/// Le projet n'écrit pas sa propre abstraction d'horloge : il utilise <see cref="TimeProvider"/>
/// du framework, et <c>FakeTimeProvider</c> dans les tests. Ces tests ne valident donc pas
/// notre code mais le <b>contrat</b> sur lequel le seau à jetons s'appuiera — mesure de durée
/// exacte, monotonie, et progression uniquement sur demande explicite.
///
/// S'ils venaient à échouer après une montée de version, cela signifierait que les garanties
/// de déterminisme du principe III ne tiennent plus, et le shaper deviendrait non testable
/// sans attente réelle. C'est précisément ce qu'on veut détecter tôt.
/// </remarks>
public sealed class VirtualClockTests
{
    private static FakeTimeProvider CreateClock() =>
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void GetElapsedTime_ApresAvance_RendLaDureeExacte()
    {
        FakeTimeProvider clock = CreateClock();
        long start = clock.GetTimestamp();

        clock.Advance(TimeSpan.FromMilliseconds(250));

        clock.GetElapsedTime(start).Should().Be(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void GetTimestamp_SansAvance_NeProgressePas()
    {
        // Le seau à jetons recalcule ses jetons a partir du temps ecoule. Si l'horloge
        // avancait toute seule, deux lectures successives produiraient des resultats
        // differents et les tests deviendraient instables.
        FakeTimeProvider clock = CreateClock();

        long first = clock.GetTimestamp();
        long second = clock.GetTimestamp();

        second.Should().Be(first);
    }

    [Fact]
    public void GetTimestamp_EstMonotoneCroissant()
    {
        FakeTimeProvider clock = CreateClock();
        long previous = clock.GetTimestamp();

        for (int i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            long current = clock.GetTimestamp();

            current.Should().BeGreaterThan(previous);
            previous = current;
        }
    }

    [Fact]
    public void GetElapsedTime_SurDeTresPetitsIncrements_ResteExact()
    {
        // Le drainage lit par lots toutes les quelques millisecondes : la resolution doit
        // rester fidele bien en dessous de la milliseconde.
        FakeTimeProvider clock = CreateClock();
        long start = clock.GetTimestamp();

        clock.Advance(TimeSpan.FromTicks(1));

        clock.GetElapsedTime(start).Should().Be(TimeSpan.FromTicks(1));
    }

    [Fact]
    public void Advance_CumuleLesDurees()
    {
        FakeTimeProvider clock = CreateClock();
        long start = clock.GetTimestamp();

        clock.Advance(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(7));

        clock.GetElapsedTime(start).Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void UtcNow_SuitLesAvances()
    {
        // Utilise pour l'horodatage des journaux, pas pour la mise en forme.
        FakeTimeProvider clock = CreateClock();
        DateTimeOffset before = clock.GetUtcNow();

        clock.Advance(TimeSpan.FromHours(3));

        clock.GetUtcNow().Should().Be(before.AddHours(3));
    }
}
