using Microsoft.Extensions.Time.Testing;
using NetworkLimiter.Service.Health;

namespace NetworkLimiter.Service.Tests.Health;

/// <summary>
/// Surveillance de la pression de file — research.md R-004, principe IV.
/// </summary>
/// <remarks>
/// <para>
/// WinDivert <b>rejette</b> les paquets quand sa file noyau déborde : 16 384 paquets, 32 Mo,
/// ou 2 secondes de séjour. Si la boucle de drainage ne tient pas la cadence, la limitation
/// se transforme en perte de paquets non maîtrisée — un mode de défaillance opaque, qui
/// dégrade la latence de toute la machine et pas seulement de l'application visée.
/// </para>
/// <para>
/// Ce moniteur détecte la situation pour que le service <b>ferme ses handles</b> et libère le
/// trafic, plutôt que de laisser le noyau jeter des paquets en silence. Mieux vaut ne plus
/// limiter que dégrader la connexion sans le dire.
/// </para>
/// <para>
/// La subtilité encodée ici : un lot plein isolé est <b>normal</b> sous charge. Seule une
/// saturation <b>soutenue</b> signifie qu'on décroche. Déclencher sur un pic produirait des
/// libérations intempestives à chaque téléchargement.
/// </para>
/// </remarks>
public sealed class QueuePressureMonitorTests
{
    private const int BatchCapacity = 255;

    private static (QueuePressureMonitor Monitor, FakeTimeProvider Clock) Create(
        double threshold = 0.9,
        TimeSpan? sustainedFor = null)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return (new QueuePressureMonitor(clock, threshold, sustainedFor ?? TimeSpan.FromSeconds(2)), clock);
    }

    // -- État initial ---------------------------------------------------------

    [Fact]
    public void AuDemarrage_LaPressionEstNulle()
    {
        (QueuePressureMonitor monitor, _) = Create();

        monitor.Pressure.Should().Be(0);
        monitor.ShouldReleaseTraffic.Should().BeFalse();
    }

    // -- Mesure de la pression ------------------------------------------------

    [Fact]
    public void LotsVides_DonnentUnePressionNulle()
    {
        (QueuePressureMonitor monitor, FakeTimeProvider clock) = Create();

        for (int i = 0; i < 10; i++)
        {
            monitor.RecordBatch(packetsRead: 0, BatchCapacity);
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        monitor.Pressure.Should().Be(0);
    }

    [Fact]
    public void LotsPleins_DonnentUnePressionMaximale()
    {
        (QueuePressureMonitor monitor, FakeTimeProvider clock) = Create();

        for (int i = 0; i < 10; i++)
        {
            monitor.RecordBatch(BatchCapacity, BatchCapacity);
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        monitor.Pressure.Should().Be(1);
    }

    [Fact]
    public void LotsAMoitiePleins_ConvergentVersUnePressionIntermediaire()
    {
        // La mesure est une moyenne mobile exponentielle : elle converge asymptotiquement,
        // elle n'atteint pas sa cible en quelques lots. C'est voulu — c'est ce lissage qui
        // fait qu'un lot plein isolé ne déclenche rien. Le test doit donc laisser la mesure
        // converger, sur 50 lots, plutôt que d'exiger une valeur exacte trop tôt.
        (QueuePressureMonitor monitor, FakeTimeProvider clock) = Create();

        for (int i = 0; i < 50; i++)
        {
            monitor.RecordBatch(BatchCapacity / 2, BatchCapacity);
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        monitor.Pressure.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void LaMesure_MonteProgressivementEtNonDUnCoup()
    {
        // Verrouille explicitement le lissage : sans lui, la premiere lecture pleine
        // porterait deja la pression a 1 et le garde-fou se declencherait sur un pic.
        (QueuePressureMonitor monitor, FakeTimeProvider clock) = Create();

        monitor.RecordBatch(BatchCapacity, BatchCapacity);
        double afterFirst = monitor.Pressure;

        clock.Advance(TimeSpan.FromMilliseconds(100));
        monitor.RecordBatch(BatchCapacity, BatchCapacity);
        double afterSecond = monitor.Pressure;

        afterFirst.Should().BeLessThan(1);
        afterSecond.Should().BeGreaterThan(afterFirst);
    }

    // -- Un pic isolé ne déclenche pas ---------------------------------------

    [Fact]
    public void UnSeulLotPlein_NeDeclenchePasLaLiberation()
    {
        // Un lot plein isole est normal : un telechargement qui demarre remplit la file le
        // temps que la boucle prenne son rythme. Declencher ici libererait le trafic a
        // chaque debut de transfert.
        (QueuePressureMonitor monitor, _) = Create();

        monitor.RecordBatch(BatchCapacity, BatchCapacity);

        monitor.ShouldReleaseTraffic.Should().BeFalse();
    }

    [Fact]
    public void SaturationBreve_NeDeclenchePasLaLiberation()
    {
        (QueuePressureMonitor monitor, FakeTimeProvider clock) = Create(
            sustainedFor: TimeSpan.FromSeconds(2));

        for (int i = 0; i < 5; i++)
        {
            monitor.RecordBatch(BatchCapacity, BatchCapacity);
            clock.Advance(TimeSpan.FromMilliseconds(100));   // 500 ms au total
        }

        monitor.ShouldReleaseTraffic.Should().BeFalse();
    }

    // -- Une saturation soutenue déclenche ------------------------------------

    [Fact]
    public void SaturationSoutenue_DeclencheLaLiberation()
    {
        (QueuePressureMonitor monitor, FakeTimeProvider clock) = Create(
            sustainedFor: TimeSpan.FromSeconds(2));

        for (int i = 0; i < 30; i++)
        {
            monitor.RecordBatch(BatchCapacity, BatchCapacity);
            clock.Advance(TimeSpan.FromMilliseconds(100));   // 3 s au total
        }

        monitor.ShouldReleaseTraffic.Should().BeTrue();
    }

    [Fact]
    public void RetourAuCalme_AnnuleLeDecompteDeSaturation()
    {
        // La saturation doit etre CONTINUE. Une accalmie prouve que la boucle a rattrape
        // son retard : repartir de zero evite de liberer le trafic pour une saturation
        // ancienne et deja resorbee.
        (QueuePressureMonitor monitor, FakeTimeProvider clock) = Create(
            sustainedFor: TimeSpan.FromSeconds(2));

        for (int i = 0; i < 15; i++)
        {
            monitor.RecordBatch(BatchCapacity, BatchCapacity);
            clock.Advance(TimeSpan.FromMilliseconds(100));   // 1,5 s de saturation
        }

        monitor.RecordBatch(packetsRead: 1, BatchCapacity);  // accalmie
        clock.Advance(TimeSpan.FromMilliseconds(100));

        for (int i = 0; i < 15; i++)
        {
            monitor.RecordBatch(BatchCapacity, BatchCapacity);
            clock.Advance(TimeSpan.FromMilliseconds(100));   // 1,5 s a nouveau
        }

        monitor.ShouldReleaseTraffic.Should().BeFalse();
    }

    [Fact]
    public void SaturationSousLeSeuil_NeDeclenchePas()
    {
        (QueuePressureMonitor monitor, FakeTimeProvider clock) = Create(threshold: 0.9);

        for (int i = 0; i < 50; i++)
        {
            monitor.RecordBatch((int)(BatchCapacity * 0.8), BatchCapacity);
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        monitor.ShouldReleaseTraffic.Should().BeFalse();
    }

    // -- Réinitialisation -----------------------------------------------------

    [Fact]
    public void Reset_RemetLaPressionEtLeDecompteAZero()
    {
        // Appele a la reouverture des handles : repartir sur une mesure heritee de la
        // session precedente ferait liberer le trafic immediatement.
        (QueuePressureMonitor monitor, FakeTimeProvider clock) = Create(
            sustainedFor: TimeSpan.FromSeconds(2));

        for (int i = 0; i < 30; i++)
        {
            monitor.RecordBatch(BatchCapacity, BatchCapacity);
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        monitor.ShouldReleaseTraffic.Should().BeTrue();

        monitor.Reset();

        monitor.Pressure.Should().Be(0);
        monitor.ShouldReleaseTraffic.Should().BeFalse();
    }

    // -- Robustesse -----------------------------------------------------------

    [Fact]
    public void Constructeur_SansHorloge_Leve()
    {
        Action act = () => _ = new QueuePressureMonitor(null!, 0.9, TimeSpan.FromSeconds(2));

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(0)]
    [InlineData(1.1)]
    public void SeuilHorsDeZeroUn_Leve(double threshold)
    {
        Action act = () => _ = new QueuePressureMonitor(
            new FakeTimeProvider(), threshold, TimeSpan.FromSeconds(2));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CapaciteDeLotNulle_Leve()
    {
        (QueuePressureMonitor monitor, _) = Create();

        Action act = () => monitor.RecordBatch(packetsRead: 0, batchCapacity: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void LotPlusGrandQueLaCapacite_Leve()
    {
        (QueuePressureMonitor monitor, _) = Create();

        Action act = () => monitor.RecordBatch(packetsRead: 300, batchCapacity: 255);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void PressionResteToujoursEntreZeroEtUn()
    {
        (QueuePressureMonitor monitor, FakeTimeProvider clock) = Create();
        int[] reads = [0, 1, 128, 255, 40, 255, 0, 255];

        foreach (int read in reads)
        {
            monitor.RecordBatch(read, BatchCapacity);
            clock.Advance(TimeSpan.FromMilliseconds(50));

            monitor.Pressure.Should().BeInRange(0, 1);
        }
    }
}
