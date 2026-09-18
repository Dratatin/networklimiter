using System.Text.Json;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Contracts.Serialization;

namespace NetworkLimiter.Service.Ipc;

/// <summary>
/// Exécute la poignée de main côté service sur une connexion établie.
/// </summary>
/// <remarks>
/// Aucune requête n'est traitée avant que cette poignée de main ait réussi. Tant qu'elle n'a
/// pas abouti, le service ne sait pas à quelle version de contrat il parle : exécuter quoi que
/// ce soit dans cet état reviendrait à interpréter des champs dont il ignore le sens.
/// </remarks>
public sealed class HandshakeHandler
{
    private readonly string _serviceVersion;

    /// <summary>Crée le gestionnaire.</summary>
    /// <exception cref="ArgumentException"><paramref name="serviceVersion"/> est vide.</exception>
    public HandshakeHandler(string serviceVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceVersion);
        _serviceVersion = serviceVersion;
    }

    /// <summary>
    /// Lit le premier message, le valide et répond.
    /// </summary>
    /// <returns>
    /// <c>true</c> si la connexion peut servir, <c>false</c> si l'appelant doit être
    /// déconnecté. Dans ce second cas, une réponse d'erreur actionnable a déjà été émise.
    /// </returns>
    public async Task<bool> PerformAsync(
        Stream stream,
        ICallerIdentity callerIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(callerIdentity);

        byte[]? raw = await MessageFraming.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        if (raw is null)
        {
            // Fin de flux propre avant toute poignee de main : rien a repondre.
            return false;
        }

        MessageEnvelope envelope;
        try
        {
            envelope = MessageSerializer.Deserialize(raw);
        }
        catch (JsonException)
        {
            // Un message illisible ne porte meme pas d'identifiant de correlation
            // exploitable : on repond sur un identifiant vide plutot que de rien dire.
            await SendErrorAsync(
                stream, Guid.Empty, ErrorCode.ValidationFailed,
                "Message de poignée de main illisible.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        HandshakeResult result = HandshakeValidator.Validate(envelope);

        if (result.ShouldCloseConnection)
        {
            await SendErrorAsync(
                stream, envelope.Id, result.ErrorCode ?? ErrorCode.ValidationFailed,
                result.Diagnostic ?? "Poignée de main refusée.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        // L'elevation est CONSTATEE sur le jeton reel de l'appelant, jamais lue dans le
        // message : HelloPayload ne porte pas ce champ, et un message qui l'ajouterait
        // aurait deja ete rejete comme champ inconnu.
        HelloResultPayload payload = HandshakeValidator.CreateResult(
            _serviceVersion, callerIdentity.IsElevatedAdministrator);

        MessageEnvelope response = MessageEnvelope.CreateResult(
            envelope.Id, MessageTypes.Hello + MessageTypes.ResultSuffix, payload);

        await MessageFraming.WriteAsync(
            stream, MessageSerializer.SerializeToUtf8Bytes(response), cancellationToken).ConfigureAwait(false);

        return true;
    }

    private static async Task SendErrorAsync(
        Stream stream,
        Guid requestId,
        ErrorCode code,
        string message,
        CancellationToken cancellationToken)
    {
        MessageEnvelope error = MessageEnvelope.CreateError(requestId, code, message);

        await MessageFraming.WriteAsync(
            stream, MessageSerializer.SerializeToUtf8Bytes(error), cancellationToken).ConfigureAwait(false);
    }
}
