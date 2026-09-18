using System.Buffers.Binary;
using NetworkLimiter.Service.Interception;

namespace NetworkLimiter.Service.Tests.Interception;

/// <summary>
/// Découpage des lots de paquets — research.md R-004.
/// </summary>
/// <remarks>
/// <para>
/// <c>WinDivertRecvEx</c> remplit un tampon unique avec jusqu'à 255 paquets bout à bout, sans
/// séparateur : les frontières ne se déduisent que du champ de longueur de chaque en-tête IP.
/// </para>
/// <para>
/// Une erreur d'un octet ne provoque aucun plantage. Elle produit des paquets mal découpés,
/// donc attribués aux mauvaises applications, donc limités selon les mauvaises règles — et
/// l'utilisateur constate simplement que « ça ne marche pas bien ». D'où des tests octet par
/// octet, et en particulier sur la différence IPv4/IPv6 qui est le piège principal.
/// </para>
/// </remarks>
public sealed class PacketBatchReaderTests
{
    /// <summary>Construit un paquet IPv4 dont le champ « Total Length » inclut l'en-tête.</summary>
    private static byte[] IPv4Packet(int totalLength)
    {
        byte[] packet = new byte[totalLength];
        packet[0] = 0x45;   // version 4, en-tête de 5 mots
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)totalLength);
        return packet;
    }

    /// <summary>Construit un paquet IPv6 dont « Payload Length » EXCLUT les 40 octets d'en-tête.</summary>
    private static byte[] IPv6Packet(int payloadLength)
    {
        byte[] packet = new byte[40 + payloadLength];
        packet[0] = 0x60;   // version 6
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), (ushort)payloadLength);
        return packet;
    }

    private static byte[] Concat(params byte[][] packets) => packets.SelectMany(p => p).ToArray();

    // -- Longueur d'un paquet isolé -------------------------------------------

    [Theory]
    [InlineData(20)]
    [InlineData(64)]
    [InlineData(1500)]
    [InlineData(65535)]
    public void LongueurIPv4_EstLueDansLeChampTotalLength(int totalLength)
    {
        PacketBatchReader.TryGetPacketLength(IPv4Packet(totalLength)).Should().Be(totalLength);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(1460)]
    public void LongueurIPv6_AjouteLesQuaranteOctetsDEnTete(int payloadLength)
    {
        // Le piege : en IPv4 le champ inclut l'en-tete, en IPv6 il l'exclut. Confondre les
        // deux decale tout le reste du lot.
        PacketBatchReader.TryGetPacketLength(IPv6Packet(payloadLength))
                         .Should().Be(40 + payloadLength);
    }

    [Fact]
    public void MemeValeurDeChamp_DonneDesLongueursDifferentesSelonLaVersion()
    {
        // Verrouille explicitement la difference de convention entre les deux familles.
        byte[] v4 = IPv4Packet(100);
        byte[] v6 = IPv6Packet(100);

        PacketBatchReader.TryGetPacketLength(v4).Should().Be(100);
        PacketBatchReader.TryGetPacketLength(v6).Should().Be(140);
    }

    [Theory]
    [InlineData(0x00)]   // version 0
    [InlineData(0x50)]   // version 5
    [InlineData(0xF0)]   // version 15
    public void VersionIPInconnue_EstSignalee(byte firstByte)
    {
        byte[] packet = new byte[64];
        packet[0] = firstByte;

        PacketBatchReader.TryGetPacketLength(packet).Should().Be(-1);
    }

    [Fact]
    public void PaquetVide_EstSignale()
    {
        PacketBatchReader.TryGetPacketLength([]).Should().Be(-1);
    }

    [Fact]
    public void EnTeteIPv4Tronque_EstSignale()
    {
        byte[] packet = [0x45, 0x00, 0x00, 0x40];   // 4 octets au lieu de 20

        PacketBatchReader.TryGetPacketLength(packet).Should().Be(-1);
    }

    [Fact]
    public void EnTeteIPv6Tronque_EstSignale()
    {
        byte[] packet = new byte[20];
        packet[0] = 0x60;

        PacketBatchReader.TryGetPacketLength(packet).Should().Be(-1);
    }

    [Fact]
    public void LongueurIPv4InferieureALEnTete_EstSignalee()
    {
        // Un paquet annoncant 10 octets alors que son en-tete en fait 20 est incoherent.
        // Sans ce garde-fou, le decoupage avancerait de 10 octets et se desynchroniserait ;
        // avec une longueur nulle, il bouclerait indefiniment.
        byte[] packet = IPv4Packet(20);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 10);

        PacketBatchReader.TryGetPacketLength(packet).Should().Be(-1);
    }

    // -- Découpage d'un lot ---------------------------------------------------

    [Fact]
    public void LotDUnSeulPaquet_EstDecoupeEnUn()
    {
        byte[] batch = IPv4Packet(64);
        Span<PacketSlice> slices = new PacketSlice[8];

        int count = PacketBatchReader.Split(batch, batch.Length, slices);

        count.Should().Be(1);
        slices[0].Should().Be(new PacketSlice(0, 64));
    }

    [Fact]
    public void LotDePlusieursPaquets_EstDecoupeDansLOrdre()
    {
        byte[] batch = Concat(IPv4Packet(40), IPv4Packet(100), IPv4Packet(64));
        Span<PacketSlice> slices = new PacketSlice[8];

        int count = PacketBatchReader.Split(batch, batch.Length, slices);

        count.Should().Be(3);
        slices[0].Should().Be(new PacketSlice(0, 40));
        slices[1].Should().Be(new PacketSlice(40, 100));
        slices[2].Should().Be(new PacketSlice(140, 64));
    }

    [Fact]
    public void LotMelangeantIPv4EtIPv6_EstDecoupeCorrectement()
    {
        // Le cas qui revele une confusion de convention : si l'IPv6 etait mesure comme de
        // l'IPv4, le troisieme paquet commencerait 40 octets trop tot.
        byte[] batch = Concat(IPv4Packet(40), IPv6Packet(60), IPv4Packet(80));
        Span<PacketSlice> slices = new PacketSlice[8];

        int count = PacketBatchReader.Split(batch, batch.Length, slices);

        count.Should().Be(3);
        slices[0].Should().Be(new PacketSlice(0, 40));
        slices[1].Should().Be(new PacketSlice(40, 100));   // 40 d'en-tête + 60 de charge
        slices[2].Should().Be(new PacketSlice(140, 80));
    }

    [Fact]
    public void LotVide_DonneZeroPaquet()
    {
        Span<PacketSlice> slices = new PacketSlice[8];

        PacketBatchReader.Split([], 0, slices).Should().Be(0);
    }

    [Fact]
    public void LotTronque_RendLesPaquetsCompletsEtIgnoreLeReste()
    {
        // Lever ferait perdre tout le lot pour un seul paquet incomplet. Les paquets deja
        // identifies restent exploitables.
        byte[] batch = Concat(IPv4Packet(40), IPv4Packet(100));
        Span<PacketSlice> slices = new PacketSlice[8];

        int count = PacketBatchReader.Split(batch, usedBytes: 100, slices);   // 40 + 60 sur 100

        count.Should().Be(1);
        slices[0].Should().Be(new PacketSlice(0, 40));
    }

    [Fact]
    public void PaquetMalformeEnMilieuDeLot_InterromptSansPerdreLePrecedent()
    {
        byte[] malformed = new byte[20];
        malformed[0] = 0x00;   // version invalide
        byte[] batch = Concat(IPv4Packet(40), malformed, IPv4Packet(40));
        Span<PacketSlice> slices = new PacketSlice[8];

        int count = PacketBatchReader.Split(batch, batch.Length, slices);

        count.Should().Be(1);
        slices[0].Should().Be(new PacketSlice(0, 40));
    }

    [Fact]
    public void TamponDeSortieTropPetit_LimiteLeNombreDePaquets()
    {
        byte[] batch = Concat(IPv4Packet(40), IPv4Packet(40), IPv4Packet(40));
        Span<PacketSlice> slices = new PacketSlice[2];

        int count = PacketBatchReader.Split(batch, batch.Length, slices);

        count.Should().Be(2);
    }

    [Fact]
    public void LotAuMaximumDeWinDivert_EstDecoupeIntegralement()
    {
        // 255 paquets : le lot maximal que WinDivertRecvEx peut remplir.
        byte[][] packets = Enumerable.Range(0, 255).Select(_ => IPv4Packet(40)).ToArray();
        byte[] batch = Concat(packets);
        Span<PacketSlice> slices = new PacketSlice[255];

        int count = PacketBatchReader.Split(batch, batch.Length, slices);

        count.Should().Be(255);
        slices[254].Should().Be(new PacketSlice(254 * 40, 40));
    }

    // -- Robustesse -----------------------------------------------------------

    [Fact]
    public void OctetsUtilesSuperieursAuTampon_Leve()
    {
        byte[] batch = new byte[64];
        PacketSlice[] slices = new PacketSlice[8];

        Action act = () => PacketBatchReader.Split(batch, usedBytes: 128, slices);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void OctetsUtilesNegatifs_Leve()
    {
        byte[] batch = new byte[64];
        PacketSlice[] slices = new PacketSlice[8];

        Action act = () => PacketBatchReader.Split(batch, usedBytes: -1, slices);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
