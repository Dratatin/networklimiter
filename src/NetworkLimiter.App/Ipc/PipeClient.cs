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
    private readonly string _pipeName;
    private readonly string _clientVersion;
    private NamedPipeClientStream? _stream;

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

        return new ConnectionResult(State, payload.Elevated, payload.ServiceVersion, null);
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
        State = ConnectionState.Disconnected;
    }
}
