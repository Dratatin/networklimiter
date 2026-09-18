using System.IO.Pipes;
using System.Runtime.Versioning;
using ILogger = Serilog.ILogger;

namespace NetworkLimiter.Service.Ipc;

/// <summary>
/// Accepte les connexions de l'interface et les sert en parallèle.
/// </summary>
/// <remarks>
/// <para>
/// Une instance de tuyau par connexion, quatre au maximum (contrat IPC). Chacune est servie sur
/// sa propre tâche : une interface qui bloque ne doit pas empêcher les autres de se connecter,
/// et surtout pas empêcher le service de continuer à limiter le trafic.
/// </para>
/// <para>
/// <b>L'IPC n'est jamais critique.</b> Si l'écoute échoue — nom déjà pris, ACL refusée — le
/// service continue de limiter : les règles sont déjà chargées et le pipeline tourne. Seule
/// l'interface devient injoignable, ce qui est gênant mais sans effet sur le réseau de
/// l'utilisateur (principe IV).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class IpcListener : IDisposable
{
    private readonly RequestDispatcher _dispatcher;
    private readonly StateBroadcaster _broadcaster;
    private readonly string _serviceVersion;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _acceptors = [];

    /// <summary>Crée l'écouteur.</summary>
    /// <exception cref="ArgumentNullException">Un argument est <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="serviceVersion"/> est vide.</exception>
    public IpcListener(
        RequestDispatcher dispatcher,
        StateBroadcaster broadcaster,
        string serviceVersion,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceVersion);
        ArgumentNullException.ThrowIfNull(log);

        _dispatcher = dispatcher;
        _broadcaster = broadcaster;
        _serviceVersion = serviceVersion;
        _log = log;
    }

    /// <summary>Démarre les boucles d'acceptation.</summary>
    public void Start()
    {
        for (int index = 0; index < PipeServer.MaxServerInstances; index++)
        {
            _acceptors.Add(Task.Run(() => AcceptLoopAsync(_stopping.Token)));
        }

        _log.Information(
            "Canal de contrôle ouvert : {Instances} instance(s) sur « {Tuyau} ».",
            PipeServer.MaxServerInstances,
            PipeServer.PipeName);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;

            try
            {
                pipe = PipeServer.Create();

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var connection = new ClientConnection(
                    pipe,
                    _dispatcher,
                    new HandshakeHandler(_serviceVersion),
                    new PipeCallerIdentity(pipe),
                    _log);

                // Le tuyau appartient desormais a la connexion, qui le fermera.
                pipe = null;

                await connection.ServeAsync(_broadcaster, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Une instance qui echoue ne doit pas emporter la boucle : la suivante
                // reessaiera. Sans cela, quatre echecs successifs rendraient le service
                // definitivement injoignable sans qu'il s'arrete pour autant.
                _log.Warning(exception, "Connexion refusée sur le canal de contrôle.");
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    /// <summary>Arrête l'écoute et ferme les connexions.</summary>
    public void Stop()
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        _stopping.Cancel();

        // Attente bornee : l'arret du service ne doit pas dependre d'un client qui traine.
        Task.WaitAll([.. _acceptors], TimeSpan.FromSeconds(3));

        _log.Information("Canal de contrôle fermé.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        _stopping.Dispose();
    }
}
