using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Core.Rules;
using NetworkLimiter.Core.Shaping;
using NetworkLimiter.Service.FlowTable;
using NetworkLimiter.Service.Health;
using NetworkLimiter.Service.Interception;
using NetworkLimiter.Service.Persistence;
using NetworkLimiter.Service.ProcessIdentity;
using NetworkLimiter.Service.Resilience;
using NetworkLimiter.Service.Safety;
using Serilog.Events;
using ILogger = Serilog.ILogger;

namespace NetworkLimiter.Service;

/// <summary>
/// Assemble et fait vivre l'interception pour la durée du service.
/// </summary>
/// <remarks>
/// <para>
/// C'est ici que les pièces se rencontrent : configuration persistée, règles, table de flux,
/// identité de processus, mise en forme, boucles d'interception. Chacune est testée seule ;
/// ce worker est le seul endroit où leur assemblage existe.
/// </para>
/// <para>
/// Toute défaillance mène au mode dégradé, jamais à un arrêt silencieux : le service reste
/// vivant pour pouvoir <b>expliquer</b> ce qui ne va pas, et le trafic reste libre (principes
/// IV et VI).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class InterceptionWorker : BackgroundService
{
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FlowRetention = TimeSpan.FromMinutes(5);

    private readonly ILogger _log;
    private readonly TimeProvider _clock;

    private ShapingPipeline? _pipeline;
    private ShapingCoordinator? _coordinator;
    private ServiceStateMachine? _state;
    private NetworkChangeMonitor? _networkMonitor;
    private FlowTable.FlowTable? _flowTable;
    private RuleStore? _ruleStore;
    private string? _degradedReason;
    private long[] _lastCounters = [];

    public InterceptionWorker(ILogger log, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);

        _log = log;
        _clock = clock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!TryStart())
        {
            // Mode degrade : aucune limite n'est appliquee, mais le service reste vivant.
            // S'arreter priverait l'interface de tout moyen d'expliquer la situation a
            // l'utilisateur (principe VI).
            _log.Warning("Service en mode dégradé : {Raison}", _degradedReason);

            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(MaintenanceInterval, stoppingToken).ConfigureAwait(false);
                RunMaintenance();
            }
        }
        catch (OperationCanceledException)
        {
            // Arret demande : la liberation se fait dans StopAsync.
        }
    }

    private bool TryStart()
    {
        try
        {
            // Les ACL du repertoire de donnees sont un controle de securite, pas un reglage :
            // s'il est impossible de les poser, on ne limite pas. Mieux vaut ne pas limiter
            // que limiter derriere un verrou qu'on sait contournable.
            ConfigAcl.AclCheckResult acl = ConfigAcl.EnsureSecured(ServicePaths.RootDirectory);

            if (!acl.WasCorrect)
            {
                if (acl.OwnershipReclaimed)
                {
                    _log.Warning("Reprise de propriété du répertoire de données : {Diagnostic}", acl.Diagnostic);
                }
                else
                {
                    _log.Information("Permissions rétablies : {Diagnostic}", acl.Diagnostic);
                }
            }

            PersistedConfig config = LoadConfiguration();
            var configStore = new ConfigStore(ServicePaths.ConfigFile, _clock);
            _ruleStore = new RuleStore(configStore, config);

            var driver = new WinDivertDriverService();
            DriverStartResult start = driver.EnsureRunning();

            if (!start.Started)
            {
                _degradedReason = start.Diagnostic ?? "Le pilote d'interception n'a pas pu être chargé.";
                return false;
            }

            BuildPipeline();

            _ruleStore.Changed += OnConfigurationChanged;
            _coordinator!.Apply(_ruleStore.ActiveRules);
            _pipeline!.Start();

            _log.Information(
                "Interception active, {Regles} règle(s) dans le profil « {Profil} ».",
                _ruleStore.ActiveRules.Count,
                _ruleStore.Config.ActiveProfile.Name);

            return true;
        }
        catch (Exception exception) when (
            exception is ConfigNotSecurableException or InterceptionUnavailableException or IOException)
        {
            _degradedReason = exception.Message;
            _log.Error(exception, "Démarrage de l'interception impossible.");

            // Ne laisse rien d'ouvert derriere soi : un demarrage partiel qui laisserait un
            // handle actif etranglerait du trafic sans personne pour le liberer.
            _pipeline?.CloseAll();
            return false;
        }
    }

    private PersistedConfig LoadConfiguration()
    {
        var store = new ConfigStore(ServicePaths.ConfigFile, _clock);
        store.RecoverInterruptedWrite();

        ConfigReadResult read = store.Read();

        switch (read.Status)
        {
            case ConfigReadStatus.Ok:
                PersistedConfig? parsed = ConfigSerializer.TryDeserialize(read.Content!, out string? problem);

                if (parsed is not null)
                {
                    return parsed;
                }

                // Structurellement lisible mais invalide : meme traitement qu'une corruption.
                _log.Error("Configuration invalide : {Probleme}. Aucune limite ne sera appliquée.", problem);
                return PersistedConfig.CreateDefault();

            case ConfigReadStatus.Corrupt:
                // FR-022 : aucune limite appliquee, fichier conserve, utilisateur informe.
                _log.Error("Configuration corrompue : {Diagnostic}", read.Diagnostic);
                return PersistedConfig.CreateDefault();

            default:
                _log.Information("Aucune configuration : démarrage avec un profil vide.");
                PersistedConfig fresh = PersistedConfig.CreateDefault();
                store.Write(ConfigSerializer.Serialize(fresh));
                return fresh;
        }
    }

    private void BuildPipeline()
    {
        _flowTable = new FlowTable.FlowTable(_clock);

        var shaper = new PacketShaper(_clock);
        var ruleResolver = new RuleResolver();
        var processResolver = new ProcessIdentityResolver(
            new Win32ProcessInfoProvider(),
            new Core.Classification.PathNormalizer(new Win32DeviceVolumeResolver()));

        var pressure = new QueuePressureMonitor(_clock, threshold: 0.9, sustainedFor: TimeSpan.FromSeconds(2));

        _coordinator = new ShapingCoordinator(shaper, ruleResolver, new FileSystemPathProbe());
        _networkMonitor = new NetworkChangeMonitor(new NetworkChangeCoalescer(_clock));
        _networkMonitor.Start();

        // La machine a etats recoit le pipeline comme fermeur de handles : c'est ce qui rend
        // le fail-open automatique, quelle que soit la voie de sortie.
        ShapingPipeline? pipeline = null;
        _state = new ServiceStateMachine(new DeferredHandleCloser(() => pipeline), _clock);

        pipeline = new ShapingPipeline(
            WinDivertInterceptor.CreateFlowInterceptor(),
            WinDivertInterceptor.CreateNetworkInterceptor(),
            _flowTable,
            processResolver,
            ruleResolver,
            shaper,
            pressure,
            _state,
            _log);

        _pipeline = pipeline;
    }

    private void RunMaintenance()
    {
        ReportCounters();
        _state?.CheckWatchdog();

        // Tous les flux ne produisent pas d'evenement de suppression : arret brutal d'un
        // processus, perte d'evenement sous charge. Ce balayage borne la table.
        _flowTable?.PurgeStale(FlowRetention);

        if (_networkMonitor?.TryConsumeChange() is { } reason)
        {
            // FR-036 : l'environnement reseau a change. Les regles sont reappliquees sans
            // redemarrer le service.
            _log.Information("Changement d'environnement réseau ({Cause}) : règles réappliquées.", reason);
            _coordinator?.Apply(_ruleStore?.ActiveRules ?? []);
        }

        if (_state?.Current == ServiceState.FailOpen && _coordinator is not null)
        {
            _coordinator.InterceptionAvailable = false;
        }
    }

    /// <summary>
    /// Publie le mouvement des compteurs d'étape depuis la dernière seconde.
    /// </summary>
    /// <remarks>
    /// En <c>Debug</c>, car c'est une ligne par seconde dès qu'il y a du trafic. Silencieux
    /// quand rien ne bouge : un service permanent ne doit pas remplir un journal pour dire
    /// qu'il ne se passe rien.
    /// </remarks>
    private void ReportCounters()
    {
        if (_pipeline is null || !_log.IsEnabled(LogEventLevel.Debug))
        {
            return;
        }

        if (_pipeline.Counters.DescribeDelta(_lastCounters) is { } delta)
        {
            _log.Debug("Étapes : {Etapes}", delta);
        }

        _lastCounters = _pipeline.Counters.Snapshot();
    }

    private void OnConfigurationChanged(object? sender, PersistedConfig config)
    {
        // FR-002 : une regle modifiee prend effet sans redemarrer quoi que ce soit.
        _coordinator?.Apply(config.ActiveProfile.Rules);

        _log.Information(
            "Règles réappliquées : {Regles} règle(s) actives.",
            config.ActiveProfile.Rules.Count(rule => rule.Enabled));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Liberer le trafic AVANT toute autre chose : c'est le geste qui rend le reseau a
        // l'utilisateur, et il ne doit dependre d'aucune etape ulterieure (principe IV).
        _pipeline?.CloseAll();
        _networkMonitor?.Stop();

        if (_ruleStore is not null)
        {
            _ruleStore.Changed -= OnConfigurationChanged;
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _pipeline?.Dispose();
        _networkMonitor?.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Ferme des handles qui n'existent pas encore au moment où la machine à états est créée.
    /// </summary>
    /// <remarks>
    /// Le pipeline a besoin de la machine à états, qui a besoin d'un fermeur de handles, qui
    /// est le pipeline : cette indirection casse le cycle sans rendre la dépendance optionnelle
    /// — ce qui aurait laissé la porte ouverte à un fail-open silencieusement inopérant.
    /// </remarks>
    private sealed class DeferredHandleCloser(Func<ShapingPipeline?> resolve) : IHandleCloser
    {
        public void CloseAll() => resolve()?.CloseAll();
    }
}
