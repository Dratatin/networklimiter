using System.Net;
using System.Runtime.Versioning;
using NetworkLimiter.Core.Classification;
using NetworkLimiter.Core.Rules;
using NetworkLimiter.Core.Shaping;
using NetworkLimiter.Service.FlowTable;
using NetworkLimiter.Service.Health;
using NetworkLimiter.Service.ProcessIdentity;
using NetworkLimiter.Service.Safety;
using ILogger = Serilog.ILogger;

namespace NetworkLimiter.Service.Interception;

/// <summary>
/// Boucle d'interception : du paquet brut à la réinjection.
/// </summary>
/// <remarks>
/// <para>
/// Assemble la chaîne complète — paquet → flux → processus → application → règle → seau →
/// réinjection — sur deux boucles dédiées, une par couche WinDivert.
/// </para>
/// <para>
/// Deux <b>threads</b> et non des tâches : <c>WinDivertRecvEx</c> est un appel natif bloquant
/// qui monopoliserait un thread du pool pendant des secondes. La fermeture du handle est ce qui
/// débloque la réception et termine la boucle.
/// </para>
/// <para>
/// Toute défaillance de la boucle mène au fail-open : les handles sont fermés, les paquets en
/// attente libérés, et le trafic redevient libre. Un paquet qu'on ne sait pas classer est
/// réinjecté sans limitation — dans le doute, on rend le réseau à l'utilisateur (principe IV).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ShapingPipeline : IHandleCloser, IDisposable
{
    /// <summary>Nombre maximal de paquets lus en un appel, imposé par WinDivert.</summary>
    private const int BatchSize = 255;

    /// <summary>Taille du tampon de lot : de quoi accueillir des paquets de taille maximale.</summary>
    private const int PacketBufferBytes = BatchSize * 1500 * 2;

    private readonly IPacketInterceptor _flowInterceptor;
    private readonly IPacketInterceptor _networkInterceptor;
    private readonly FlowTable.FlowTable _flowTable;
    private readonly ProcessIdentityResolver _processResolver;
    private readonly RuleResolver _ruleResolver;
    private readonly PacketShaper _shaper;
    private readonly QueuePressureMonitor _pressure;
    private readonly ServiceStateMachine _state;
    private readonly ILogger _log;

    private readonly Lock _gate = new();
    private Thread? _flowThread;
    private Thread? _networkThread;
    private volatile bool _running;

    /// <summary>Crée la boucle d'interception.</summary>
    /// <exception cref="ArgumentNullException">Un argument est <c>null</c>.</exception>
    public ShapingPipeline(
        IPacketInterceptor flowInterceptor,
        IPacketInterceptor networkInterceptor,
        FlowTable.FlowTable flowTable,
        ProcessIdentityResolver processResolver,
        RuleResolver ruleResolver,
        PacketShaper shaper,
        QueuePressureMonitor pressure,
        ServiceStateMachine state,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(flowInterceptor);
        ArgumentNullException.ThrowIfNull(networkInterceptor);
        ArgumentNullException.ThrowIfNull(flowTable);
        ArgumentNullException.ThrowIfNull(processResolver);
        ArgumentNullException.ThrowIfNull(ruleResolver);
        ArgumentNullException.ThrowIfNull(shaper);
        ArgumentNullException.ThrowIfNull(pressure);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(log);

        _flowInterceptor = flowInterceptor;
        _networkInterceptor = networkInterceptor;
        _flowTable = flowTable;
        _processResolver = processResolver;
        _ruleResolver = ruleResolver;
        _shaper = shaper;
        _pressure = pressure;
        _state = state;
        _log = log;
    }

    /// <summary>Compteurs d'étape, pour savoir où la chaîne s'interrompt.</summary>
    public PipelineCounters Counters { get; } = new();

    /// <summary>Chemins normalisés des processus vus récemment, pour l'état de santé.</summary>
    public IReadOnlySet<string> RunningPaths => _runningPaths;

    private readonly HashSet<string> _runningPaths = new(StringComparer.Ordinal);

    /// <summary>Ouvre les handles et démarre les deux boucles.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_running)
            {
                return;
            }

            // La couche FLOW d'abord : elle alimente la table qui attribue les paquets aux
            // processus. L'ouvrir en second laisserait une fenetre pendant laquelle tout
            // paquet serait « de flux inconnu », donc non limite.
            _flowInterceptor.Open();
            _networkInterceptor.Open();

            _pressure.Reset();
            _running = true;

            _flowThread = StartLoop("NetworkLimiter.Flow", RunFlowLoop);
            _networkThread = StartLoop("NetworkLimiter.Network", RunNetworkLoop);

            _state.TransitionTo(ServiceState.Shaping);
            _log.Information("Interception démarrée.");
        }
    }

    /// <summary>
    /// Ferme les handles et libère tout le trafic.
    /// </summary>
    /// <remarks>
    /// Idempotent et silencieux : c'est le geste appelé sur tous les chemins de sortie, y
    /// compris depuis un gestionnaire d'exception (principe IV).
    /// </remarks>
    public void CloseAll()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;

            // Fermer les handles debloque les receptions natives en cours : sans cela, les
            // deux threads resteraient bloques indefiniment dans WinDivertRecvEx.
            _flowInterceptor.Close();
            _networkInterceptor.Close();

            JoinLoop(_flowThread);
            JoinLoop(_networkThread);

            _flowThread = null;
            _networkThread = null;

            ReleasePendingPackets();
            _flowTable.Clear();
            _processResolver.Clear();

            _log.Information("Interception arrêtée, trafic libéré.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CloseAll();
        _flowInterceptor.Dispose();
        _networkInterceptor.Dispose();
    }

    private static Thread StartLoop(string name, ThreadStart body)
    {
        var thread = new Thread(body)
        {
            Name = name,
            IsBackground = true,
        };

        thread.Start();
        return thread;
    }

    private static void JoinLoop(Thread? thread)
    {
        // Borne l'attente : un thread bloque dans un appel natif ne doit pas empecher le
        // service de s'arreter. Les threads sont en arriere-plan, donc ils ne retiennent pas
        // le processus.
        thread?.Join(TimeSpan.FromSeconds(5));
    }

    private void RunFlowLoop()
    {
        var addresses = new WinDivertAddress[BatchSize];

        try
        {
            while (_running)
            {
                int count = _flowInterceptor.Receive(Span<byte>.Empty, addresses, out _);

                if (count == 0)
                {
                    // Handle ferme ou arret en cours : la boucle se termine proprement.
                    ReportLoopExit("flux");
                    break;
                }

                for (int index = 0; index < count; index++)
                {
                    HandleFlowEvent(addresses[index]);
                }

                _state.ReportProgress();
            }
        }
        catch (Exception exception) when (_running)
        {
            Fail(exception, "flux");
        }
    }

    private void HandleFlowEvent(in WinDivertAddress address)
    {
        Counters.Add(PipelineCounter.FlowEvents);

        if (address.Layer != WinDivertLayer.Flow)
        {
            Counters.Add(PipelineCounter.FlowWrongLayer);
            return;
        }

        WinDivertFlowData flow = address.AsFlowData();
        FlowKey key = BuildFlowKey(flow, address.IPv6);

        if (address.Event == WinDivertEvent.FlowDeleted)
        {
            Counters.Add(PipelineCounter.FlowsDeleted);
            _flowTable.OnFlowDeleted(key);
            return;
        }

        Counters.Add(PipelineCounter.FlowsEstablished);
        _flowTable.OnFlowEstablished(key, flow.EndpointId, flow.ProcessId);

        // Resout l'identite tout de suite : le processus peut disparaitre avant le premier
        // paquet, et on perdrait alors la seule occasion de connaitre son chemin.
        ResolvedProcess? process = _processResolver.Resolve(flow.ProcessId);

        if (process is null)
        {
            Counters.Add(PipelineCounter.FlowIdentityUnknown);
            return;
        }

        lock (_runningPaths)
        {
            _runningPaths.Add(process.NormalizedPath);
        }
    }

    /// <summary>
    /// Reconstruit le quintuplet d'un événement de flux.
    /// </summary>
    /// <remarks>
    /// WinDivert stocke les adresses sur quatre mots de 32 bits, quelle que soit la famille.
    /// En IPv4, seul le premier est significatif — lire les quatre produirait une adresse
    /// absurde et aucun paquet ne serait jamais attribué à ce flux.
    /// </remarks>
    private static FlowKey BuildFlowKey(in WinDivertFlowData flow, bool isIPv6)
    {
        IPAddress local = ToAddress(flow.LocalAddr0, flow.LocalAddr1, flow.LocalAddr2, flow.LocalAddr3, isIPv6);
        IPAddress remote = ToAddress(flow.RemoteAddr0, flow.RemoteAddr1, flow.RemoteAddr2, flow.RemoteAddr3, isIPv6);

        return new FlowKey(flow.Protocol, local, flow.LocalPort, remote, flow.RemotePort);
    }

    private static IPAddress ToAddress(uint word0, uint word1, uint word2, uint word3, bool isIPv6)
    {
        if (!isIPv6)
        {
            // WinDivert rend l'adresse IPv4 en ordre hote ; IPAddress attend l'ordre reseau.
            Span<byte> octets = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(octets, word0);

            return new IPAddress(octets);
        }

        Span<byte> bytes = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes[12..], word0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes[8..12], word1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes[4..8], word2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes[..4], word3);

        return new IPAddress(bytes);
    }

    private void RunNetworkLoop()
    {
        byte[] packets = new byte[PacketBufferBytes];
        var addresses = new WinDivertAddress[BatchSize];
        var slices = new PacketSlice[BatchSize];
        var ready = new List<PendingPacket>();

        try
        {
            while (_running)
            {
                int count = _networkInterceptor.Receive(packets, addresses, out int bytesReceived);

                if (count == 0)
                {
                    ReportLoopExit("réseau");
                    break;
                }

                _pressure.RecordBatch(count, BatchSize);

                // Ne plus limiter plutot que laisser le noyau rejeter en silence : au-dela du
                // seuil, on rend la main (research.md R-004, principe IV).
                if (_pressure.ShouldReleaseTraffic)
                {
                    _log.Warning(
                        "Pression de file soutenue ({Pressure:P0}) : le trafic est libéré.",
                        _pressure.Pressure);

                    _state.Fail(new InvalidOperationException(
                        "La boucle de drainage ne suit plus le débit entrant."));
                    return;
                }

                int sliceCount = PacketBatchReader.Split(packets, bytesReceived, slices);

                for (int index = 0; index < sliceCount && index < count; index++)
                {
                    ProcessPacket(packets, slices[index], addresses[index]);
                }

                // Reinjecte ce dont le budget est revenu, dans l'ordre d'arrivee.
                ready.Clear();
                _shaper.DrainReady(ready);
                _shaper.DrainReleased(ready);
                ReinjectPending(ready);

                _state.ReportProgress();
            }
        }
        catch (Exception exception) when (_running)
        {
            Fail(exception, "réseau");
        }
    }

    private void ProcessPacket(byte[] packets, in PacketSlice slice, in WinDivertAddress address)
    {
        ReadOnlySpan<byte> packet = packets.AsSpan(slice.Offset, slice.Length);

        Counters.Add(PipelineCounter.PacketsReceived);

        // Un paquet deja reinjecte par un autre outil WinDivert ne doit pas etre mis en forme
        // une seconde fois : son debit serait divise deux fois.
        if (address.Impostor || address.Loopback)
        {
            Counters.Add(PipelineCounter.PacketsBypassed);
            Reinject(packet, address);
            return;
        }

        PacketHeader? header = PacketHeaderParser.TryParse(packet);

        if (header is null)
        {
            Counters.Add(PipelineCounter.PacketsUnreadable);

            // Paquet illisible : on laisse passer. Le retenir serait pire que ne pas le
            // limiter (principe IV).
            Reinject(packet, address);
            return;
        }

        PacketDirection direction = address.Outbound ? PacketDirection.Outbound : PacketDirection.Inbound;
        NetworkScope scope = NetworkScopeClassifier.Classify(
            direction == PacketDirection.Outbound ? header.DestinationAddress : header.SourceAddress);

        if (scope != NetworkScope.Internet)
        {
            Counters.Add(PipelineCounter.ScopeExcluded);
        }

        Guid? ruleId = ResolveRule(header, direction);

        Counters.Add(ruleId is null ? PipelineCounter.RuleUnmatched : PipelineCounter.RuleMatched);

        // Le jeton est calcule une fois et transmis explicitement. Le deduire d'un champ
        // partage au moment de la mise en attente fonctionnerait par coincidence — tant que
        // les deux appels restent adjacents sur le meme thread — et casserait silencieusement
        // a la premiere reorganisation.
        long token = Interlocked.Increment(ref _nextToken);

        var request = new ShapingRequest(
            Token: token,
            RuleId: ruleId,
            Direction: direction,
            Protocol: header.Transport,
            Scope: scope,
            SizeBytes: slice.Length);

        switch (_shaper.Evaluate(request))
        {
            case ShapingOutcome.PassThrough:
                Counters.Add(PipelineCounter.Passed);
                Reinject(packet, address);
                break;

            case ShapingOutcome.Send:
                Counters.Add(PipelineCounter.Sent);
                Reinject(packet, address);
                break;

            case ShapingOutcome.Delay:
                Counters.Add(PipelineCounter.Delayed);

                // Le tampon de lot sera reutilise au prochain appel : le paquet retenu doit
                // etre copie, sinon il serait ecrase avant sa reinjection.
                _delayed[token] = (packet.ToArray(), address);
                break;

            case ShapingOutcome.Drop:
                // Ne rien faire : un paquet non reinjecte est perdu, ce qui EST le mecanisme
                // de la limite pour le trafic sans controle de congestion (FR-003a).
                Counters.Add(PipelineCounter.Dropped);
                break;

            default:
                Counters.Add(PipelineCounter.Passed);
                Reinject(packet, address);
                break;
        }
    }

    private Guid? ResolveRule(PacketHeader header, PacketDirection direction)
    {
        // Le quintuplet de la table est oriente « local / distant ». Un paquet sortant a sa
        // source en local ; un paquet entrant l'a en destination. Confondre les deux ferait
        // echouer toutes les recherches dans un sens.
        (IPAddress local, ushort localPort, IPAddress remote, ushort remotePort) =
            direction == PacketDirection.Outbound
                ? (header.SourceAddress, header.SourcePort, header.DestinationAddress, header.DestinationPort)
                : (header.DestinationAddress, header.DestinationPort, header.SourceAddress, header.SourcePort);

        var key = new FlowKey(header.Protocol, local, localPort, remote, remotePort);

        if (!_flowTable.TryGetProcessId(key, out uint processId))
        {
            // Course entre les deux couches : le paquet est arrive avant que son flux ne soit
            // connu. Il passe sans limitation.
            Counters.Add(PipelineCounter.FlowUnmatched);
            return null;
        }

        Counters.Add(PipelineCounter.FlowMatched);

        ResolvedProcess? process = _processResolver.Resolve(processId);

        if (process is null)
        {
            return null;
        }

        var identity = new AppIdentity(process.NormalizedPath, process.ExecutableName, process.ExecutableName);

        return _ruleResolver.Resolve(identity)?.RuleId;
    }

    private void Reinject(ReadOnlySpan<byte> packet, in WinDivertAddress address)
    {
        Span<WinDivertAddress> single = stackalloc WinDivertAddress[1];
        single[0] = address;

        _networkInterceptor.Send(packet, single);
    }

    private void ReinjectPending(List<PendingPacket> pending)
    {
        foreach (PendingPacket packet in pending)
        {
            if (_delayed.Remove(packet.Token, out (byte[] Bytes, WinDivertAddress Address) stored))
            {
                _networkInterceptor.Send(stored.Bytes, new ReadOnlySpan<WinDivertAddress>(in stored.Address));
            }
        }
    }

    private readonly Dictionary<long, (byte[] Bytes, WinDivertAddress Address)> _delayed = [];
    private long _nextToken;

    private void ReleasePendingPackets()
    {
        var released = new List<PendingPacket>();
        _shaper.ReleaseAll(released);

        // Les paquets retenus sont reinjectes sans limitation : les abandonner serait une
        // perte silencieuse au moment precis ou l'on cesse de limiter.
        foreach (PendingPacket packet in released)
        {
            if (_delayed.Remove(packet.Token, out (byte[] Bytes, WinDivertAddress Address) stored))
            {
                TryReinjectQuietly(stored);
            }
        }

        _delayed.Clear();
    }

    private void TryReinjectQuietly((byte[] Bytes, WinDivertAddress Address) stored)
    {
        try
        {
            _networkInterceptor.Send(stored.Bytes, new ReadOnlySpan<WinDivertAddress>(in stored.Address));
        }
        catch (InterceptionUnavailableException)
        {
            // Le handle est deja ferme : le paquet est perdu, mais le trafic est libre. C'est
            // le compromis acceptable, l'inverse ne l'est pas.
        }
    }

    /// <summary>
    /// Signale la fin d'une boucle de réception.
    /// </summary>
    /// <remarks>
    /// Se taire ici a coûté une session de diagnostic entière : la couche <c>FLOW</c> s'était
    /// arrêtée sans un mot, pendant que le service continuait d'annoncer « interception
    /// active ». Une boucle morte qui n'est pas dite est une limitation qui ne s'applique
    /// jamais, sans que rien ne l'explique. Pendant un arrêt demandé, c'est attendu et la
    /// mention reste discrète ; hors arrêt, c'est un avertissement.
    /// </remarks>
    private void ReportLoopExit(string loop)
    {
        if (_running)
        {
            _log.Warning(
                "Boucle {Loop} terminée alors que l'interception est censée être active. " +
                "Les paquets ne seront plus attribués.",
                loop);

            return;
        }

        _log.Debug("Boucle {Loop} terminée sur fermeture du handle.", loop);
    }

    private void Fail(Exception exception, string loop)
    {
        _log.Error(exception, "Boucle {Loop} interrompue. Le trafic est libéré.", loop);
        _state.Fail(exception);
    }
}
