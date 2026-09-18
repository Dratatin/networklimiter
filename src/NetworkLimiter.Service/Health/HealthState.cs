using NetworkLimiter.Service.Resilience;

namespace NetworkLimiter.Service.Health;

/// <summary>
/// Raison pour laquelle une règle définie n'est pas appliquée.
/// </summary>
/// <remarks>
/// <para>
/// Énumération volontairement <b>exhaustive et fermée</b>. Toute règle non appliquée en porte
/// exactement une : c'est la réponse à « pourquoi cette application n'est pas limitée ? », la
/// question à laquelle le principe VI oblige à répondre. Une règle inactive sans raison est un
/// défaut, pas un cas particulier.
/// </para>
/// </remarks>
public enum RuleInactiveReason
{
    /// <summary>Aucun processus correspondant n'est en cours d'exécution.</summary>
    ApplicationNotRunning,

    /// <summary>Le chemin enregistré n'existe plus et aucun repli n'a été trouvé.</summary>
    ExecutablePathNotFound,

    /// <summary>La règle s'applique via le repli sur le nom d'exécutable, pas le chemin exact.</summary>
    MatchedByFallbackName,

    /// <summary>L'utilisateur a désactivé la règle sans la supprimer.</summary>
    RuleDisabled,

    /// <summary>La limitation est suspendue globalement.</summary>
    GloballySuspended,

    /// <summary>L'interception n'est pas opérationnelle : pilote absent, handle fermé.</summary>
    InterceptionUnavailable,

    /// <summary>Application packagée non prise en charge.</summary>
    PackagedAppUnsupported,
}

/// <summary>Une règle définie mais non appliquée, avec sa cause.</summary>
/// <param name="RuleId">Identifiant de la règle.</param>
/// <param name="Reason">Cause unique de l'inapplication.</param>
public sealed record InactiveRule(Guid RuleId, RuleInactiveReason Reason);

/// <summary>
/// État de santé du service, tel que l'interface l'affiche.
/// </summary>
/// <remarks>
/// Support direct de FR-026. L'utilisateur doit pouvoir répondre seul à « pourquoi cette
/// application n'est pas limitée ? » ; sans cette réponse, le produit devient imprévisible,
/// donc inutilisable.
/// </remarks>
/// <param name="InterceptionActive">La mise en forme est opérationnelle.</param>
/// <param name="DriverLoaded">Le pilote est chargé.</param>
/// <param name="Suspended">La limitation est suspendue par l'utilisateur.</param>
/// <param name="ActiveRuleCount">Nombre de règles effectivement appliquées.</param>
/// <param name="InactiveRules">Règles définies mais non appliquées, chacune avec sa cause.</param>
/// <param name="QueuePressure">Pression de la file d'interception, entre 0 et 1.</param>
/// <param name="VpnDetected">Un adaptateur de type VPN est actif.</param>
/// <param name="ConfigWarning">Avertissement sur la configuration, ou <c>null</c>.</param>
/// <param name="LastNetworkChange">Dernier changement d'environnement réseau traité.</param>
public sealed record HealthState(
    bool InterceptionActive,
    bool DriverLoaded,
    bool Suspended,
    int ActiveRuleCount,
    IReadOnlyList<InactiveRule> InactiveRules,
    double QueuePressure,
    bool VpnDetected,
    string? ConfigWarning,
    NetworkChangeReason? LastNetworkChange)
{
    /// <summary>État de santé d'un service qui n'applique aucune limite.</summary>
    public static HealthState NotLimiting(string? configWarning = null) =>
        new(InterceptionActive: false,
            DriverLoaded: false,
            Suspended: false,
            ActiveRuleCount: 0,
            InactiveRules: [],
            QueuePressure: 0,
            VpnDetected: false,
            ConfigWarning: configWarning,
            LastNetworkChange: null);
}
