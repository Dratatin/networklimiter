using System.Globalization;
using System.Text.Json;
using NetworkLimiter.Contracts.Serialization;

namespace NetworkLimiter.Contracts.Messages;

/// <summary>Verdict d'une poignée de main.</summary>
public enum HandshakeOutcome
{
    /// <summary>Versions compatibles, la connexion peut servir.</summary>
    Accepted,

    /// <summary>Le premier message n'était pas <c>Hello</c>.</summary>
    HelloExpected,

    /// <summary>Versions de protocole incompatibles.</summary>
    VersionMismatch,

    /// <summary>Charge utile absente, illisible ou porteuse d'un champ inconnu.</summary>
    Malformed,
}

/// <summary>Résultat détaillé d'une poignée de main.</summary>
/// <param name="Outcome">Verdict.</param>
/// <param name="ErrorCode">Code à renvoyer à l'appelant, ou <c>null</c> si accepté.</param>
/// <param name="Diagnostic">Message actionnable destiné à l'utilisateur, ou <c>null</c> si accepté.</param>
public sealed record HandshakeResult(HandshakeOutcome Outcome, ErrorCode? ErrorCode, string? Diagnostic)
{
    /// <summary>Indique si la connexion doit être fermée immédiatement.</summary>
    public bool ShouldCloseConnection => Outcome != HandshakeOutcome.Accepted;
}

/// <summary>
/// Valide la poignée de main d'une connexion.
/// </summary>
/// <remarks>
/// <para>
/// Ce code est le premier à toucher une entrée non fiable. Il traduit donc toute malformation
/// en verdict et ne laisse jamais remonter d'exception : une exception non gérée ici tuerait
/// la boucle de connexion du service.
/// </para>
/// <para>
/// Il vit dans <c>Contracts</c> plutôt que dans le service parce qu'il est purement
/// protocolaire — les deux côtés du canal ont intérêt à partager exactement les mêmes règles,
/// et un désaccord se manifeste alors à la compilation.
/// </para>
/// </remarks>
public static class HandshakeValidator
{
    /// <summary>Valide le premier message d'une connexion.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="firstMessage"/> est <c>null</c>.</exception>
    public static HandshakeResult Validate(MessageEnvelope firstMessage)
    {
        ArgumentNullException.ThrowIfNull(firstMessage);

        if (!string.Equals(firstMessage.Type, MessageTypes.Hello, StringComparison.Ordinal))
        {
            return new HandshakeResult(
                HandshakeOutcome.HelloExpected,
                Messages.ErrorCode.ValidationFailed,
                "Le premier message d'une connexion doit être « Hello ».");
        }

        HelloPayload payload;
        try
        {
            payload = MessageSerializer.ReadPayload<HelloPayload>(firstMessage);
        }
        catch (JsonException exception)
        {
            return new HandshakeResult(
                HandshakeOutcome.Malformed,
                Messages.ErrorCode.ValidationFailed,
                $"Poignée de main illisible : {exception.Message}");
        }

        if (payload.ProtocolVersion != ProtocolVersion.Current)
        {
            return new HandshakeResult(
                HandshakeOutcome.VersionMismatch,
                Messages.ErrorCode.ProtocolVersionMismatch,
                $"L'interface parle la version {payload.ProtocolVersion.ToString(CultureInfo.InvariantCulture)} du protocole, " +
                $"le service la version {ProtocolVersion.Current.ToString(CultureInfo.InvariantCulture)}. " +
                "Mettez à jour l'interface et le service ensemble.");
        }

        return new HandshakeResult(HandshakeOutcome.Accepted, null, null);
    }

    /// <summary>
    /// Construit la réponse de poignée de main.
    /// </summary>
    /// <param name="serviceVersion">Version du service.</param>
    /// <param name="elevated">
    /// Élévation <b>constatée</b> sur le jeton réel de l'appelant. Ce n'est jamais une valeur
    /// que le client aurait déclarée : <see cref="HelloPayload"/> ne porte pas ce champ.
    /// </param>
    public static HelloResultPayload CreateResult(string serviceVersion, bool elevated) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            ServiceVersion = serviceVersion,
            Elevated = elevated,
        };
}
