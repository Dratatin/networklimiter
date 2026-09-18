using Microsoft.Extensions.Time.Testing;
using NetworkLimiter.Service.Resilience;

namespace NetworkLimiter.Service.Tests.Resilience;

/// <summary>
/// Résilience aux changements d'environnement réseau — FR-036, principe II.
/// </summary>
/// <remarks>
/// <para>
/// Le principe II élève les cas dégradés au rang d'exigences : bascule Wi-Fi ↔ Ethernet,
/// veille et reprise, partage de connexion mobile, adaptateurs virtuels Hyper-V, WSL ou
/// Docker. Ce ne sont pas des situations exotiques, ce sont les situations ordinaires d'un
/// ordinateur portable.
/// </para>
/// <para>
/// Le piège est le <b>volume</b> d'événements. Windows les émet par rafales : une simple
/// bascule d'adaptateur en produit une dizaine en quelques centaines de millisecondes, et une
/// sortie de veille davantage. Réagir à chacun ferait rouvrir les handles autant de fois, avec
/// à chaque fois une fenêtre pendant laquelle le trafic n'est pas limité — l'inverse exact de
/// l'effet recherché.
/// </para>
/// </remarks>
public sealed class NetworkChangeTests
{
    private static (NetworkChangeCoalescer Coalescer, FakeTimeProvider Clock) Create(
        TimeSpan? quietPeriod = null)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return (new NetworkChangeCoalescer(clock, quietPeriod ?? TimeSpan.FromSeconds(2)), clock);
    }

    // -- État initial ---------------------------------------------------------

    [Fact]
    public void SansEvenement_RienNEstATraiter()
    {
        (NetworkChangeCoalescer coalescer, _) = Create();

        coalescer.HasPendingChange.Should().BeFalse();
        coalescer.TryConsume().Should().BeNull();
    }

    // -- Regroupement des rafales ---------------------------------------------

    [Fact]
    public void RafaleDEvenements_NeProduitQuUneSeuleNotification()
    {
        // Le cas reel : une bascule Wi-Fi vers Ethernet.
        (NetworkChangeCoalescer coalescer, FakeTimeProvider clock) = Create();

        for (int i = 0; i < 12; i++)
        {
            coalescer.Record(NetworkChangeReason.AddressChanged);
            clock.Advance(TimeSpan.FromMilliseconds(50));
        }

        clock.Advance(TimeSpan.FromSeconds(2));

        coalescer.TryConsume().Should().Be(NetworkChangeReason.AddressChanged);
        coalescer.TryConsume().Should().BeNull("la notification a déjà été consommée");
    }

    [Fact]
    public void PendantLaRafale_RienNEstNotifie()
    {
        // Notifier trop tot ferait rouvrir les handles au milieu de la bascule, alors que la
        // pile reseau n'est pas encore stabilisee.
        (NetworkChangeCoalescer coalescer, FakeTimeProvider clock) = Create();

        for (int i = 0; i < 10; i++)
        {
            coalescer.Record(NetworkChangeReason.AddressChanged);
            clock.Advance(TimeSpan.FromMilliseconds(100));

            coalescer.TryConsume().Should().BeNull();
        }
    }

    [Fact]
    public void ChaqueEvenementProlongeLeDelai()
    {
        (NetworkChangeCoalescer coalescer, FakeTimeProvider clock) = Create(TimeSpan.FromSeconds(2));
        coalescer.Record(NetworkChangeReason.AddressChanged);

        clock.Advance(TimeSpan.FromMilliseconds(1900));
        coalescer.Record(NetworkChangeReason.AddressChanged);   // relance le compte

        clock.Advance(TimeSpan.FromMilliseconds(1900));
        coalescer.TryConsume().Should().BeNull("le second événement a relancé la période de silence");

        clock.Advance(TimeSpan.FromMilliseconds(200));
        coalescer.TryConsume().Should().NotBeNull();
    }

    [Fact]
    public void EvenementsEspaces_ProduisentPlusieursNotifications()
    {
        (NetworkChangeCoalescer coalescer, FakeTimeProvider clock) = Create(TimeSpan.FromSeconds(2));

        for (int i = 0; i < 3; i++)
        {
            coalescer.Record(NetworkChangeReason.AddressChanged);
            clock.Advance(TimeSpan.FromSeconds(5));

            coalescer.TryConsume().Should().NotBeNull($"le changement {i} est isolé");
        }
    }

    [Fact]
    public void ExactementAuSeuil_EstNotifie()
    {
        (NetworkChangeCoalescer coalescer, FakeTimeProvider clock) = Create(TimeSpan.FromSeconds(2));
        coalescer.Record(NetworkChangeReason.AddressChanged);

        clock.Advance(TimeSpan.FromSeconds(2));

        coalescer.TryConsume().Should().Be(NetworkChangeReason.AddressChanged);
    }

    // -- Priorité de la sortie de veille --------------------------------------

    [Fact]
    public void SortieDeVeille_PrimeSurLesAutresCauses()
    {
        // La sortie de veille est la plus perturbante pour la pile reseau, et c'est la cause
        // qu'il faut afficher a l'utilisateur si la reapplication prend du temps. Noyee dans
        // une rafale de changements d'adresse, elle disparaitrait du diagnostic.
        (NetworkChangeCoalescer coalescer, FakeTimeProvider clock) = Create();

        coalescer.Record(NetworkChangeReason.AddressChanged);
        coalescer.Record(NetworkChangeReason.AvailabilityChanged);
        coalescer.Record(NetworkChangeReason.ResumedFromSleep);
        coalescer.Record(NetworkChangeReason.AddressChanged);

        clock.Advance(TimeSpan.FromSeconds(3));

        coalescer.TryConsume().Should().Be(NetworkChangeReason.ResumedFromSleep);
    }

    [Fact]
    public void PremiereCause_EstConserveeSiAucuneSortieDeVeille()
    {
        (NetworkChangeCoalescer coalescer, FakeTimeProvider clock) = Create();

        coalescer.Record(NetworkChangeReason.AvailabilityChanged);
        coalescer.Record(NetworkChangeReason.AddressChanged);

        clock.Advance(TimeSpan.FromSeconds(3));

        coalescer.TryConsume().Should().Be(NetworkChangeReason.AvailabilityChanged);
    }

    // -- Cycle complet --------------------------------------------------------

    [Fact]
    public void ApresConsommation_UnNouveauChangementRepartDeZero()
    {
        (NetworkChangeCoalescer coalescer, FakeTimeProvider clock) = Create(TimeSpan.FromSeconds(2));

        coalescer.Record(NetworkChangeReason.ResumedFromSleep);
        clock.Advance(TimeSpan.FromSeconds(3));
        coalescer.TryConsume().Should().Be(NetworkChangeReason.ResumedFromSleep);

        coalescer.Record(NetworkChangeReason.AddressChanged);
        clock.Advance(TimeSpan.FromSeconds(3));

        coalescer.TryConsume().Should().Be(NetworkChangeReason.AddressChanged);
    }

    [Fact]
    public void Reset_OublieLeChangementEnAttente()
    {
        // Appele a la fermeture des handles : reagir a un changement survenu avant l'arret
        // n'aurait aucun sens.
        (NetworkChangeCoalescer coalescer, FakeTimeProvider clock) = Create();
        coalescer.Record(NetworkChangeReason.AddressChanged);

        coalescer.Reset();
        clock.Advance(TimeSpan.FromSeconds(10));

        coalescer.HasPendingChange.Should().BeFalse();
        coalescer.TryConsume().Should().BeNull();
    }

    [Fact]
    public void HasPendingChange_RefleteLEtatReel()
    {
        (NetworkChangeCoalescer coalescer, FakeTimeProvider clock) = Create();

        coalescer.HasPendingChange.Should().BeFalse();

        coalescer.Record(NetworkChangeReason.AddressChanged);
        coalescer.HasPendingChange.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(3));
        coalescer.TryConsume();

        coalescer.HasPendingChange.Should().BeFalse();
    }

    // -- Robustesse -----------------------------------------------------------

    [Fact]
    public void Constructeur_SansHorloge_Leve()
    {
        Action act = () => _ = new NetworkChangeCoalescer(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PeriodeDeSilenceNonPositive_Leve(int seconds)
    {
        Action act = () => _ = new NetworkChangeCoalescer(
            new FakeTimeProvider(), TimeSpan.FromSeconds(seconds));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ToutesLesCauses_SontDistinctes()
    {
        Enum.GetValues<NetworkChangeReason>().Select(r => (int)r).Should().OnlyHaveUniqueItems();
    }
}
