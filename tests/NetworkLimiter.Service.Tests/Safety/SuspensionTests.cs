using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Core.Classification;
using NetworkLimiter.Core.Rules;
using NetworkLimiter.Core.Shaping;
using NetworkLimiter.Core.Units;
using NetworkLimiter.Service.Interception;
using NetworkLimiter.Service.Health;
using NetworkLimiter.Service.Safety;
using Xunit;

namespace NetworkLimiter.Service.Tests.Safety;

/// <summary>
/// Suspension globale (FR-024).
/// </summary>
/// <remarks>
/// <para>
/// C'est le bouton d'arrêt d'urgence du produit : l'utilisateur constate que quelque chose ne
/// marche plus et veut rendre son réseau <b>maintenant</b>, sans avoir à comprendre laquelle de
/// ses règles est en cause. Deux propriétés doivent tenir ensemble, et c'est leur conjonction
/// qui est délicate — <b>toutes les limites tombent</b>, et <b>aucune règle n'est perdue</b>.
/// </para>
/// <para>
/// Lever les limites en supprimant les règles serait bien plus simple à écrire, et ferait de la
/// suspension un piège : l'utilisateur perdrait sa configuration en cherchant à dépanner sa
/// connexion, ce qu'aucun message ne l'aurait prévenu de risquer.
/// </para>
/// </remarks>
public sealed class SuspensionTests
{
    private static readonly Guid RuleId = Guid.NewGuid();

    [Fact]
    public void LaSuspension_LeveToutesLesLimites()
    {
        (SuspensionController controller, PacketShaper shaper) = Build();

        Evaluate(shaper).Should().NotBe(ShapingOutcome.PassThrough, "une limite s'applique");

        controller.Suspend();

        // Le paquet doit desormais traverser sans consommer de budget : c'est PassThrough, pas
        // Send. Send signifierait « dans le budget », donc une limite encore active.
        Evaluate(shaper).Should().Be(ShapingOutcome.PassThrough);
    }

    [Fact]
    public void LaSuspension_ConserveLesRegles()
    {
        (SuspensionController controller, _) = Build();

        controller.Suspend();

        // FR-024 : les regles sont CONSERVEES. Les supprimer ferait perdre sa configuration a
        // un utilisateur qui cherchait seulement a depanner sa connexion.
        controller.Rules.ActiveRules.Should().ContainSingle()
            .Which.Id.Should().Be(RuleId);
    }

    [Fact]
    public void LaReprise_RetablitLesLimites()
    {
        (SuspensionController controller, PacketShaper shaper) = Build();

        controller.Suspend();
        controller.Resume();

        Evaluate(shaper).Should().NotBe(
            ShapingOutcome.PassThrough,
            "la reprise doit réappliquer les règles conservées, sans intervention de l'utilisateur");
    }

    [Fact]
    public void LesPaquetsEnAttente_SontLiberes_JamaisAbandonnes()
    {
        (SuspensionController controller, PacketShaper shaper) = Build();

        // Epuise le credit pour que des paquets soient reellement en file.
        for (int index = 0; index < 40; index++)
        {
            Evaluate(shaper);
        }

        shaper.QueuedPacketCount.Should().BePositive("des paquets doivent attendre");

        controller.Suspend();

        var released = new List<PendingPacket>();
        controller.Coordinator.ReleasedPackets.Should().NotBeEmpty(
            "les paquets retenus doivent ressortir : les abandonner perdrait du trafic déjà " +
            "accepté, au moment précis où l'on cesse de limiter");

        shaper.DrainReleased(released);
        shaper.QueuedPacketCount.Should().Be(0, "plus aucune file ne doit rester sans personne pour s'en occuper");
    }

    [Fact]
    public void LEtatDeSuspension_EstIdempotent()
    {
        (SuspensionController controller, PacketShaper shaper) = Build();

        controller.Suspend();
        controller.Suspend();

        controller.IsSuspended.Should().BeTrue();
        Evaluate(shaper).Should().Be(ShapingOutcome.PassThrough);

        controller.Resume();
        controller.Resume();

        controller.IsSuspended.Should().BeFalse();
    }

    [Fact]
    public void LaSuspension_EstSignalee_PourChaqueRegle()
    {
        (SuspensionController controller, _) = Build();

        controller.Suspend();

        // FR-026 : une regle inactive doit TOUJOURS pouvoir dire pourquoi. « Suspendu »
        // l'emporte sur toute autre cause : c'est la plus englobante, et envoyer l'utilisateur
        // chercher ailleurs serait le pire des diagnostics.
        controller.Coordinator
            .GetInactiveReason(controller.Rules.ActiveRules[0], NoRunningProcess)
            .Should().Be(RuleInactiveReason.GloballySuspended);
    }

    [Fact]
    public void LaSuspension_EstNotifiee_UneSeuleFoisParChangement()
    {
        (SuspensionController controller, _) = Build();

        int changes = 0;
        controller.Changed += (_, _) => changes++;

        controller.Suspend();
        controller.Suspend();
        controller.Resume();

        // Notifier a chaque appel ferait rediffuser l'etat a toutes les interfaces pour rien,
        // et clignoter leur affichage sans qu'aucune valeur n'ait change.
        changes.Should().Be(2);
    }

    private static IReadOnlySet<string> NoRunningProcess { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    private static ShapingOutcome Evaluate(PacketShaper shaper) =>
        shaper.Evaluate(new ShapingRequest(
            Token: Random.Shared.NextInt64(),
            RuleId: RuleId,
            Direction: PacketDirection.Inbound,
            Protocol: TransportProtocol.Tcp,
            Scope: NetworkScope.Internet,
            SizeBytes: 1400));

    private static (SuspensionController Controller, PacketShaper Shaper) Build()
    {
        var clock = new FakeTimeProvider();
        var shaper = new PacketShaper(clock);
        var coordinator = new ShapingCoordinator(shaper, new RuleResolver(), new AlwaysExistsProbe());

        var store = new StubRuleSource(
        [
            new RuleDto
            {
                Id = RuleId,
                Target = new AppIdentityDto
                {
                    ExecutablePath = @"c:\jeux\jeu.exe",
                    ExecutableName = "jeu.exe",
                    DisplayName = "Jeu",
                },
                DownloadBytesPerSecond = ByteRate.MinBytesPerSecond,
                UploadBytesPerSecond = null,
                Enabled = true,
                ExemptFromGlobal = false,
            },
        ]);

        coordinator.Apply(store.ActiveRules);

        return (new SuspensionController(coordinator, store), shaper);
    }

    private sealed class StubRuleSource(IReadOnlyList<RuleDto> rules) : IRuleSource
    {
        public IReadOnlyList<RuleDto> ActiveRules { get; } = rules;
    }

    private sealed class AlwaysExistsProbe : IPathExistenceProbe
    {
        public bool Exists(string executablePath) => true;
    }
}
