using System.Text.Json.Serialization;

namespace NetworkLimiter.Contracts.Messages;

/// <summary>
/// Causes d'échec exposées par le service, telles que l'interface doit les traiter.
/// </summary>
/// <remarks>
/// Sérialisées en texte, jamais en entier : un décalage d'énumération changerait sinon
/// silencieusement le sens d'un message entre deux versions.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ErrorCode>))]
public enum ErrorCode
{
    /// <summary>Commande d'écriture émise sans élévation. L'interface propose la relance élevée.</summary>
    ElevationRequired,

    /// <summary>Versions de protocole incompatibles. L'interface affiche une action de mise à jour.</summary>
    ProtocolVersionMismatch,

    /// <summary>Champ hors bornes, inconnu ou manquant. L'interface signale le champ fautif.</summary>
    ValidationFailed,

    /// <summary>Identifiant inexistant. L'interface rafraîchit son état.</summary>
    NotFound,

    /// <summary>Une règle vise déjà cette application. L'interface propose d'éditer l'existante.</summary>
    ConflictingRule,

    /// <summary>Pilote absent ou handle fermé. L'interface affiche l'état de santé.</summary>
    InterceptionUnavailable,

    /// <summary>Configuration illisible. L'interface propose la restauration.</summary>
    ConfigCorrupt,

    /// <summary>Défaillance inattendue. L'interface invite à consulter le journal.</summary>
    InternalError,
}
