using Microsoft.Extensions.Time.Testing;
using NetworkLimiter.Core.Shaping;
using NetworkLimiter.Core.Units;

namespace NetworkLimiter.Core.Tests.Shaping;

/// <summary>
/// Seau à jetons — FR-003, data-model.md.
/// </summary>
/// <remarks>
/// <para>
/// C'est le cœur du produit, et la partie dont l'erreur est la plus invisible : un shaper faux
/// « marche » tout en délivrant le mauvais débit. Seule une mesure face à une valeur attendue
/// le prouve — d'où des tests en <b>horloge virtuelle</b>, sans une seule attente réelle
/// (principe III).
/// </para>
/// <para>
/// Les invariants vérifiés ici sont ceux de data-model.md, plus un que la conception impose et
/// qu'il serait facile d'oublier : un paquet plus grand que la capacité du seau doit finir par
/// passer, sinon le flux se bloque définitivement.
/// </para>
/// </remarks>
public sealed class TokenBucketTests
{
    private const long OneMegabytePerSecond = 1_048_576;

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static TokenBucket Bucket(long bytesPerSecond, FakeTimeProvider clock, TokenBucket? parent = null) =>
        new(ByteRate.FromBytesPerSecond(bytesPerSecond), clock, parent);

    /// <summary>Consomme tout ce qui peut l'être et rend le volume écoulé sur une durée.</summary>
    private static long DrainOver(TokenBucket bucket, FakeTimeProvider clock, TimeSpan duration, int packetSize)
    {
        long total = 0;
        TimeSpan step = TimeSpan.FromMilliseconds(1);

        for (TimeSpan elapsed = TimeSpan.Zero; elapsed < duration; elapsed += step)
        {
            while (bucket.TryConsume(packetSize))
            {
                total += packetSize;
            }

            clock.Advance(step);
        }

        return total;
    }

    // -- Bornes des jetons ----------------------------------------------------

    [Fact]
    public void LesJetons_RestentDansLesBornes()
    {
        FakeTimeProvider clock = Clock();
        TokenBucket bucket = Bucket(OneMegabytePerSecond, clock);

        for (int i = 0; i < 100; i++)
        {
            bucket.TryConsume(1500);
            clock.Advance(TimeSpan.FromMilliseconds(7));

            bucket.AvailableTokens.Should().BeInRange(0, bucket.CapacityBytes);
        }
    }

    [Fact]
    public void LeSeauNeDebordePas_MemeApresUneLongueInactivite()
    {
        // Sans plafonnement, une heure d'inactivite accumulerait 3,6 Go de jetons et la
        // premiere rafale passerait sans aucune limite.
        FakeTimeProvider clock = Clock();
        TokenBucket bucket = Bucket(OneMegabytePerSecond, clock);

        clock.Advance(TimeSpan.FromHours(1));
        bucket.Refill();

        bucket.AvailableTokens.Should().Be(bucket.CapacityBytes);
    }

    [Fact]
    public void Capacite_SuitLaFormuleDuModeleDeDonnees()
    {
        FakeTimeProvider clock = Clock();

        // A 1 Mo/s : 100 ms de debit = 104 857 octets, superieur a 2 x MTU.
        Bucket(OneMegabytePerSecond, clock).CapacityBytes.Should().Be(104_857);

        // A 10 Ko/s : 100 ms = 1 024 octets, inferieur a 2 x MTU, donc le plancher s'applique.
        Bucket(10_240, clock).CapacityBytes.Should().Be(2 * TokenBucket.MaximumTransmissionUnit);
    }

    // -- Débit tenu -----------------------------------------------------------

    [Theory]
    [InlineData(10_240)]        // 10 Ko/s, borne basse
    [InlineData(102_400)]       // 100 Ko/s
    [InlineData(1_048_576)]     // 1 Mo/s
    [InlineData(10_485_760)]    // 10 Mo/s
    [InlineData(104_857_600)]   // 100 Mo/s
    public void LeDebitTenuSurDixSecondes_EstDansLesDixPourCent(long rate)
    {
        // FR-003 : ecart mesure n'excedant pas 10 %, moyenne sur une fenetre de 10 secondes.
        FakeTimeProvider clock = Clock();
        TokenBucket bucket = Bucket(rate, clock);

        long transferred = DrainOver(bucket, clock, TimeSpan.FromSeconds(10), packetSize: 1024);

        long expected = rate * 10;
        transferred.Should().BeInRange((long)(expected * 0.9), (long)(expected * 1.1));
    }

    [Fact]
    public void SurUneLongueDuree_LeDebitMoyenConverge()
    {
        FakeTimeProvider clock = Clock();
        TokenBucket bucket = Bucket(OneMegabytePerSecond, clock);

        long transferred = DrainOver(bucket, clock, TimeSpan.FromSeconds(60), packetSize: 1500);

        long expected = OneMegabytePerSecond * 60;
        transferred.Should().BeInRange((long)(expected * 0.95), (long)(expected * 1.05));
    }

    [Fact]
    public void SansJetons_RienNePasse()
    {
        FakeTimeProvider clock = Clock();
        TokenBucket bucket = Bucket(OneMegabytePerSecond, clock);

        while (bucket.TryConsume(1500))
        {
            // Vide le seau.
        }

        bucket.TryConsume(1500).Should().BeFalse();
    }

    // -- Paquet plus grand que la capacité ------------------------------------

    [Fact]
    public void UnPaquetPlusGrandQueLaCapacite_FinitParPasser()
    {
        // Invariant de survie. A 10 Ko/s la capacite vaut 3 000 octets ; un segment de
        // 65 535 octets — courant avec le deport de segmentation — ne tiendrait jamais
        // dedans. Sans traitement special, ce flux se bloquerait DEFINITIVEMENT, et
        // l'utilisateur verrait une application simplement cesser de fonctionner.
        FakeTimeProvider clock = Clock();
        TokenBucket bucket = Bucket(10_240, clock);
        const int largePacket = 65_535;

        largePacket.Should().BeGreaterThan((int)bucket.CapacityBytes);

        clock.Advance(TimeSpan.FromSeconds(10));   // le seau se remplit à ras bord

        bucket.TryConsume(largePacket).Should().BeTrue();
    }

    [Fact]
    public void UnPaquetSurdimensionne_VideEntierementLeSeau()
    {
        // Il paie ce qu'il peut : le debit moyen reste borne malgre le depassement ponctuel.
        FakeTimeProvider clock = Clock();
        TokenBucket bucket = Bucket(10_240, clock);
        clock.Advance(TimeSpan.FromSeconds(10));

        bucket.TryConsume(65_535).Should().BeTrue();

        bucket.AvailableTokens.Should().Be(0);
        bucket.TryConsume(65_535).Should().BeFalse();
    }

    // -- Changement de débit à chaud ------------------------------------------

    [Fact]
    public void ChangerLeDebit_NeRemetPasLesJetonsAZero()
    {
        // L'utilisateur qui ajuste un plafond ne doit pas voir sa connexion se figer une
        // seconde a chaque modification.
        FakeTimeProvider clock = Clock();
        TokenBucket bucket = Bucket(OneMegabytePerSecond, clock);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        bucket.Refill();

        double before = bucket.AvailableTokens;
        before.Should().BeGreaterThan(0);

        bucket.Rate = ByteRate.FromBytesPerSecond(OneMegabytePerSecond * 2);

        bucket.AvailableTokens.Should().Be(before);
    }

    [Fact]
    public void ReduireLeDebit_RamenneLesJetonsSousLaNouvelleCapacite()
    {
        FakeTimeProvider clock = Clock();
        TokenBucket bucket = Bucket(104_857_600, clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        bucket.Refill();

        bucket.Rate = ByteRate.FromBytesPerSecond(10_240);

        bucket.AvailableTokens.Should().BeLessThanOrEqualTo(bucket.CapacityBytes);
    }

    [Fact]
    public void ApresChangementDeDebit_LeNouveauDebitEstTenu()
    {
        FakeTimeProvider clock = Clock();
        TokenBucket bucket = Bucket(OneMegabytePerSecond, clock);
        DrainOver(bucket, clock, TimeSpan.FromSeconds(2), 1024);

        bucket.Rate = ByteRate.FromBytesPerSecond(102_400);
        long transferred = DrainOver(bucket, clock, TimeSpan.FromSeconds(10), 1024);

        long expected = 102_400L * 10;
        transferred.Should().BeInRange((long)(expected * 0.9), (long)(expected * 1.2));
    }

    // -- Horloge qui recule ---------------------------------------------------

    /// <summary>Horloge délibérément fautive : ses horodatages peuvent reculer.</summary>
    /// <remarks>
    /// <c>TimeProvider.GetTimestamp()</c> est monotone par contrat, et <c>FakeTimeProvider</c>
    /// refuse d'ailleurs d'avancer en arrière. Un recul ne devrait donc jamais survenir — mais
    /// le seau s'en protège quand même, et cette protection ne serait vérifiée par aucun test
    /// sans une horloge qui viole volontairement le contrat.
    ///
    /// Ce n'est pas de la paranoïa gratuite : un <c>TimeProvider</c> fourni par erreur, une
    /// pile de virtualisation qui resynchronise son compteur, et le seau se retrouverait soit
    /// à distribuer des jetons gratuits, soit figé pour toujours.
    /// </remarks>
    private sealed class RewindingTimeProvider : TimeProvider
    {
        private long _timestamp;

        public void Advance(TimeSpan delta) =>
            _timestamp += (long)(delta.TotalSeconds * TimestampFrequency);

        public override long GetTimestamp() => _timestamp;
    }

    [Fact]
    public void UneHorlogeQuiRecule_NeCreePasDeJetons()
    {
        // Creer des jetons a partir d'un temps ecoule negatif laisserait passer une rafale
        // sans aucune limite.
        var clock = new RewindingTimeProvider();
        var bucket = new TokenBucket(ByteRate.FromBytesPerSecond(OneMegabytePerSecond), clock);

        while (bucket.TryConsume(1500))
        {
            // Vide le seau. Il reste un reliquat inferieur a la taille d'un paquet : la
            // propriete a verifier n'est donc pas « zero jeton » mais « aucun jeton cree ».
        }

        double before = bucket.AvailableTokens;

        clock.Advance(TimeSpan.FromMilliseconds(-500));
        bucket.Refill();

        bucket.AvailableTokens.Should().Be(before, "un temps écoulé négatif ne doit créer aucun jeton");
        bucket.TryConsume(1500).Should().BeFalse();
    }

    [Fact]
    public void UneHorlogeQuiRecule_NeBloquePasLeSeauDefinitivement()
    {
        // L'autre defaillance possible, et la plus grave : conserver l'horodatage du futur,
        // ce qui empecherait tout rechargement ulterieur et couperait l'application sans
        // qu'aucune limite ne soit en cause.
        var clock = new RewindingTimeProvider();
        var bucket = new TokenBucket(ByteRate.FromBytesPerSecond(OneMegabytePerSecond), clock);

        while (bucket.TryConsume(1500))
        {
            // Vide le seau.
        }

        clock.Advance(TimeSpan.FromMilliseconds(-500));
        bucket.Refill();
        clock.Advance(TimeSpan.FromSeconds(1));

        bucket.TryConsume(1500).Should().BeTrue();
    }

    // -- Robustesse -----------------------------------------------------------

    [Fact]
    public void Constructeur_SansHorloge_Leve()
    {
        Action act = () => _ = new TokenBucket(ByteRate.FromBytesPerSecond(OneMegabytePerSecond), null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConsommerUnVolumeNonPositif_Leve(int bytes)
    {
        FakeTimeProvider clock = Clock();
        TokenBucket bucket = Bucket(OneMegabytePerSecond, clock);

        Action act = () => bucket.TryConsume(bytes);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AucunTestNUtiliseLHorlogeReelle()
    {
        // Garde-fou du principe III, verifie ici par construction : toute la classe est
        // pilotee par TimeProvider, et les tests n'instancient que FakeTimeProvider.
        typeof(TokenBucket).GetConstructors()
            .Should().OnlyContain(c => c.GetParameters().Any(p => p.ParameterType == typeof(TimeProvider)));
    }
}
