using System.Text.Json.Serialization;

namespace NetworkLimiter.Contracts.Messages;

/// <summary>État d'application d'une règle.</summary>
/// <remarks>
/// Sérialisée en toutes lettres, comme <see cref="ErrorCode"/>. En numérique, réordonner
/// l'énumération changerait silencieusement le sens pour un client plus ancien : « active »
/// deviendrait « inactive » sans qu'aucune version de protocole ne bouge.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<RuleApplicationStatus>))]
public enum RuleApplicationStatus
{
    /// <summary>La règle est appliquée : une application visée tourne et son trafic est plafonné.</summary>
    Active,

    /// <summary>La règle est définie mais ne s'applique pas ; <c>InactiveReason</c> dit pourquoi.</summary>
    Inactive,
}

/// <summary>
/// Pourquoi une règle définie ne s'applique pas (FR-026).
/// </summary>
/// <remarks>
/// <para>
/// Ce vocabulaire appartient au <b>contrat</b>, pas au service. Il traverse la frontière IPC :
/// le laisser dépendre d'une énumération interne rendrait un simple renommage capable de casser
/// l'interface sans qu'aucun compilateur ne le signale, puisque la valeur voyageait auparavant
/// sous forme de chaîne obtenue par <c>ToString()</c>.
/// </para>
/// <para>
/// L'ordre de déclaration est celui de la <b>priorité</b> : la cause la plus englobante d'abord.
/// Annoncer « application non lancée » alors que toute la limitation est suspendue enverrait
/// l'utilisateur chercher au mauvais endroit.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<RuleInactiveReasonDto>))]
public enum RuleInactiveReasonDto
{
    /// <summary>L'interception n'est pas opérationnelle : pilote absent, handle fermé.</summary>
    InterceptionUnavailable,

    /// <summary>La limitation est suspendue globalement.</summary>
    GloballySuspended,

    /// <summary>L'utilisateur a désactivé la règle sans la supprimer.</summary>
    RuleDisabled,

    /// <summary>Le chemin enregistré n'existe plus et aucun repli n'a été trouvé.</summary>
    ExecutablePathNotFound,

    /// <summary>La règle s'applique via le repli sur le nom d'exécutable, pas le chemin exact.</summary>
    MatchedByFallbackName,

    /// <summary>Application packagée non prise en charge.</summary>
    PackagedAppUnsupported,

    /// <summary>Aucun processus correspondant n'est en cours d'exécution.</summary>
    ApplicationNotRunning,
}

/// <summary>
/// Une règle telle que l'interface doit l'afficher.
/// </summary>
/// <remarks>
/// Étend <see cref="RuleDto"/> de ce que l'utilisateur ne peut pas déduire : la règle
/// s'applique-t-elle, et sinon pourquoi. FR-026 l'exige — une règle définie mais inactive doit
/// toujours pouvoir répondre à « pourquoi cette application n'est-elle pas limitée ? », sans
/// quoi l'utilisateur n'a aucun moyen d'agir.
/// </remarks>
public sealed record RuleStateDto
{
    /// <summary>La règle telle qu'elle est persistée.</summary>
    public required RuleDto Rule { get; init; }

    /// <summary>La règle s'applique-t-elle en ce moment.</summary>
    public required RuleApplicationStatus Status { get; init; }

    /// <summary>Raison de l'inactivité, ou <c>null</c> si la règle s'applique.</summary>
    public RuleInactiveReasonDto? InactiveReason { get; init; }

    /// <summary>
    /// Vérifie l'invariant du contrat, ou rend le problème constaté.
    /// </summary>
    /// <remarks>
    /// <b>Exactement une</b> raison pour une règle inactive, <b>aucune</b> pour une règle
    /// active. Une règle inactive sans raison est le pire des deux mondes : l'interface
    /// affiche « ne s'applique pas » et ne peut rien dire de plus, laissant l'utilisateur
    /// devant un problème sans prise. FR-026 existe précisément pour l'interdire.
    /// </remarks>
    public string? Validate() => (Status, InactiveReason) switch
    {
        (RuleApplicationStatus.Inactive, null) =>
            "Une règle inactive doit porter une raison (FR-026).",

        (RuleApplicationStatus.Active, not null) =>
            "Une règle active ne peut pas porter de raison d'inactivité.",

        (RuleApplicationStatus.Inactive, { } reason) when !Enum.IsDefined(reason) =>
            $"Raison d'inactivité inconnue : {reason}.",

        _ => null,
    };

    /// <summary>
    /// Nombre de processus en cours actuellement couverts par cette règle.
    /// </summary>
    /// <remarks>
    /// Zéro sur une règle par ailleurs valide signifie « l'application n'est pas lancée », ce
    /// qui est la cause d'inactivité la plus fréquente et la plus facile à mal interpréter.
    /// </remarks>
    public required int MatchedProcessCount { get; init; }
}

/// <summary>Un profil et l'état de ses règles.</summary>
public sealed record ProfileStateDto
{
    /// <summary>Identifiant du profil.</summary>
    public required Guid Id { get; init; }

    /// <summary>Nom affichable.</summary>
    public required string Name { get; init; }

    /// <summary>Plafond global du profil.</summary>
    public required GlobalLimitDto GlobalLimit { get; init; }

    /// <summary>Règles du profil, avec leur état.</summary>
    public required IReadOnlyList<RuleStateDto> Rules { get; init; }
}

/// <summary>
/// Demande de suspension ou de reprise globale (FR-024).
/// </summary>
/// <remarks>
/// Un booléen explicite plutôt qu'une bascule : deux interfaces ouvertes en même temps
/// enverraient des bascules qui s'annuleraient, et l'utilisateur constaterait que son bouton
/// « suspendre » ne suspend rien une fois sur deux.
/// </remarks>
public sealed record SetSuspendedPayload
{
    /// <summary>Suspendre la limitation.</summary>
    public required bool Suspended { get; init; }
}

/// <summary>
/// Une application vue en train de communiquer.
/// </summary>
/// <remarks>
/// C'est ce qui rend l'outil utilisable sans deviner : sans cette liste, l'utilisateur devrait
/// connaître le chemin exact de l'exécutable à limiter et le saisir à la main. Seul le service
/// dispose de l'information, puisque lui seul voit les flux.
/// </remarks>
public sealed record ObservedAppDto
{
    /// <summary>Chemin normalisé de l'exécutable.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Nom de fichier de l'exécutable.</summary>
    public required string ExecutableName { get; init; }

    /// <summary>Une règle du profil actif vise déjà cette application.</summary>
    public required bool AlreadyRuled { get; init; }
}

/// <summary>Réponse à <c>GetState</c>.</summary>
public sealed record GetStateResultPayload
{
    /// <summary>Profil actif.</summary>
    public required Guid ActiveProfileId { get; init; }

    /// <summary>Tous les profils.</summary>
    public required IReadOnlyList<ProfileStateDto> Profiles { get; init; }

    /// <summary>La limitation est suspendue globalement (FR-024).</summary>
    public required bool Suspended { get; init; }

    /// <summary>
    /// L'interception est opérationnelle.
    /// </summary>
    /// <remarks>
    /// À <c>false</c>, aucune règle ne s'applique quoi qu'affiche le reste de la charge utile.
    /// L'interface doit le dire franchement plutôt que de montrer des règles « actives » qui ne
    /// le sont pas.
    /// </remarks>
    public required bool InterceptionAvailable { get; init; }

    /// <summary>
    /// Applications ayant eu une activité réseau récente, pour proposer quoi limiter (FR-001).
    /// </summary>
    /// <remarks>
    /// Ne contient <b>aucune</b> information sur leurs correspondants : ni adresse, ni nom
    /// d'hôte, ni port distant (FR-018, FR-033). Savoir qu'une application communique n'exige
    /// pas de savoir avec qui, et l'outil n'a aucune raison de le transmettre.
    /// </remarks>
    public required IReadOnlyList<ObservedAppDto> ObservedApplications { get; init; }
}

/// <summary>
/// Notification poussée : l'état a changé.
/// </summary>
/// <remarks>
/// Diffusée à <b>tous</b> les clients connectés, y compris non élevés (FR-034b). Une interface
/// en lecture seule doit refléter les changements faits par une instance élevée, sans quoi
/// l'utilisateur verrait deux fenêtres se contredire.
/// </remarks>
public sealed record StateChangedPayload
{
    /// <summary>État complet après le changement.</summary>
    public required GetStateResultPayload State { get; init; }
}
