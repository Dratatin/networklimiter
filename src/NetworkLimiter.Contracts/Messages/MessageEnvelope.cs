using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkLimiter.Contracts.Messages;

/// <summary>
/// Détail d'une erreur renvoyée par le service.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ErrorInfo
{
    /// <summary>Cause, sous forme de code énuméré exploitable par l'interface.</summary>
    [JsonPropertyName("code")]
    public required ErrorCode Code { get; init; }

    /// <summary>Texte affichable à l'utilisateur.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

/// <summary>
/// Enveloppe commune à tous les messages du protocole.
/// </summary>
/// <remarks>
/// <para>
/// La charge utile reste un <see cref="JsonElement"/> brut. C'est délibéré : le type concret
/// est choisi par le <b>receveur</b>, à partir de la liste blanche de
/// <see cref="MessageTypes"/>, jamais dicté par le message. Cela ferme la désérialisation
/// polymorphe, vecteur classique d'exécution de code par choix de type attaquant.
/// </para>
/// <para>
/// <see cref="JsonUnmappedMemberHandling.Disallow"/> rejette tout champ inconnu. Le protocole
/// ne pratique pas la tolérance : un champ ignoré est un champ dont on ne sait pas s'il devait
/// changer le comportement.
/// </para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MessageEnvelope
{
    /// <summary>Discriminant du message, comparé à la liste blanche.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Identifiant de corrélation entre une requête et sa réponse.</summary>
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    /// <summary>Présent sur les réponses : succès ou échec.</summary>
    [JsonPropertyName("ok")]
    public bool? Ok { get; init; }

    /// <summary>Charge utile, non interprétée à ce niveau.</summary>
    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }

    /// <summary>Détail de l'erreur, présent uniquement sur une réponse en échec.</summary>
    [JsonPropertyName("error")]
    public ErrorInfo? Error { get; init; }

    /// <summary>Crée une requête avec une charge utile.</summary>
    public static MessageEnvelope CreateRequest<TPayload>(string type, TPayload payload)
        where TPayload : class =>
        new()
        {
            Type = type,
            Id = Guid.NewGuid(),
            Payload = Serialization.MessageSerializer.ToElement(payload),
        };

    /// <summary>Crée une requête sans charge utile.</summary>
    public static MessageEnvelope CreateRequest(string type) =>
        new() { Type = type, Id = Guid.NewGuid() };

    /// <summary>Crée une réponse de succès corrélée à une requête.</summary>
    public static MessageEnvelope CreateResult<TPayload>(Guid requestId, string type, TPayload payload)
        where TPayload : class =>
        new()
        {
            Type = type,
            Id = requestId,
            Ok = true,
            Payload = Serialization.MessageSerializer.ToElement(payload),
        };

    /// <summary>Crée une réponse d'erreur corrélée à une requête.</summary>
    public static MessageEnvelope CreateError(Guid requestId, ErrorCode code, string message) =>
        new()
        {
            Type = MessageTypes.Error,
            Id = requestId,
            Ok = false,
            Error = new ErrorInfo { Code = code, Message = message },
        };
}

/// <summary>Charge utile de la poignée de main, émise par l'interface.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HelloPayload
{
    /// <summary>Version du protocole que parle l'interface.</summary>
    [JsonPropertyName("protocolVersion")]
    public required int ProtocolVersion { get; init; }

    /// <summary>Version de l'interface, à fin de diagnostic.</summary>
    [JsonPropertyName("clientVersion")]
    public required string ClientVersion { get; init; }
}

/// <summary>Réponse à la poignée de main, émise par le service.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HelloResultPayload
{
    /// <summary>Version du protocole que parle le service.</summary>
    [JsonPropertyName("protocolVersion")]
    public required int ProtocolVersion { get; init; }

    /// <summary>Version du service.</summary>
    [JsonPropertyName("serviceVersion")]
    public required string ServiceVersion { get; init; }

    /// <summary>
    /// Indique si l'appelant est élevé.
    /// </summary>
    /// <remarks>
    /// Calculé par le service à partir du jeton <b>réel</b> de l'appelant. Un client ne peut
    /// pas se déclarer élevé : le champ n'existe pas dans <see cref="HelloPayload"/>, et un
    /// message qui l'y ajouterait serait rejeté comme champ inconnu.
    /// </remarks>
    [JsonPropertyName("elevated")]
    public required bool Elevated { get; init; }
}
