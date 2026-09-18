namespace NetworkLimiter.Core.Shaping;

/// <summary>Un paquet en attente de réinjection.</summary>
/// <param name="Token">Référence opaque vers le paquet, gérée par l'appelant.</param>
/// <param name="SizeBytes">Taille du paquet, en octets.</param>
/// <param name="EnqueuedTimestamp">Horodatage d'entrée en file.</param>
public readonly record struct PendingPacket(long Token, int SizeBytes, long EnqueuedTimestamp);

/// <summary>
/// File bornée de paquets en attente de réinjection.
/// </summary>
/// <remarks>
/// <para>
/// C'est ici, et nulle part ailleurs, que la mise en forme perd volontairement un paquet. Le
/// rejet est donc <b>compté</b> et remonté jusqu'à l'interface (FR-003b) : une perte
/// silencieuse ferait conclure à l'utilisateur que l'outil dégrade son réseau au hasard.
/// </para>
/// <para>
/// La file est bornée en nombre <b>et</b> en octets. Sans borne, une application saturant sa
/// limite ferait croître la file indéfiniment : la mémoire du service exploserait, et les
/// paquets finiraient par être réinjectés si tard que les connexions expireraient — une
/// défaillance bien pire que la perte d'un paquet.
/// </para>
/// <para>
/// Le rejet est en <b>queue</b> : c'est le paquet le plus récent qui est refusé, jamais un
/// paquet déjà accepté. Rejeter en tête casserait l'ordre des paquets déjà admis et
/// provoquerait des retransmissions inutiles.
/// </para>
/// </remarks>
public sealed class DelayQueue
{
    private readonly Queue<PendingPacket> _packets;
    private readonly int _maxPackets;
    private readonly long _maxBytes;

    private long _queuedBytes;

    /// <summary>Crée une file de retard.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Une borne n'est pas positive.</exception>
    public DelayQueue(int maxPackets = 1024, long maxBytes = 4 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxPackets, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxBytes, 0);

        _maxPackets = maxPackets;
        _maxBytes = maxBytes;
        _packets = new Queue<PendingPacket>(Math.Min(maxPackets, 256));
    }

    /// <summary>Nombre de paquets en attente.</summary>
    public int Count => _packets.Count;

    /// <summary>Volume en attente, en octets.</summary>
    public long QueuedBytes => _queuedBytes;

    /// <summary>Nombre de paquets rejetés depuis la création ou la dernière remise à zéro.</summary>
    public long DroppedPackets { get; private set; }

    /// <summary>Indique si la file ne peut plus accepter un paquet de cette taille.</summary>
    public bool IsFullFor(int sizeBytes) =>
        _packets.Count >= _maxPackets || _queuedBytes + sizeBytes > _maxBytes;

    /// <summary>
    /// Met un paquet en attente.
    /// </summary>
    /// <returns><c>false</c> si la file est pleine ; le paquet est alors rejeté et compté.</returns>
    /// <exception cref="ArgumentOutOfRangeException">La taille n'est pas positive.</exception>
    public bool TryEnqueue(PendingPacket packet)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(packet.SizeBytes, 0);

        if (IsFullFor(packet.SizeBytes))
        {
            DroppedPackets++;
            return false;
        }

        _packets.Enqueue(packet);
        _queuedBytes += packet.SizeBytes;

        return true;
    }

    /// <summary>Retire le paquet le plus ancien.</summary>
    public bool TryDequeue(out PendingPacket packet)
    {
        if (!_packets.TryDequeue(out packet))
        {
            return false;
        }

        _queuedBytes -= packet.SizeBytes;
        return true;
    }

    /// <summary>Consulte le paquet le plus ancien sans le retirer.</summary>
    public bool TryPeek(out PendingPacket packet) => _packets.TryPeek(out packet);

    /// <summary>Compte un rejet décidé en amont, sans passage par la file.</summary>
    /// <remarks>
    /// Cas du trafic descendant sans contrôle de congestion (FR-003a) : le rejet y est le
    /// mécanisme même de la limite, pas un débordement. Il doit néanmoins être compté, sans
    /// quoi l'interface ne pourrait pas en afficher la proportion (FR-003b).
    /// </remarks>
    public void CountExternalDrop() => DroppedPackets++;

    /// <summary>Vide la file et remet le compteur de rejets à zéro.</summary>
    public void Clear()
    {
        _packets.Clear();
        _queuedBytes = 0;
        DroppedPackets = 0;
    }
}
