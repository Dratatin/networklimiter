using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Core.Classification;
using NetworkLimiter.Core.Rules;
using NetworkLimiter.Core.Shaping;
using NetworkLimiter.Service.Health;
using NetworkLimiter.Service.Interception;
using NetworkLimiter.Service.Persistence;
using NetworkLimiter.Service.ProcessIdentity;
using NetworkLimiter.Service.Safety;
using Serilog.Core;

namespace NetworkLimiter.Integration.Tests.Stories;

/// <summary>
/// Monte le pipeline réel avec des intercepteurs simulés.
/// </summary>
/// <remarks>
/// <para>
/// Tout ce qui est éprouvé ici est le <b>code de production</b> : <c>ShapingPipeline</c>,
/// <c>ShapingCoordinator</c>, <c>RuleStore</c>, <c>PacketShaper</c>, la table de flux et la
/// résolution de règles, câblés comme <c>InterceptionWorker</c> les câble. Seules les deux
/// extrémités sont remplacées — le pilote, qui exige des privilèges administrateur, et le
/// système d'exploitation pour l'identité des processus.
/// </para>
/// <para>
/// C'est l'assemblage que les tests unitaires ne couvrent pas : chaque pièce était vérifiée
/// isolément bien avant que la chaîne complète ne voie son premier paquet.
/// </para>
/// </remarks>
internal sealed class ShapingHarness : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "nl-shaping-" + Guid.NewGuid().ToString("N"));

    private readonly FakeInterceptor _flow = new();
    private readonly FakeInterceptor _network = new();

    public ShapingHarness()
    {
        Directory.CreateDirectory(_directory);

        // L'instant de depart n'a aucune importance — seuls les ecarts comptent — et
        // FakeTimeProvider refuse de reculer sous le sien, son horodatage etant monotone par
        // contrat.
        Clock = new FakeTimeProvider();

        var configStore = new ConfigStore(Path.Combine(_directory, "config.json"), Clock);
        Rules = new RuleStore(configStore, PersistedConfig.CreateDefault());

        Shaper = new PacketShaper(Clock);
        Resolver = new RuleResolver();
        Processes = new StubProcessInfoProvider();

        Coordinator = new ShapingCoordinator(Shaper, Resolver, new AlwaysExistsProbe());

        var state = new ServiceStateMachine(new NoOpCloser(), Clock);

        Pipeline = new ShapingPipeline(
            _flow,
            _network,
            new Service.FlowTable.FlowTable(Clock),
            new ProcessIdentityResolver(Processes, new PathNormalizer(new NoDeviceResolver())),
            Resolver,
            Shaper,
            new QueuePressureMonitor(Clock, threshold: 0.9, sustainedFor: TimeSpan.FromSeconds(2)),
            state,
            Logger.None);

        // Meme cablage que le service : une ecriture de regle declenche l'application.
        Rules.Changed += (_, config) => Coordinator.Apply(config.ActiveProfile.Rules);
        Coordinator.Apply(Rules.ActiveRules);

        Pipeline.Start();
    }

    public FakeTimeProvider Clock { get; }

    public RuleStore Rules { get; }

    public PacketShaper Shaper { get; }

    public RuleResolver Resolver { get; }

    public ShapingCoordinator Coordinator { get; }

    public ShapingPipeline Pipeline { get; }

    public StubProcessInfoProvider Processes { get; }

    /// <summary>Paquets réinjectés depuis le début, dans l'ordre.</summary>
    public IReadOnlyList<byte[]> Reinjected => _network.Sent;

    /// <summary>Déclare un flux établi pour un processus, comme le ferait la couche FLOW.</summary>
    public void EstablishFlow(uint processId, string localAddress, ushort localPort, string remoteAddress, ushort remotePort)
    {
        var data = new WinDivertFlowData
        {
            EndpointId = processId,
            ProcessId = processId,
            LocalAddr0 = ToWord(localAddress),
            RemoteAddr0 = ToWord(remoteAddress),
            LocalPort = localPort,
            RemotePort = remotePort,
            Protocol = 6,
        };

        uint bits = WinDivertAddress.PackBits(
            WinDivertLayer.Flow, WinDivertEvent.FlowEstablished, sniffed: true, outbound: true);

        _flow.Enqueue([], [WinDivertAddress.FromFlowData(bits, data)]);
    }

    /// <summary>
    /// Fait descendre un paquet et attend qu'il soit traité.
    /// </summary>
    /// <returns><c>true</c> s'il a été réinjecté, <c>false</c> s'il a été retenu ou rejeté.</returns>
    public bool DeliverInbound(string fromAddress, ushort fromPort, string toAddress, ushort toPort, int sizeBytes)
    {
        int before = _network.Sent.Count;

        byte[] packet = BuildTcpPacket(fromAddress, fromPort, toAddress, toPort, sizeBytes);

        uint bits = WinDivertAddress.PackBits(WinDivertLayer.Network, WinDivertEvent.NetworkPacket);

        _network.Enqueue(packet, [WinDivertAddress.FromRaw(Clock.GetTimestamp(), bits)]);
        _network.WaitForProcessed();

        return _network.Sent.Count > before;
    }

    /// <summary>Attend que la couche FLOW ait absorbé tout ce qui lui a été remis.</summary>
    public void WaitForFlows() => _flow.WaitForProcessed();

    /// <summary>Construit un paquet IPv4 + TCP complet, tel que le parseur l'attend.</summary>
    private static byte[] BuildTcpPacket(string source, ushort sourcePort, string destination, ushort destinationPort, int totalBytes)
    {
        const int HeaderBytes = 40;

        int length = Math.Max(totalBytes, HeaderBytes);
        byte[] packet = new byte[length];

        packet[0] = 0x45;                                              // IPv4, en-tête de 5 mots
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)length);
        packet[8] = 64;                                                // TTL
        packet[9] = 6;                                                 // TCP

        IPAddress.Parse(source).GetAddressBytes().CopyTo(packet, 12);
        IPAddress.Parse(destination).GetAddressBytes().CopyTo(packet, 16);

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), destinationPort);

        packet[32] = 0x50;                                             // en-tête TCP de 5 mots

        return packet;
    }

    private static uint ToWord(string address) =>
        BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(address).GetAddressBytes());

    public void Dispose()
    {
        // Pipeline.Dispose ferme deja les intercepteurs, mais les liberer explicitement rend
        // la propriete evidente a la relecture — et vraie si le pipeline changeait d'avis.
        Pipeline.Dispose();
        _flow.Dispose();
        _network.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Nettoyage de confort.
        }
    }

    /// <summary>Intercepteur simulé : une file d'entrée, un journal de sortie.</summary>
    internal sealed class FakeInterceptor : IPacketInterceptor
    {
        private readonly BlockingCollection<(byte[] Packets, WinDivertAddress[] Addresses)> _incoming = [];
        private readonly List<byte[]> _sent = [];
        private readonly Lock _gate = new();
        private readonly ManualResetEventSlim _drained = new(initialState: true);

        private int _pending;
        private int _awaitingAck;

        public bool IsOpen { get; private set; }

        public IReadOnlyList<byte[]> Sent
        {
            get
            {
                lock (_gate)
                {
                    return [.. _sent];
                }
            }
        }

        public void Open() => IsOpen = true;

        public void Enqueue(byte[] packets, WinDivertAddress[] addresses)
        {
            Interlocked.Increment(ref _pending);
            _drained.Reset();
            _incoming.Add((packets, addresses));
        }

        /// <summary>
        /// Attend que la boucle ait consommé tout ce qui a été remis.
        /// </summary>
        /// <remarks>
        /// Attente sur signal, jamais sur durée : le test avance dès que le travail est fait,
        /// et le délai n'est là que pour transformer un blocage en échec nommé plutôt qu'en
        /// suite de tests suspendue.
        /// </remarks>
        public void WaitForProcessed()
        {
            if (!_drained.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("La boucle d'interception n'a pas consommé le lot remis.");
            }
        }

        public int Receive(Span<byte> packetBuffer, Span<WinDivertAddress> addresses, out int bytesReceived)
        {
            bytesReceived = 0;

            // Revenir ici prouve que le lot precedent est ENTIEREMENT traite : la boucle ne
            // redemande du travail qu'une fois le precedent fini. Signaler a la livraison
            // libererait le test avant que le paquet n'ait ete mis en forme, et les assertions
            // porteraient sur un etat qui n'existe pas encore.
            AcknowledgePrevious();

            (byte[] Packets, WinDivertAddress[] Addresses) batch;

            try
            {
                batch = _incoming.Take();
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                // File fermee : equivaut a un handle ferme, ce que la boucle traite comme un
                // arret propre.
                return 0;
            }

            batch.Packets.CopyTo(packetBuffer);
            bytesReceived = batch.Packets.Length;

            for (int index = 0; index < batch.Addresses.Length; index++)
            {
                addresses[index] = batch.Addresses[index];
            }

            Interlocked.Exchange(ref _awaitingAck, 1);

            return batch.Addresses.Length;
        }

        private void AcknowledgePrevious()
        {
            if (Interlocked.Exchange(ref _awaitingAck, 0) == 1 &&
                Interlocked.Decrement(ref _pending) == 0)
            {
                _drained.Set();
            }
        }

        public void Send(ReadOnlySpan<byte> packets, ReadOnlySpan<WinDivertAddress> addresses)
        {
            lock (_gate)
            {
                _sent.Add(packets.ToArray());
            }
        }

        private bool _closed;

        /// <summary>
        /// Ferme la file. Idempotent et silencieux, comme l'exige le contrat.
        /// </summary>
        /// <remarks>
        /// Ce garde n'est pas une commodité : <c>IPacketInterceptor.Close</c> impose d'être
        /// idempotent et de ne jamais lever, parce que c'est le geste qui rend le réseau et
        /// qu'il est appelé sur tous les chemins de sortie. Un double qui ne respecterait pas
        /// le contrat éprouverait autre chose que le produit.
        /// </remarks>
        public void Close()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            IsOpen = false;
            _incoming.CompleteAdding();
            _drained.Set();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Close();
            _disposed = true;

            _incoming.Dispose();
            _drained.Dispose();
        }

        private bool _disposed;
    }

    /// <summary>Associe un identifiant de processus à un chemin, sans toucher au système.</summary>
    internal sealed class StubProcessInfoProvider : IProcessInfoProvider
    {
        private readonly Dictionary<uint, string> _paths = [];

        public void Map(uint processId, string executablePath) => _paths[processId] = executablePath;

        public bool TryGetProcessInfo(uint processId, out ProcessInfo? info)
        {
            if (_paths.TryGetValue(processId, out string? path))
            {
                info = new ProcessInfo(path, processId, IsPackaged: false);
                return true;
            }

            info = null;
            return false;
        }
    }

    private sealed class AlwaysExistsProbe : IPathExistenceProbe
    {
        public bool Exists(string executablePath) => true;
    }

    private sealed class NoDeviceResolver : IDeviceVolumeResolver
    {
        public bool TryResolve(string devicePath, out string resolved)
        {
            resolved = string.Empty;
            return false;
        }
    }

    private sealed class NoOpCloser : IHandleCloser
    {
        public void CloseAll()
        {
            // Le banc controle lui-meme la fermeture ; le fail-open est couvert par ses
            // propres tests.
        }
    }
}
