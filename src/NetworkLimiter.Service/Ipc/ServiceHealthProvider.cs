using System.Runtime.Versioning;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Service.Health;
using NetworkLimiter.Service.Interception;
using NetworkLimiter.Service.Safety;

namespace NetworkLimiter.Service.Ipc;

/// <summary>Rend l'état de santé du service.</summary>
public interface IHealthSource
{
    /// <summary>Compose l'état de santé courant.</summary>
    HealthResultPayload GetHealth();
}

/// <summary>
/// Compose l'état de santé à partir de ce que le service constate réellement.
/// </summary>
/// <remarks>
/// <para>
/// Chaque champ existe pour <b>distinguer une cause d'une autre</b>. Un seul booléen « ça
/// marche » obligerait l'utilisateur à deviner s'il manque le pilote, si sa machine est hors
/// matrice, si la limitation est suspendue, ou si son application n'est simplement pas lancée —
/// quatre situations sans rien de commun, sauf qu'aucune limite ne s'applique.
/// </para>
/// <para>
/// Rien n'est supposé : la raison du mode dégradé vient du démarrage effectif, la détection de
/// VPN d'un examen des adaptateurs, la compatibilité de la même porte que celle qui décide de
/// démarrer ou non. Composer un état de santé à partir d'hypothèses serait pire que ne rien
/// afficher.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ServiceHealthProvider : IHealthSource
{
    private readonly ShapingCoordinator _coordinator;
    private readonly SuspensionController _suspension;
    private readonly Func<IReadOnlySet<string>> _runningPaths;
    private readonly Func<IReadOnlyList<Contracts.Messages.RuleDto>> _rules;
    private readonly INetworkAdapterSource _adapters;
    private readonly Func<string?> _degradedReason;
    private readonly string _serviceVersion;

    /// <summary>Crée le fournisseur.</summary>
    /// <exception cref="ArgumentNullException">Un argument est <c>null</c>.</exception>
    public ServiceHealthProvider(
        ShapingCoordinator coordinator,
        SuspensionController suspension,
        Func<IReadOnlyList<Contracts.Messages.RuleDto>> rules,
        Func<IReadOnlySet<string>> runningPaths,
        INetworkAdapterSource adapters,
        Func<string?> degradedReason,
        string serviceVersion)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(suspension);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(runningPaths);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(degradedReason);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceVersion);

        _coordinator = coordinator;
        _suspension = suspension;
        _rules = rules;
        _runningPaths = runningPaths;
        _adapters = adapters;
        _degradedReason = degradedReason;
        _serviceVersion = serviceVersion;
    }

    /// <inheritdoc />
    public HealthResultPayload GetHealth()
    {
        IReadOnlySet<string> running = _runningPaths();
        IReadOnlyList<Contracts.Messages.RuleDto> rules = _rules();

        int inactive = rules.Count(rule => _coordinator.GetInactiveReason(rule, running) is not null);

        VpnStatus vpn = VpnDetector.Detect(_adapters);
        PlatformInfo platform = CompatibilityGate.GetCurrentPlatform();
        CompatibilityResult compatibility = CompatibilityGate.Evaluate(platform);

        bool supported = CompatibilityGate.AllowsInterception(compatibility.Verdict);

        return new HealthResultPayload
        {
            InterceptionActive = _coordinator.InterceptionAvailable && !_suspension.IsSuspended,

            // Le pilote est charge si l'interception est disponible : c'est le service qui l'a
            // demarre, et sa disponibilite en decoule directement. Interroger le gestionnaire
            // de services a chaque appel couterait un aller-retour noyau pour une information
            // qu'on possede deja.
            DriverLoaded = _coordinator.InterceptionAvailable,

            Suspended = _suspension.IsSuspended,
            ActiveRuleCount = rules.Count - inactive,
            InactiveRuleCount = inactive,
            QueuePressure = 0,
            VpnDetected = vpn.Detected,
            VpnWarning = vpn.Warning,
            ConfigWarning = null,
            DegradedReason = _degradedReason(),

            Compatibility = new CompatibilityDto
            {
                Supported = supported,
                OsBuild = platform.OsBuild,
                Architecture = platform.ProcessArchitecture.ToString(),
                Diagnostic = supported ? null : compatibility.Diagnostic,
            },

            ServiceVersion = _serviceVersion,
        };
    }
}
