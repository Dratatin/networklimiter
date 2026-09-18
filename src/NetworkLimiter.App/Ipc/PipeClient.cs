using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Contracts.Serialization;

namespace NetworkLimiter.App.Ipc;

/// <summary>État de la liaison avec le service.</summary>
public enum ConnectionState
{
    /// <summary>Aucune connexion en cours.</summary>
    Disconnected,

    /// <summary>Connexion et poignée de main en cours.</summary>
    Connecting,

    /// <summary>Connecté, poignée de main réussie.</summary>
    Connected,

    /// <summary>Le service est injoignable — arrêté, non installé, ou en panne.</summary>
    ServiceUnavailable,

    /// <summary>Le service parle une version de protocole incompatible.</summary>
    VersionMismatch,
}

/// <summary>Résultat d'une tentative de connexion.</summary>
/// <param name="State">État résultant.</param>
/// <param name="Elevated">Élévation constatée par le service pour cette connexion.</param>
/// <param name="ServiceVersion">Version du service, si la poignée de main a abouti.</param>
/// <param name="Diagnostic">Message affichable en cas d'échec.</param>
public sealed record ConnectionResult(
    ConnectionState State,
    bool Elevated,
    string? ServiceVersion,
    string? Diagnostic);

/// <summary>
/// Client du canal de contrôle, côté interface.
/// </summary>
/// <remarks>
/// <para>
/// L'interface s'exécute sans élévation. Ce client n'a donc aucun privilège propre : tout ce
/// qu'il peut faire est borné par ce que le service accepte de sa part.
/// </para>
/// <para>
/// L'indisponibilité du service n'est pas une erreur exceptionnelle mais un <b>état normal</b>
/// à afficher : service arrêté, pilote non chargé, machine hors matrice. Les échecs sont donc
/// traduits en <see cref="ConnectionState"/> plutôt que propagés en exceptions, pour que
/// l'interface puisse toujours expliquer la situation (principe VI).
/// </para>
/// </remarks>
public sealed class PipeClient : IAsyncDisposable
{
    /// <summary>
    /// Période d'interrogation de l'état.
    /// </summary>
    /// <remarks>
    /// Confortablement sous le délai d'inactivité de 30 s du contrat, qui ferme les connexions
    /// muettes. Elle sert donc deux fins d'un seul geste : maintenir la connexion, et rattraper
    /// une notification poussée qui se serait perdue. Une interface qui n'afficherait l'état que
    /// sur notification resterait figée sans jamais le dire.
    /// </remarks>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly string _pipeName;
    private readonly string _clientVersion;
    private readonly Dictionary<Guid, TaskCompletionSource<MessageEnvelope>> _pending = [];
    private readonly Lock _pendingGate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private NamedPipeClientStream? _stream;
    private CancellationTokenSource? _reading;

    /// <summary>Crée un client.</summary>
    public PipeClient(string pipeName = "NetworkLimiter.v1", string clientVersion = "1.0.0")
    {
        _pipeName = pipeName;
        _clientVersion = clientVersion;
    }

    /// <summary>État courant de la liaison.</summary>
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    /// <summary>Élévation constatée par le service pour cette connexion.</summary>
    public bool IsElevated { get; private set; }

    /// <summary>
    /// Se connecte au service et exécute la poignée de main.
    /// </summary>
    public async Task<ConnectionResult> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await DisposeStreamAsync().ConfigureAwait(false);
        State = ConnectionState.Connecting;

        try
        {
            _stream = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            await _stream.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);

            return await PerformHandshakeAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await FailAsync(
                ConnectionState.ServiceUnavailable,
                "Le service NetworkLimiter ne répond pas. Vérifiez qu'il est démarré.").ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            return await FailAsync(
                ConnectionState.ServiceUnavailable,
                $"Liaison interrompue avec le service : {exception.Message}").ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            return await FailAsync(
                ConnectionState.ServiceUnavailable,
                "Accès au canal de contrôle refusé.").ConfigureAwait(false);
        }
    }

    private async Task<ConnectionResult> PerformHandshakeAsync(CancellationToken cancellationToken)
    {
        MessageEnvelope hello = MessageEnvelope.CreateRequest(
            MessageTypes.Hello,
            new HelloPayload
            {
                ProtocolVersion = ProtocolVersion.Current,
                ClientVersion = _clientVersion,
            });

        await MessageFraming.WriteAsync(
            _stream!, MessageSerializer.SerializeToUtf8Bytes(hello), cancellationToken).ConfigureAwait(false);

        byte[]? raw = await MessageFraming.ReadAsync(_stream!, cancellationToken).ConfigureAwait(false);
        if (raw is null)
        {
            return await FailAsync(
                ConnectionState.ServiceUnavailable,
                "Le service a fermé la connexion sans répondre.").ConfigureAwait(false);
        }

        MessageEnvelope response;
        try
        {
            response = MessageSerializer.Deserialize(raw);
        }
        catch (JsonException exception)
        {
            return await FailAsync(
                ConnectionState.VersionMismatch,
                $"Réponse du service illisible : {exception.Message}").ConfigureAwait(false);
        }

        if (response.Ok != true)
        {
            ConnectionState state = response.Error?.Code == ErrorCode.ProtocolVersionMismatch
                ? ConnectionState.VersionMismatch
                : ConnectionState.ServiceUnavailable;

            return await FailAsync(state, response.Error?.Message ?? "Poignée de main refusée.")
                .ConfigureAwait(false);
        }

        HelloResultPayload payload = MessageSerializer.ReadPayload<HelloResultPayload>(response);

        State = ConnectionState.Connected;
        IsElevated = payload.Elevated;

        // La boucle de lecture ne demarre qu'ICI, la poignee de main terminee : elle est
        // sequentielle par nature, et un lecteur concurrent lui volerait sa reponse.
        StartReading();

        return new ConnectionResult(State, payload.Elevated, payload.ServiceVersion, null);
    }

    /// <summary>Émis pour tout message poussé par le service, hors réponses.</summary>
    public event EventHandler<MessageEnvelope>? Notification;

    /// <summary>Émis quand la liaison est rompue, quelle qu'en soit la cause.</summary>
    public event EventHandler<string>? Disconnected;

    private void StartReading()
    {
        _reading = new CancellationTokenSource();
        CancellationToken token = _reading.Token;

        _ = Task.Run(() => ReadLoopAsync(token), token);
    }

    /// <summary>
    /// Boucle de lecture unique, qui répartit par identifiant de corrélation.
    /// </summary>
    /// <remarks>
    /// C'est la seule conception correcte ici. Le service pousse <c>StateChanged</c> à tout
    /// moment : un simple « écrire puis lire » finirait tôt ou tard par prendre une
    /// notification pour la réponse attendue, et l'interface afficherait un résultat qui
    /// répond à une autre question.
    /// </remarks>
    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        string reason = "Liaison fermée.";

        try
        {
            while (!cancellationToken.IsCancellationRequested && _stream is { IsConnected: true })
            {
                byte[]? raw = await MessageFraming.ReadAsync(_stream, cancellationToken).ConfigureAwait(false);

                if (raw is null)
                {
                    reason = "Le service a fermé la connexion.";
                    break;
                }

                Deliver(MessageSerializer.Deserialize(raw));
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or JsonException
                      or MessageTooLargeException)
        {
            reason = $"Liaison interrompue : {exception.Message}";
        }

        FailPending(reason);
        State = ConnectionState.ServiceUnavailable;
        Disconnected?.Invoke(this, reason);
    }

    private void Deliver(MessageEnvelope message)
    {
        TaskCompletionSource<MessageEnvelope>? waiter;

        lock (_pendingGate)
        {
            _pending.Remove(message.Id, out waiter);
        }

        if (waiter is not null)
        {
            waiter.TrySetResult(message);
            return;
        }

        // Aucun demandeur : c'est un message pousse. Un identifiant inconnu sur une REPONSE
        // serait en revanche anormal — il est traite comme une notification plutot que
        // silencieusement jete, pour rester observable.
        Notification?.Invoke(this, message);
    }

    private void FailPending(string reason)
    {
        List<TaskCompletionSource<MessageEnvelope>> waiters;

        lock (_pendingGate)
        {
            waiters = [.. _pending.Values];
            _pending.Clear();
        }

        // Aucune requete ne doit rester suspendue sur une connexion morte : l'interface
        // attendrait indefiniment une reponse qui ne viendra jamais, sans rien afficher.
        foreach (TaskCompletionSource<MessageEnvelope> waiter in waiters)
        {
            waiter.TrySetException(new IOException(reason));
        }
    }

    /// <summary>
    /// Envoie une requête et attend sa réponse.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> est <c>null</c>.</exception>
    /// <exception cref="IOException">La liaison est rompue.</exception>
    public async Task<MessageEnvelope> SendAsync(
        MessageEnvelope request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_stream is not { IsConnected: true })
        {
            throw new IOException("Non connecté au service.");
        }

        var waiter = new TaskCompletionSource<MessageEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pendingGate)
        {
            _pending[request.Id] = waiter;
        }

        try
        {
            // Meme raison que cote service : deux ecritures concurrentes entrelaceraient leurs
            // octets et detruiraient le cadrage, de facon parfaitement silencieuse.
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await MessageFraming.WriteAsync(
                    _stream, MessageSerializer.SerializeToUtf8Bytes(request), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            return await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_pendingGate)
            {
                _pending.Remove(request.Id);
            }

            throw;
        }
    }

    private async Task<ConnectionResult> FailAsync(ConnectionState state, string diagnostic)
    {
        await DisposeStreamAsync().ConfigureAwait(false);
        State = state;
        IsElevated = false;

        return new ConnectionResult(state, false, null, diagnostic);
    }

    private async ValueTask DisposeStreamAsync()
    {
        if (_reading is not null)
        {
            await _reading.CancelAsync().ConfigureAwait(false);
            _reading.Dispose();
            _reading = null;
        }

        FailPending("Liaison fermée.");

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisposeStreamAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        State = ConnectionState.Disconnected;
    }
}
