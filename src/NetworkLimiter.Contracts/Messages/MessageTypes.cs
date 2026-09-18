using System.Collections.Frozen;

namespace NetworkLimiter.Contracts.Messages;

/// <summary>
/// Liste blanche des types de message et catégorisation lecture / écriture.
/// </summary>
/// <remarks>
/// <para>
/// Cette classe porte à elle seule la règle d'accès de FR-034 : consultation libre, modification
/// soumise à élévation. Le service interroge <see cref="RequiresElevation"/> avant de traiter
/// quoi que ce soit.
/// </para>
/// <para>
/// Deux choix délibérément conservateurs : la comparaison est <b>ordinale et sensible à la
/// casse</b> — aucune correspondance approximative sur une frontière de privilège — et un type
/// inconnu est réputé exiger l'élévation. Un message qu'on ajouterait sans le catégoriser
/// serait ainsi refusé, jamais traité comme une lecture accessible à tous.
/// </para>
/// </remarks>
public static class MessageTypes
{
    // -- Lecture : accessibles sans élévation ---------------------------------

    /// <summary>Poignée de main. Premier message obligatoire de toute connexion.</summary>
    public const string Hello = "Hello";

    /// <summary>Lecture de l'état complet : profils, règles, plafond global.</summary>
    public const string GetState = "GetState";

    /// <summary>Lecture de l'état de santé.</summary>
    public const string GetHealth = "GetHealth";

    /// <summary>Abonnement au flux de métriques temps réel.</summary>
    public const string SubscribeMetrics = "SubscribeMetrics";

    // -- Écriture : élévation obligatoire -------------------------------------

    /// <summary>Création ou modification d'une règle.</summary>
    public const string UpsertRule = "UpsertRule";

    /// <summary>Suppression d'une règle.</summary>
    public const string DeleteRule = "DeleteRule";

    /// <summary>Activation ou désactivation d'une règle, sans suppression.</summary>
    public const string SetRuleEnabled = "SetRuleEnabled";

    /// <summary>Modification du plafond global.</summary>
    public const string SetGlobalLimit = "SetGlobalLimit";

    /// <summary>Création ou renommage d'un profil.</summary>
    public const string UpsertProfile = "UpsertProfile";

    /// <summary>Suppression d'un profil.</summary>
    public const string DeleteProfile = "DeleteProfile";

    /// <summary>Changement du profil actif.</summary>
    public const string SetActiveProfile = "SetActiveProfile";

    /// <summary>Suspension globale de la limitation.</summary>
    public const string SetSuspended = "SetSuspended";

    /// <summary>Import d'une configuration complète.</summary>
    public const string ImportConfig = "ImportConfig";

    // -- Réponses et messages poussés -----------------------------------------

    /// <summary>Réponse d'erreur.</summary>
    public const string Error = "Error";

    /// <summary>Suffixe des réponses de succès.</summary>
    public const string ResultSuffix = "Result";

    /// <summary>Notification poussée : l'état a changé.</summary>
    public const string StateChanged = "StateChanged";

    /// <summary>Notification poussée : nouvelle mesure de débit.</summary>
    public const string MetricsTick = "MetricsTick";

    /// <summary>Messages de lecture, accessibles sans élévation.</summary>
    public static readonly FrozenSet<string> ReadRequests =
        new[] { Hello, GetState, GetHealth, SubscribeMetrics }
            .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Messages d'écriture, soumis à élévation (FR-034a).</summary>
    public static readonly FrozenSet<string> WriteRequests =
        new[]
        {
            UpsertRule, DeleteRule, SetRuleEnabled, SetGlobalLimit,
            UpsertProfile, DeleteProfile, SetActiveProfile, SetSuspended, ImportConfig,
        }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Ensemble des requêtes reconnues.</summary>
    public static readonly FrozenSet<string> AllRequests =
        ReadRequests.Concat(WriteRequests).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Indique si le type est une requête reconnue. Comparaison ordinale, sensible à la casse.
    /// </summary>
    public static bool IsKnownRequest(string? type) =>
        type is not null && AllRequests.Contains(type);

    /// <summary>
    /// Indique si le type exige une élévation de privilèges.
    /// </summary>
    /// <remarks>
    /// Un type inconnu exige l'élévation. En cas de doute, on refuse plutôt que d'ouvrir :
    /// l'inverse ferait d'un oubli de catégorisation une faille d'élévation de privilèges.
    /// </remarks>
    public static bool RequiresElevation(string? type) =>
        type is null || !ReadRequests.Contains(type);
}
