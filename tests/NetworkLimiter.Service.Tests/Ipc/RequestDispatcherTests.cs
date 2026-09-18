using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Contracts.Serialization;
using NetworkLimiter.Service.Interception;
using NetworkLimiter.Service.Ipc;
using NetworkLimiter.Service.Ipc.Handlers;
using NetworkLimiter.Service.Persistence;
using NetworkLimiter.Service.Safety;
using Serilog;
using NetworkLimiter.Service.Health;
using Xunit;

namespace NetworkLimiter.Service.Tests.Ipc;

/// <summary>
/// Vérifie l'acheminement des requêtes et, surtout, ce qui est <b>refusé</b>.
/// </summary>
/// <remarks>
/// Le répartiteur est la frontière de privilège du produit. Une erreur d'autorisation ici ne
/// produirait aucun symptôme visible : tout marcherait, simplement un utilisateur sans droits
/// pourrait modifier les limites de la machine. C'est la définition d'une faille silencieuse,
/// donc les cas de refus sont testés plus densément que les cas de succès.
/// </remarks>
public sealed class RequestDispatcherTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "nl-dispatch-" + Guid.NewGuid().ToString("N"));

    private readonly RuleStore _rules;
    private readonly RequestDispatcher _dispatcher;

    public RequestDispatcherTests()
    {
        Directory.CreateDirectory(_directory);

        var clock = new FakeTimeProvider();
        var store = new ConfigStore(Path.Combine(_directory, "config.json"), clock);

        _rules = new RuleStore(store, PersistedConfig.CreateDefault());

        var coordinator = new ShapingCoordinator(
            new Core.Shaping.PacketShaper(clock),
            new Core.Rules.RuleResolver(),
            new AlwaysExistsProbe());

        Suspension = new SuspensionController(coordinator, _rules);

        Health = new ServiceHealthProvider(
            coordinator,
            Suspension,
            () => _rules.ActiveRules,
            () => RunningPaths,
            new NoAdapters(),
            () => null,
            "1.0.0");

        _dispatcher = new RequestDispatcher(
            new RuleHandlers(_rules, Serilog.Core.Logger.None),
            new ServiceStateProvider(_rules, coordinator, () => RunningPaths),
            Health,
            Suspension,
            Serilog.Core.Logger.None);
    }

    private SuspensionController Suspension { get; }

    private ServiceHealthProvider Health { get; }

    [Fact]
    public void LEtatDeSante_EstLisibleSansElevation()
    {
        // Un utilisateur sans droits doit pouvoir DIAGNOSTIQUER. Exiger une elevation pour
        // lire l'etat de sante le priverait de toute explication au moment ou il en a besoin.
        MessageEnvelope response = _dispatcher.Dispatch(
            MessageEnvelope.CreateRequest(MessageTypes.GetHealth), callerIsElevated: false);

        response.Ok.Should().BeTrue();

        HealthResultPayload health = MessageSerializer.ReadPayload<HealthResultPayload>(response);
        health.ServiceVersion.Should().Be("1.0.0");
    }

    [Fact]
    public void LEtatDeSante_DistingueLaSuspensionDeLIndisponibilite()
    {
        Suspension.Suspend();

        HealthResultPayload health = MessageSerializer.ReadPayload<HealthResultPayload>(
            _dispatcher.Dispatch(
                MessageEnvelope.CreateRequest(MessageTypes.GetHealth), callerIsElevated: false));

        // Deux causes tres differentes d'« aucune limite appliquee ». Les confondre enverrait
        // l'utilisateur reinstaller un pilote qui fonctionne parfaitement.
        health.Suspended.Should().BeTrue();
        health.InterceptionActive.Should().BeFalse("rien ne s'applique pendant une suspension");
        health.DriverLoaded.Should().BeTrue("le pilote n'y est pour rien");
    }

    [Fact]
    public void LEtatDeSante_CompteLesReglesInactives()
    {
        _dispatcher.Dispatch(
            MessageEnvelope.CreateRequest(MessageTypes.UpsertRule, BuildUpsert(@"c:\absent\rien.exe")),
            callerIsElevated: true);

        HealthResultPayload health = MessageSerializer.ReadPayload<HealthResultPayload>(
            _dispatcher.Dispatch(
                MessageEnvelope.CreateRequest(MessageTypes.GetHealth), callerIsElevated: false));

        health.InactiveRuleCount.Should().Be(1);
        health.ActiveRuleCount.Should().Be(0);
    }

    private sealed class NoAdapters : NetworkLimiter.Service.Health.INetworkAdapterSource
    {
        public IReadOnlyList<NetworkLimiter.Service.Health.NetworkAdapter> GetAdapters() => [];
    }

    [Fact]
    public void LaSuspension_ExigeUneElevation()
    {
        MessageEnvelope response = _dispatcher.Dispatch(
            MessageEnvelope.CreateRequest(
                MessageTypes.SetSuspended, new SetSuspendedPayload { Suspended = true }),
            callerIsElevated: false);

        response.Error!.Code.Should().Be(ErrorCode.ElevationRequired);

        // Le refus doit precede l'effet : suspendre puis refuser laisserait le reseau sans
        // limites au profit d'un appelant sans droits.
        Suspension.IsSuspended.Should().BeFalse();
    }

    [Fact]
    public void LaSuspension_EstAppliquee_EtDiffusee()
    {
        int broadcasts = 0;
        _dispatcher.StateWritten += (_, _) => broadcasts++;

        _dispatcher.Dispatch(
            MessageEnvelope.CreateRequest(
                MessageTypes.SetSuspended, new SetSuspendedPayload { Suspended = true }),
            callerIsElevated: true)
            .Ok.Should().BeTrue();

        Suspension.IsSuspended.Should().BeTrue();
        broadcasts.Should().Be(1, "toutes les interfaces doivent voir que la limitation est levée");
    }

    [Fact]
    public void LaReprise_RepasseParLeMemeChemin()
    {
        _dispatcher.Dispatch(
            MessageEnvelope.CreateRequest(
                MessageTypes.SetSuspended, new SetSuspendedPayload { Suspended = true }),
            callerIsElevated: true);

        _dispatcher.Dispatch(
            MessageEnvelope.CreateRequest(
                MessageTypes.SetSuspended, new SetSuspendedPayload { Suspended = false }),
            callerIsElevated: true);

        Suspension.IsSuspended.Should().BeFalse();
    }

    [Fact]
    public void LEtatRendu_PorteLaSuspension()
    {
        Suspension.Suspend();

        MessageEnvelope response = _dispatcher.Dispatch(
            MessageEnvelope.CreateRequest(MessageTypes.GetState), callerIsElevated: false);

        // Sans ce champ, une interface afficherait des règles « actives » alors qu'aucune ne
        // s'applique — le mensonge le plus coûteux que cet outil puisse produire.
        MessageSerializer.ReadPayload<GetStateResultPayload>(response)
            .Suspended.Should().BeTrue();
    }

    private static IReadOnlySet<string> RunningPaths { get; } =
        new HashSet<string>(StringComparer.Ordinal) { @"c:\jeux\jeu.exe" };

    [Theory]
    [InlineData(MessageTypes.UpsertRule)]
    [InlineData(MessageTypes.DeleteRule)]
    [InlineData(MessageTypes.SetRuleEnabled)]
    [InlineData(MessageTypes.SetGlobalLimit)]
    [InlineData(MessageTypes.SetSuspended)]
    [InlineData(MessageTypes.ImportConfig)]
    public void UneEcritureSansElevation_EstRefusee_AvecUnCodeActionnable(string type)
    {
        MessageEnvelope response = _dispatcher.Dispatch(
            MessageEnvelope.CreateRequest(type), callerIsElevated: false);

        response.Ok.Should().NotBe(true);
        response.Error!.Code.Should().Be(ErrorCode.ElevationRequired);

        // Le message doit dire QUOI FAIRE. « Accès refusé » laisserait l'utilisateur sans
        // recours devant une commande qui existe pourtant dans son interface (FR-034b).
        response.Error.Message.Should().Contain("administrateur");
    }

    [Fact]
    public void UneEcritureSansElevation_NeModifieRien()
    {
        _dispatcher.Dispatch(
            MessageEnvelope.CreateRequest(MessageTypes.UpsertRule, BuildUpsert()),
            callerIsElevated: false);

        // Le refus doit precede le traitement, pas le suivre : une validation faite puis
        // annulee laisserait des traces (fichier ecrit, evenement emis).
        _rules.ActiveRules.Should().BeEmpty();
    }

    [Fact]
    public void UneLecture_EstAutorisee_SansElevation()
    {
        MessageEnvelope response = _dispatcher.Dispatch(
            MessageEnvelope.CreateRequest(MessageTypes.GetState), callerIsElevated: false);

        response.Ok.Should().BeTrue();

        GetStateResultPayload state = MessageSerializer.ReadPayload<GetStateResultPayload>(response);
        state.Profiles.Should().HaveCount(1);
    }

    [Fact]
    public void UneEcritureElevee_EstAppliquee_EtDeclencheUneDiffusion()
    {
        int broadcasts = 0;
        _dispatcher.StateWritten += (_, _) => broadcasts++;

        MessageEnvelope response = _dispatcher.Dispatch(
            MessageEnvelope.CreateRequest(MessageTypes.UpsertRule, BuildUpsert()),
            callerIsElevated: true);

        response.Ok.Should().BeTrue();
        _rules.ActiveRules.Should().HaveCount(1);
        broadcasts.Should().Be(1);
    }

    [Fact]
    public void UneEcritureRefusee_NeDeclenchePasDeDiffusion()
    {
        int broadcasts = 0;
        _dispatcher.StateWritten += (_, _) => broadcasts++;

        // Identifiant inexistant : l'ecriture echoue au niveau du depot, pas de l'autorisation.
        _dispatcher.Dispatch(
            MessageEnvelope.CreateRequest(
                MessageTypes.DeleteRule, new DeleteRulePayload { RuleId = Guid.NewGuid() }),
            callerIsElevated: true);

        // Diffuser apres un refus ferait rafraichir toutes les interfaces pour un etat
        // inchange, et laisserait croire que quelque chose a bouge.
        broadcasts.Should().Be(0);
    }

    [Fact]
    public void UnTypeInconnu_EstRefuse_JamaisTraiteEnSilence()
    {
        MessageEnvelope response = _dispatcher.Dispatch(
            MessageEnvelope.CreateRequest("FaisMoiConfiance"), callerIsElevated: true);

        response.Ok.Should().NotBe(true);
        response.Error!.Code.Should().Be(ErrorCode.ValidationFailed);
    }

    [Fact]
    public void UneChargeUtileMalFormee_EstRefusee_SansTuerLaConnexion()
    {
        // Charge utile d'un AUTRE message : structurellement du JSON, semantiquement absurde.
        MessageEnvelope response = _dispatcher.Dispatch(
            MessageEnvelope.CreateRequest(
                MessageTypes.UpsertRule, new DeleteRulePayload { RuleId = Guid.NewGuid() }),
            callerIsElevated: true);

        response.Ok.Should().NotBe(true);
        response.Error!.Code.Should().Be(ErrorCode.ValidationFailed);
    }

    [Fact]
    public void LEtatRendu_PorteLaRaisonDInactivite_EtLeNombreDeProcessus()
    {
        _dispatcher.Dispatch(
            MessageEnvelope.CreateRequest(MessageTypes.UpsertRule, BuildUpsert(@"c:\bureau\absent.exe")),
            callerIsElevated: true);

        MessageEnvelope response = _dispatcher.Dispatch(
            MessageEnvelope.CreateRequest(MessageTypes.GetState), callerIsElevated: false);

        RuleStateDto rule = MessageSerializer
            .ReadPayload<GetStateResultPayload>(response)
            .Profiles[0].Rules[0];

        // FR-026 : une regle definie mais inactive doit TOUJOURS pouvoir dire pourquoi.
        rule.Status.Should().Be(RuleApplicationStatus.Inactive);
        rule.InactiveReason.Should().Be(RuleInactiveReasonDto.ApplicationNotRunning);
        rule.MatchedProcessCount.Should().Be(0);
    }

    [Fact]
    public void LeNombreDeProcessus_CompteLesProcessusReellementEnCours()
    {
        _dispatcher.Dispatch(
            MessageEnvelope.CreateRequest(MessageTypes.UpsertRule, BuildUpsert(@"c:\jeux\jeu.exe")),
            callerIsElevated: true);

        MessageEnvelope response = _dispatcher.Dispatch(
            MessageEnvelope.CreateRequest(MessageTypes.GetState), callerIsElevated: false);

        RuleStateDto rule = MessageSerializer
            .ReadPayload<GetStateResultPayload>(response)
            .Profiles[0].Rules[0];

        rule.Status.Should().Be(RuleApplicationStatus.Active);
        rule.MatchedProcessCount.Should().Be(1);
    }

    private static UpsertRulePayload BuildUpsert(string path = @"c:\jeux\jeu.exe") =>
        new()
        {
            Rule = new RuleDto
            {
                Id = Guid.NewGuid(),
                Target = new AppIdentityDto
                {
                    ExecutablePath = path,
                    ExecutableName = Path.GetFileName(path),
                    DisplayName = Path.GetFileNameWithoutExtension(path),
                },
                DownloadBytesPerSecond = 204_800,
                UploadBytesPerSecond = null,
                Enabled = true,
                ExemptFromGlobal = false,
            },
        };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Nettoyage de confort : un fichier encore ouvert ne doit pas faire echouer un
            // test qui a par ailleurs reussi.
        }
    }

    private sealed class AlwaysExistsProbe : IPathExistenceProbe
    {
        public bool Exists(string executablePath) => true;
    }
}
