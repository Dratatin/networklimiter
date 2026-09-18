namespace NetworkLimiter.Contracts.Messages;

/// <summary>Compatibilité de la machine avec l'interception.</summary>
/// <remarks>
/// Rendue même — surtout — quand elle est refusée : un utilisateur dont le système est hors
/// matrice doit l'apprendre de l'outil, avec la raison, plutôt que constater que rien ne se
/// limite jamais.
/// </remarks>
public sealed record CompatibilityDto
{
    /// <summary>La machine peut faire tourner l'interception.</summary>
    public required bool Supported { get; init; }

    /// <summary>Numéro de build de Windows.</summary>
    public required int OsBuild { get; init; }

    /// <summary>Architecture du processus de service.</summary>
    public required string Architecture { get; init; }

    /// <summary>Raison du refus, ou <c>null</c> si la machine est supportée.</summary>
    public string? Diagnostic { get; init; }
}

/// <summary>
/// État de santé du service (FR-026).
/// </summary>
/// <remarks>
/// <para>
/// L'utilisateur doit pouvoir répondre seul à « pourquoi cette application n'est-elle pas
/// limitée ? ». Sans cette réponse, l'outil devient imprévisible, donc inutilisable — on ne
/// fait pas confiance à un programme qui ralentit son réseau sans jamais dire ce qu'il fait.
/// </para>
/// <para>
/// Chaque champ existe pour distinguer une cause d'une autre. Un seul booléen « ça marche »
/// obligerait l'utilisateur à deviner s'il manque le pilote, si la machine est hors matrice,
/// si la limitation est suspendue, ou si son application n'est simplement pas lancée.
/// </para>
/// </remarks>
public sealed record HealthResultPayload
{
    /// <summary>La mise en forme est opérationnelle.</summary>
    public required bool InterceptionActive { get; init; }

    /// <summary>Le pilote d'interception est chargé.</summary>
    public required bool DriverLoaded { get; init; }

    /// <summary>La limitation est suspendue par l'utilisateur (FR-024).</summary>
    public required bool Suspended { get; init; }

    /// <summary>Nombre de règles effectivement appliquées.</summary>
    public required int ActiveRuleCount { get; init; }

    /// <summary>Nombre de règles définies mais non appliquées.</summary>
    public required int InactiveRuleCount { get; init; }

    /// <summary>Pression de la file d'interception, entre 0 et 1.</summary>
    public required double QueuePressure { get; init; }

    /// <summary>Un adaptateur de type VPN semble actif.</summary>
    public required bool VpnDetected { get; init; }

    /// <summary>
    /// Avertissement sur le VPN, ou <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Le texte vient du service parce que c'est lui qui sait ce qu'il a détecté, et il décrit
    /// ce qui est <b>incertain</b>, jamais ce qui est faux : la détection est heuristique.
    /// </remarks>
    public string? VpnWarning { get; init; }

    /// <summary>Avertissement sur la configuration, ou <c>null</c>.</summary>
    public string? ConfigWarning { get; init; }

    /// <summary>
    /// Raison du mode dégradé, ou <c>null</c> si le service fonctionne.
    /// </summary>
    /// <remarks>
    /// Présent dès que l'interception n'est pas active. Un service dégradé qui ne dirait pas
    /// pourquoi laisserait l'utilisateur devant une fenêtre affirmant que rien ne marche, sans
    /// la moindre piste.
    /// </remarks>
    public string? DegradedReason { get; init; }

    /// <summary>Compatibilité de la machine.</summary>
    public required CompatibilityDto Compatibility { get; init; }

    /// <summary>Version du service.</summary>
    public required string ServiceVersion { get; init; }
}
