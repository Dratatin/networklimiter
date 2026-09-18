using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkLimiter.Contracts.Messages;

namespace NetworkLimiter.Contracts.Serialization;

/// <summary>
/// Contexte de sérialisation généré à la compilation.
/// </summary>
/// <remarks>
/// Le générateur de source évite toute réflexion à l'exécution : le service ne peut pas être
/// amené à matérialiser un type que l'appelant aurait choisi. C'est la contrepartie technique
/// de l'interdiction de désérialisation polymorphe du contrat.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(MessageEnvelope))]
[JsonSerializable(typeof(ErrorInfo))]
[JsonSerializable(typeof(HelloPayload))]
[JsonSerializable(typeof(HelloResultPayload))]
internal sealed partial class MessageJsonContext : JsonSerializerContext;

/// <summary>
/// Sérialise et désérialise les messages du protocole.
/// </summary>
/// <remarks>
/// Toutes les protections du contrat sont concentrées ici : profondeur bornée, champs inconnus
/// refusés, virgule finale et commentaires interdits. Aucun appelant ne doit construire ses
/// propres <see cref="JsonSerializerOptions"/>, sans quoi ces garanties deviendraient
/// contournables par oubli.
/// </remarks>
public static class MessageSerializer
{
    /// <summary>
    /// Profondeur maximale d'imbrication acceptée.
    /// </summary>
    /// <remarks>
    /// Sans cette borne, un message imbriquant quelques milliers d'objets suffit à épuiser la
    /// pile du service — un déni de service à coût nul pour l'appelant. Le protocole n'a
    /// besoin que de quelques niveaux ; 32 laisse une marge confortable.
    /// </remarks>
    public const int MaxDepth = 32;

    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(MessageJsonContext.Default.Options)
        {
            MaxDepth = MaxDepth,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            PropertyNameCaseInsensitive = false,
            AllowDuplicateProperties = false,
        };

        options.MakeReadOnly();
        return options;
    }

    /// <summary>Sérialise une enveloppe.</summary>
    public static string Serialize(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return JsonSerializer.Serialize(envelope, Options);
    }

    /// <summary>Sérialise une enveloppe en octets UTF-8, prêts pour le cadrage.</summary>
    public static byte[] SerializeToUtf8Bytes(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
    }

    /// <summary>Désérialise une enveloppe.</summary>
    /// <exception cref="JsonException">Le document est malformé, trop profond, ou porte un champ inconnu.</exception>
    public static MessageEnvelope Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        MessageEnvelope? envelope = JsonSerializer.Deserialize<MessageEnvelope>(json, Options);

        return envelope ?? throw new JsonException("Le message est vide ou vaut null.");
    }

    /// <summary>Désérialise une enveloppe depuis des octets UTF-8.</summary>
    /// <exception cref="JsonException">Le document est malformé, trop profond, ou porte un champ inconnu.</exception>
    public static MessageEnvelope Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        MessageEnvelope? envelope = JsonSerializer.Deserialize<MessageEnvelope>(utf8Json, Options);

        return envelope ?? throw new JsonException("Le message est vide ou vaut null.");
    }

    /// <summary>
    /// Lit la charge utile d'une enveloppe dans le type attendu par le receveur.
    /// </summary>
    /// <remarks>
    /// Le type est fourni par l'appelant de cette méthode — c'est-à-dire par le gestionnaire
    /// qui sait quel message il traite — jamais déduit du contenu du message.
    /// </remarks>
    /// <exception cref="JsonException">La charge utile est absente ou ne correspond pas au type attendu.</exception>
    public static TPayload ReadPayload<TPayload>(MessageEnvelope envelope)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Payload is not { } element)
        {
            throw new JsonException($"Le message « {envelope.Type} » ne porte pas de charge utile.");
        }

        TPayload? payload = element.Deserialize<TPayload>(Options);

        return payload ?? throw new JsonException($"Charge utile illisible pour « {envelope.Type} ».");
    }

    /// <summary>Convertit une charge utile en élément JSON, pour insertion dans une enveloppe.</summary>
    internal static JsonElement ToElement<TPayload>(TPayload payload)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.SerializeToElement(payload, Options);
    }
}
