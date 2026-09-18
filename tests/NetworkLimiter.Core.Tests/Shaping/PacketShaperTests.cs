using Microsoft.Extensions.Time.Testing;
using NetworkLimiter.Core.Classification;
using NetworkLimiter.Core.Shaping;
using NetworkLimiter.Core.Units;

namespace NetworkLimiter.Core.Tests.Shaping;

/// <summary>
/// Mise en forme par paquet — FR-001, FR-003a, FR-003c, FR-040, principe IV.
/// </summary>
/// <remarks>
/// <para>
/// C'est la pièce qui assemble tout, et donc celle où les décisions se combinent : portée
/// réseau, présence d'une règle, sens, protocole, budget disponible, état de la file.
/// </para>
/// <para>
/// Le sort par défaut est <b>laisser passer</b>. Tout ce qui n'est pas explicitement soumis à
/// un plafond traverse sans être touché. C'est le principe IV au grain du paquet : dans le
/// doute, on rend le réseau à l'utilisateur plutôt que de le retenir.
/// </para>
/// </remarks>
public sealed class PacketShaperTests
{
    private static readonly Guid RuleId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherRuleId = new("22222222-2222-2222-2222-222222222222");

    private const long OneMegabytePerSecond = 1_048_576;

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static ShaperRule Rule(long? download = OneMegabytePerSecond, long? upload = null, Guid? id = null) =>
        new(id ?? RuleId,
            download is { } d ? ByteRate.FromBytesPerSecond(d) : null,
            upload is { } u ? ByteRate.FromBytesPerSecond(u) : null);

    private static ShapingRequest Request(
        long token = 1,
        Guid? ruleId = null,
        PacketDirection direction = PacketDirection.Inbound,
        TransportProtocol protocol = TransportProtocol.Tcp,
        NetworkScope scope = NetworkScope.Internet,
        int size = 1500) =>
        new(token, ruleId ?? RuleId, direction, protocol, scope, size);

    private static PacketShaper Shaper(FakeTimeProvider clock, params ShaperRule[] rules)
    {
        var shaper = new PacketShaper(clock);
        shaper.ApplyRules(rules);
        return shaper;
    }

    // -- Laisser passer par défaut --------------------------------------------

    [Theory]
    [InlineData(NetworkScope.Local)]
    [InlineData(NetworkScope.Loopback)]
    public void TraficNonInternet_PasseSansConsommerDeBudget(NetworkScope scope)
    {
        // FR-040 : une sauvegarde vers un NAS ne doit pas epuiser le budget d'une limite
        // destinee a proteger la connexion internet.
        FakeTimeProvider clock = Clock();
        PacketShaper shaper = Shaper(clock, Rule());

        for (int i = 0; i < 1000; i++)
        {
            shaper.Evaluate(Request(scope: scope, size: 1500))
                  .Should().Be(ShapingOutcome.PassThrough);
        }

        // Le budget internet est reste intact.
        shaper.Evaluate(Request()).Should().Be(ShapingOutcome.Send);
    }

    [Fact]
    public void ApplicationSansRegle_PasseSansEtreTouchee()
    {
        FakeTimeProvider clock = Clock();
        PacketShaper shaper = Shaper(clock, Rule());

        // « with » plutôt que le paramètre optionnel : celui-ci retombe sur la règle par
        // défaut quand on lui passe null, ce qui rendait l'absence de règle intestable.
        ShapingRequest withoutRule = Request() with { RuleId = null };

        shaper.Evaluate(withoutRule).Should().Be(ShapingOutcome.PassThrough);
        shaper.Evaluate(Request(ruleId: OtherRuleId)).Should().Be(ShapingOutcome.PassThrough);
    }

    [Fact]
    public void FluxInconnu_PasseSansEtreTouche()
    {
        // Course entre la couche FLOW et la couche NETWORK : le paquet arrive avant que son
        // flux ne soit connu. Il est réinjecté sans limitation, jamais retenu (principe IV).
        FakeTimeProvider clock = Clock();
        PacketShaper shaper = Shaper(clock, Rule(download: 10_240));

        for (int i = 0; i < 1000; i++)
        {
            shaper.Evaluate(Request(token: i) with { RuleId = null })
                  .Should().Be(ShapingOutcome.PassThrough);
        }

        shaper.QueuedPacketCount.Should().Be(0);
    }

    [Fact]
    public void SensNonPlafonne_PasseSansEtreTouche()
    {
        // FR-001 : les deux plafonds sont independants. Une limite en descente ne doit rien
        // changer au trafic montant.
        FakeTimeProvider clock = Clock();
        PacketShaper shaper = Shaper(clock, Rule(download: OneMegabytePerSecond, upload: null));

        for (int i = 0; i < 1000; i++)
        {
            shaper.Evaluate(Request(direction: PacketDirection.Outbound))
                  .Should().Be(ShapingOutcome.PassThrough);
        }
    }

    [Fact]
    public void RegleSansAucunPlafond_NAlloueAucunSeau()
    {
        FakeTimeProvider clock = Clock();
        PacketShaper shaper = Shaper(clock, Rule(download: null, upload: null));

        shaper.ShapedRuleCount.Should().Be(0);
        shaper.Evaluate(Request()).Should().Be(ShapingOutcome.PassThrough);
    }

    // -- Dans le budget -------------------------------------------------------

    [Fact]
    public void DansLeBudget_LePaquetPartMaintenant()
    {
        FakeTimeProvider clock = Clock();
        PacketShaper shaper = Shaper(clock, Rule());

        shaper.Evaluate(Request()).Should().Be(ShapingOutcome.Send);
    }

    // -- Hors budget : temporisation (FR-003c) --------------------------------

    [Fact]
    public void HorsBudgetEtTemporisable_LePaquetEstMisEnFile()
    {
        FakeTimeProvider clock = Clock();
        PacketShaper shaper = Shaper(clock, Rule(download: 10_240));

        // Vide le budget.
        while (shaper.Evaluate(Request(size: 1500)) == ShapingOutcome.Send)
        {
        }

        shaper.Evaluate(Request(size: 1500)).Should().Be(ShapingOutcome.Delay);
        shaper.QueuedPacketCount.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(PacketDirection.Outbound, TransportProtocol.Udp)]
    [InlineData(PacketDirection.Outbound, TransportProtocol.Tcp)]
    [InlineData(PacketDirection.Inbound, TransportProtocol.Tcp)]
    public void CeQuiPeutEtreTemporise_NEstJamaisRejete(
        PacketDirection direction, TransportProtocol protocol)
    {
        // LE test de FR-003c. Un rejet ici signifierait que le produit degrade le trafic
        // alors qu'il pouvait simplement le ralentir.
        FakeTimeProvider clock = Clock();
        long? download = direction == PacketDirection.Inbound ? 10_240 : null;
        long? upload = direction == PacketDirection.Outbound ? 10_240 : null;
        PacketShaper shaper = Shaper(clock, Rule(download, upload));

        for (int i = 0; i < 50; i++)
        {
            shaper.Evaluate(Request(token: i, direction: direction, protocol: protocol, size: 1500))
                  .Should().NotBe(ShapingOutcome.Drop);
        }

        shaper.GetDroppedPackets(RuleId).Should().Be(0);
    }

    // -- Hors budget : rejet (FR-003a) ----------------------------------------

    [Theory]
    [InlineData(TransportProtocol.Udp)]
    [InlineData(TransportProtocol.Other)]
    public void DescendantSansControleDeCongestion_EstRejeteEtCompte(TransportProtocol protocol)
    {
        // Aucune boucle de retroaction : retarder ne ralentit pas l'emetteur. Seul le rejet
        // tient le plafond, et il doit etre compte pour que l'interface l'affiche (FR-003b).
        FakeTimeProvider clock = Clock();
        PacketShaper shaper = Shaper(clock, Rule(download: 10_240));

        while (shaper.Evaluate(Request(protocol: protocol, size: 1500)) == ShapingOutcome.Send)
        {
        }

        shaper.Evaluate(Request(protocol: protocol, size: 1500)).Should().Be(ShapingOutcome.Drop);
        shaper.GetDroppedPackets(RuleId).Should().BeGreaterThan(0);
        shaper.QueuedPacketCount.Should().Be(0, "un paquet rejeté n'occupe pas la file");
    }

    // -- Libération des paquets temporisés ------------------------------------

    [Fact]
    public void LesPaquetsTemporises_SontLiberesQuandLeBudgetRevient()
    {
        FakeTimeProvider clock = Clock();
        PacketShaper shaper = Shaper(clock, Rule(download: 102_400));

        long token = 0;
        while (shaper.Evaluate(Request(token: token++, size: 1500)) != ShapingOutcome.Delay)
        {
        }

        for (int i = 0; i < 5; i++)
        {
            shaper.Evaluate(Request(token: token++, size: 1500));
        }

        int queued = shaper.QueuedPacketCount;
        queued.Should().BeGreaterThan(0);

        clock.Advance(TimeSpan.FromSeconds(1));
        var ready = new List<PendingPacket>();
        shaper.DrainReady(ready);

        ready.Should().NotBeEmpty();
        shaper.QueuedPacketCount.Should().BeLessThan(queued);
    }

    [Fact]
    public void LesPaquetsTemporises_SortentDansLOrdre()
    {
        // Sortir dans le desordre ferait interpreter le desordre comme une perte par TCP,
        // et declencherait des retransmissions inutiles.
        FakeTimeProvider clock = Clock();
        PacketShaper shaper = Shaper(clock, Rule(download: 102_400));

        long token = 0;
        while (shaper.Evaluate(Request(token: token++, size: 1500)) != ShapingOutcome.Delay)
        {
        }

        long firstQueued = token - 1;
        for (int i = 0; i < 5; i++)
        {
            shaper.Evaluate(Request(token: token++, size: 1500));
        }

        clock.Advance(TimeSpan.FromSeconds(10));
        var ready = new List<PendingPacket>();
        shaper.DrainReady(ready);

        ready.Select(p => p.Token).Should().BeInAscendingOrder();
        ready[0].Token.Should().Be(firstQueued);
    }

    [Fact]
    public void UnPaquetNeDoublePasCeuxQuiAttendent()
    {
        // Sans cette regle, un petit paquet arrivant apres un gros passerait devant lui des
        // que le budget suffirait pour le petit — et l'ordre serait casse.
        FakeTimeProvider clock = Clock();
        PacketShaper shaper = Shaper(clock, Rule(download: 102_400));

        long token = 0;
        while (shaper.Evaluate(Request(token: token++, size: 1500)) != ShapingOutcome.Delay)
        {
        }

        shaper.Evaluate(Request(token: 999, size: 64)).Should().Be(ShapingOutcome.Delay);
    }

    // -- Aucun paquet n'est abandonné (principe IV) ---------------------------

    [Fact]
    public void SupprimerUneRegle_LibereSesPaquetsEnAttente()
    {
        // Le paquet a deja ete accepte : le perdre parce que l'utilisateur supprime sa regle
        // serait une perte silencieuse, et le pire moment pour en provoquer une.
        FakeTimeProvider clock = Clock();
        PacketShaper shaper = Shaper(clock, Rule(download: 10_240));

        long token = 0;
        while (shaper.Evaluate(Request(token: token++, size: 1500)) != ShapingOutcome.Delay)
        {
        }

        int queued = shaper.QueuedPacketCount;
        queued.Should().BeGreaterThan(0);

        shaper.ApplyRules([]);

        var released = new List<PendingPacket>();
        shaper.DrainReleased(released).Should().Be(queued);
    }

    [Fact]
    public void RetirerUnPlafondDUnSens_LibereLesPaquetsDeCeSens()
    {
        FakeTimeProvider clock = Clock();
        PacketShaper shaper = Shaper(clock, Rule(download: 10_240));

        long token = 0;
        while (shaper.Evaluate(Request(token: token++, size: 1500)) != ShapingOutcome.Delay)
        {
        }

        shaper.ApplyRules([Rule(download: null, upload: OneMegabytePerSecond)]);

        var ready = new List<PendingPacket>();
        shaper.DrainReady(ready);

        ready.Should().NotBeEmpty("les paquets retenus n'ont plus de raison d'attendre");
    }

    [Fact]
    public void ReleaseAll_NeLaisseAucunPaquetPrisonnier()
    {
        // Appele a la suspension et a la fermeture des handles : aucun paquet ne doit rester
        // dans une file dont plus personne ne s'occupe.
        FakeTimeProvider clock = Clock();
        PacketShaper shaper = Shaper(clock, Rule(download: 10_240), Rule(download: 10_240, id: OtherRuleId));

        long token = 0;
        while (shaper.Evaluate(Request(token: token++, size: 1500)) != ShapingOutcome.Delay)
        {
        }

        while (shaper.Evaluate(Request(token: token++, ruleId: OtherRuleId, size: 1500)) != ShapingOutcome.Delay)
        {
        }

        int queued = shaper.QueuedPacketCount;
        var released = new List<PendingPacket>();
        shaper.ReleaseAll(released);

        released.Should().HaveCount(queued);
        shaper.QueuedPacketCount.Should().Be(0);
        shaper.ShapedRuleCount.Should().Be(0);
    }

    // -- Modification de plafond à chaud --------------------------------------

    [Fact]
    public void ChangerUnPlafond_NeRecreePasLeSeau()
    {
        // Recreer le seau remettrait les jetons a la capacite : chaque modification
        // laisserait passer une rafale, et l'utilisateur qui ajuste son plafond verrait des
        // pics qu'il n'a pas demandes.
        FakeTimeProvider clock = Clock();
        PacketShaper shaper = Shaper(clock, Rule(download: 10_240));

        while (shaper.Evaluate(Request(size: 1500)) == ShapingOutcome.Send)
        {
        }

        shaper.ApplyRules([Rule(download: 20_480)]);

        shaper.Evaluate(Request(size: 1500)).Should().NotBe(ShapingOutcome.Send);
    }

    [Fact]
    public void AjouterUnPlafondSurLAutreSens_NAffectePasLePremier()
    {
        FakeTimeProvider clock = Clock();
        PacketShaper shaper = Shaper(clock, Rule(download: OneMegabytePerSecond));

        shaper.ApplyRules([Rule(download: OneMegabytePerSecond, upload: 10_240)]);

        shaper.Evaluate(Request(direction: PacketDirection.Inbound)).Should().Be(ShapingOutcome.Send);
    }

    // -- Robustesse -----------------------------------------------------------

    [Fact]
    public void Constructeur_SansHorloge_Leve()
    {
        Action act = () => _ = new PacketShaper(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ApplyRules_SansRegles_Leve()
    {
        var shaper = new PacketShaper(Clock());

        Action act = () => shaper.ApplyRules(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PaquetDeTailleNonPositive_Leve(int size)
    {
        PacketShaper shaper = Shaper(Clock(), Rule());

        Action act = () => shaper.Evaluate(Request(size: size));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
