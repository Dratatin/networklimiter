using NetworkLimiter.Core.Shaping;

namespace NetworkLimiter.Core.Tests.Shaping;

/// <summary>
/// File de retard et politique de rejet — FR-003a, FR-003b, FR-003c.
/// </summary>
/// <remarks>
/// <para>
/// C'est le seul endroit du produit où un paquet est volontairement perdu. Deux propriétés y
/// sont donc essentielles : le rejet est <b>compté</b>, pour que l'interface puisse en afficher
/// la proportion (FR-003b), et il ne survient <b>jamais quand la temporisation suffit</b>
/// (FR-003c).
/// </para>
/// <para>
/// Le second point est celui qui distingue un limiteur utilisable d'un limiteur qui « casse
/// internet » : un utilisateur dont les téléchargements perdent des paquets alors qu'il
/// suffisait de les ralentir conclura, à raison, que l'outil dégrade son réseau.
/// </para>
/// </remarks>
public sealed class DelayQueueTests
{
    private static PendingPacket Packet(long token, int size = 1500) => new(token, size, EnqueuedTimestamp: 0);

    // -- Comportement de file -------------------------------------------------

    [Fact]
    public void FileNeuve_EstVideEtSansRejet()
    {
        var queue = new DelayQueue();

        queue.Count.Should().Be(0);
        queue.QueuedBytes.Should().Be(0);
        queue.DroppedPackets.Should().Be(0);
        queue.TryDequeue(out _).Should().BeFalse();
    }

    [Fact]
    public void LesPaquets_SortentDansLOrdreDEntree()
    {
        // Sortir dans le desordre provoquerait des retransmissions inutiles : TCP
        // interpreterait le desordre comme une perte.
        var queue = new DelayQueue();
        for (long i = 1; i <= 5; i++)
        {
            queue.TryEnqueue(Packet(i)).Should().BeTrue();
        }

        for (long i = 1; i <= 5; i++)
        {
            queue.TryDequeue(out PendingPacket packet).Should().BeTrue();
            packet.Token.Should().Be(i);
        }
    }

    [Fact]
    public void LeVolumeEnAttente_SuitLesEntreesEtSorties()
    {
        var queue = new DelayQueue();
        queue.TryEnqueue(Packet(1, 1000));
        queue.TryEnqueue(Packet(2, 500));

        queue.QueuedBytes.Should().Be(1500);

        queue.TryDequeue(out _);
        queue.QueuedBytes.Should().Be(500);
    }

    [Fact]
    public void TryPeek_NeRetirePasLePaquet()
    {
        var queue = new DelayQueue();
        queue.TryEnqueue(Packet(42));

        queue.TryPeek(out PendingPacket peeked).Should().BeTrue();
        peeked.Token.Should().Be(42);
        queue.Count.Should().Be(1);
    }

    // -- Bornes ---------------------------------------------------------------

    [Fact]
    public void FilePleineEnNombre_RejetteEtCompte()
    {
        var queue = new DelayQueue(maxPackets: 3, maxBytes: 1_000_000);

        for (long i = 1; i <= 3; i++)
        {
            queue.TryEnqueue(Packet(i)).Should().BeTrue();
        }

        queue.TryEnqueue(Packet(4)).Should().BeFalse();

        queue.Count.Should().Be(3);
        queue.DroppedPackets.Should().Be(1);
    }

    [Fact]
    public void FilePleineEnOctets_RejetteEtCompte()
    {
        // La borne en octets compte autant que celle en nombre : mille paquets de 64 Ko
        // representent 64 Mo, ce qu'une borne en nombre seule laisserait passer.
        var queue = new DelayQueue(maxPackets: 1000, maxBytes: 3000);

        queue.TryEnqueue(Packet(1, 1500)).Should().BeTrue();
        queue.TryEnqueue(Packet(2, 1500)).Should().BeTrue();
        queue.TryEnqueue(Packet(3, 1500)).Should().BeFalse();

        queue.QueuedBytes.Should().Be(3000);
        queue.DroppedPackets.Should().Be(1);
    }

    [Fact]
    public void LeRejetEstEnQueue_LesPaquetsDejaAdmisSontConserves()
    {
        // Rejeter en tete casserait l'ordre des paquets deja acceptes.
        var queue = new DelayQueue(maxPackets: 2, maxBytes: 1_000_000);
        queue.TryEnqueue(Packet(1));
        queue.TryEnqueue(Packet(2));

        queue.TryEnqueue(Packet(3)).Should().BeFalse();

        queue.TryDequeue(out PendingPacket first).Should().BeTrue();
        first.Token.Should().Be(1);
    }

    [Fact]
    public void ApresUneSortie_LaFileAccepteANouveau()
    {
        var queue = new DelayQueue(maxPackets: 2, maxBytes: 1_000_000);
        queue.TryEnqueue(Packet(1));
        queue.TryEnqueue(Packet(2));
        queue.TryEnqueue(Packet(3)).Should().BeFalse();

        queue.TryDequeue(out _);

        queue.TryEnqueue(Packet(4)).Should().BeTrue();
    }

    [Fact]
    public void Clear_VideLaFileEtLeCompteurDeRejets()
    {
        var queue = new DelayQueue(maxPackets: 1, maxBytes: 1_000_000);
        queue.TryEnqueue(Packet(1));
        queue.TryEnqueue(Packet(2));

        queue.Clear();

        queue.Count.Should().Be(0);
        queue.QueuedBytes.Should().Be(0);
        queue.DroppedPackets.Should().Be(0);
    }

    [Fact]
    public void RejetExterne_EstCompte()
    {
        // Cas du descendant sans controle de congestion : le rejet est le mecanisme meme de
        // la limite, pas un debordement. Il doit quand meme etre compte pour FR-003b.
        var queue = new DelayQueue();

        queue.CountExternalDrop();
        queue.CountExternalDrop();

        queue.DroppedPackets.Should().Be(2);
        queue.Count.Should().Be(0);
    }

    // -- Robustesse -----------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BornesNonPositives_Levent(int bound)
    {
        Action byCount = () => _ = new DelayQueue(maxPackets: bound);
        Action byBytes = () => _ = new DelayQueue(maxBytes: bound);

        byCount.Should().Throw<ArgumentOutOfRangeException>();
        byBytes.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void PaquetDeTailleNonPositive_Leve()
    {
        var queue = new DelayQueue();

        Action act = () => queue.TryEnqueue(Packet(1, 0));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

/// <summary>
/// Politique de rejet — FR-003a et FR-003c.
/// </summary>
public sealed class DropPolicyTests
{
    // -- FR-003c : jamais de rejet quand la temporisation suffit --------------

    [Theory]
    [InlineData(TransportProtocol.Tcp)]
    [InlineData(TransportProtocol.Udp)]
    [InlineData(TransportProtocol.Other)]
    public void EnMontant_ToutEstTemporisable(TransportProtocol protocol)
    {
        // La machine decide quand elle emet : retarder controle le debit directement,
        // quel que soit le protocole.
        DropPolicy.IsDelayable(PacketDirection.Outbound, protocol).Should().BeTrue();
    }

    [Fact]
    public void EnDescendant_TcpEstTemporisable()
    {
        // Le paquet est deja arrive, mais retarder le flux fait refluer l'emetteur par le
        // controle de congestion. Rien n'est perdu.
        DropPolicy.IsDelayable(PacketDirection.Inbound, TransportProtocol.Tcp).Should().BeTrue();
    }

    [Theory]
    [InlineData(TransportProtocol.Udp)]
    [InlineData(TransportProtocol.Other)]
    public void EnDescendant_SansControleDeCongestion_LaTemporisationNeSuffitPas(TransportProtocol protocol)
    {
        // Aucune boucle de retroaction : retarder ne ralentit pas l'emetteur, la file se
        // remplirait sans jamais faire baisser le debit. Seul le rejet tient le plafond.
        DropPolicy.IsDelayable(PacketDirection.Inbound, protocol).Should().BeFalse();
    }

    [Theory]
    [InlineData(PacketDirection.Outbound, TransportProtocol.Tcp)]
    [InlineData(PacketDirection.Outbound, TransportProtocol.Udp)]
    [InlineData(PacketDirection.Inbound, TransportProtocol.Tcp)]
    public void FileNonPleine_LesPaquetsTemporisablesSontTemporises(
        PacketDirection direction, TransportProtocol protocol)
    {
        // LE test de FR-003c. Un rejet observe ici signifierait que le produit degrade le
        // trafic alors qu'il pouvait simplement le ralentir.
        DropPolicy.Decide(direction, protocol, queueIsFull: false)
                  .Should().Be(ShapingAction.Delay);
    }

    [Theory]
    [InlineData(PacketDirection.Outbound, TransportProtocol.Tcp)]
    [InlineData(PacketDirection.Inbound, TransportProtocol.Tcp)]
    public void FilePleine_MemeUnPaquetTemporisableEstRejete(
        PacketDirection direction, TransportProtocol protocol)
    {
        // Dernier recours mesure : la file a une borne, et la depasser serait pire que le
        // rejet — memoire du service et connexions expirees.
        DropPolicy.Decide(direction, protocol, queueIsFull: true)
                  .Should().Be(ShapingAction.Drop);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DescendantSansControleDeCongestion_EstRejeteQuelQueSoitLEtatDeLaFile(bool queueIsFull)
    {
        // L'etat de la file n'entre pas en ligne de compte : le rejet est ici le mecanisme
        // de la limite (FR-003a), pas un debordement.
        DropPolicy.Decide(PacketDirection.Inbound, TransportProtocol.Udp, queueIsFull)
                  .Should().Be(ShapingAction.Drop);
    }

    [Fact]
    public void ToutesLesCombinaisons_RendentUneDecisionDefinie()
    {
        // Verrouille l'exhaustivite : une combinaison non traitee tomberait dans un
        // comportement par defaut, et c'est exactement la que naissent les pertes
        // inexpliquees.
        foreach (PacketDirection direction in Enum.GetValues<PacketDirection>())
        {
            foreach (TransportProtocol protocol in Enum.GetValues<TransportProtocol>())
            {
                foreach (bool full in (bool[])[true, false])
                {
                    ShapingAction action = DropPolicy.Decide(direction, protocol, full);

                    action.Should().BeOneOf(ShapingAction.Delay, ShapingAction.Drop);
                }
            }
        }
    }
}
