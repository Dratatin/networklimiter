using System.IO.Pipes;
using System.Runtime.Versioning;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Contracts.Serialization;
using ILogger = Serilog.ILogger;

namespace NetworkLimiter.Service.Ipc;

/// <summary>
/// Sert une connexion cliente, de la poignée de main à la déconnexion.
/// </summary>
/// <remarks>
/// <para>
/// Deux sources écrivent sur le même tuyau : les réponses aux requêtes et les notifications
/// poussées. Sans sérialisation, deux messages s'entrelaceraient et le cadrage volerait en
/// éclats — le client lirait une longueur suivie des octets d'un autre message. D'où le
/// sémaphore d'écriture, qui est la raison d'être de cette classe autant que la boucle de
/// lecture.
/// </para>
/// <para>
/// Toute déconnexion est un <b>événement courant</b>, jamais une défaillance : l'interface se
/// ferme, la session se termine, le processus est tué. Les exceptions correspondantes sont
/// absorbées ici, sinon un utilisateur non privilégié arrêterait la boucle d'acceptation du
/// service en ouvrant et fermant des connexions.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ClientConnection : IBroadcastTarget, IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly RequestDispatcher _dispatcher;
    private readonly HandshakeHandler _handshake;
    private readonly ICallerIdentity _caller;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Crée une connexion autour d'un tuyau déjà accepté.</summary>
    /// <exception cref="ArgumentNullException">Un argument est <c>null</c>.</exception>
    public ClientConnection(
        NamedPipeServerStream pipe,
        RequestDispatcher dispatcher,
        HandshakeHandler handshake,
        ICallerIdentity caller,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(handshake);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(log);

        _pipe = pipe;
        _dispatcher = dispatcher;
        _handshake = handshake;
        _caller = caller;
        _log = log;
    }

    /// <summary>
    /// Sert la connexion jusqu'à sa fermeture.
    /// </summary>
    /// <returns><c>true</c> si la poignée de main a réussi et que la connexion a servi.</returns>
    public async Task<bool> ServeAsync(StateBroadcaster broadcaster, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);

        if (!await _handshake.PerformAsync(_pipe, _caller, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // Inscrit APRES la poignee de main : un client dont la version est incompatible ne doit
        // pas recevoir de notifications dans un format qu'il ne sait pas lire.
        broadcaster.Add(this);

        try
        {
            await ReadLoopAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            broadcaster.Remove(this);
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _pipe.IsConnected)
        {
            byte[]? raw;

            try
            {
                raw = await ReadWithTimeoutAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (MessageTooLargeException exception)
            {
                // Le contrat impose la fermeture SANS lecture : lire un message surdimensionne
                // pour pouvoir repondre poliment serait exactement l'abus a eviter.
                _log.Warning(
                    "Connexion fermée : message de {Octets} octets annoncé.",
                    exception.AnnouncedBytes);

                return;
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException or EndOfStreamException
                          or OperationCanceledException)
            {
                return;
            }

            if (raw is null)
            {
                return;
            }

            MessageEnvelope response;

            try
            {
                MessageEnvelope request = MessageSerializer.Deserialize(raw);

                // L'elevation est relue A CHAQUE message, jamais retenue depuis la poignee de
                // main : c'est ce qui empeche une elevation ponctuelle de devenir un droit
                // permanent sur la duree de la connexion.
                response = _dispatcher.Dispatch(request, _caller.IsElevatedAdministrator);
            }
            catch (System.Text.Json.JsonException exception)
            {
                // Message illisible : on repond une erreur sans identifiant de correlation
                // plutot que de fermer. Fermer priverait un client d'une explication utile
                // pour un simple defaut d'encodage.
                response = MessageEnvelope.CreateError(
                    Guid.Empty, ErrorCode.ValidationFailed, $"Message illisible : {exception.Message}");
            }

            if (!await TryWriteAsync(response, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task<byte[]?> ReadWithTimeoutAsync(CancellationToken cancellationToken)
    {
        // Le contrat fixe un delai d'inactivite de 30 s cote serveur. Il borne le nombre
        // d'instances qu'un client inerte peut immobiliser — elles ne sont que quatre.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PipeServer.IdleTimeout);

        return await MessageFraming.ReadAsync(_pipe, timeout.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask PushAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        await TryWriteAsync(envelope, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryWriteAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await MessageFraming.WriteAsync(
                _pipe, MessageSerializer.SerializeToUtf8Bytes(envelope), cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _writeLock.Dispose();
        _pipe.Dispose();
    }
}
