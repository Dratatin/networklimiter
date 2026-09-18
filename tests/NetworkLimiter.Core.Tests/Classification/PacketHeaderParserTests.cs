using System.Buffers.Binary;
using System.Net;
using NetworkLimiter.Core.Classification;
using NetworkLimiter.Core.Shaping;

namespace NetworkLimiter.Core.Tests.Classification;

/// <summary>
/// Extraction du quintuplet d'un paquet IP.
/// </summary>
/// <remarks>
/// <para>
/// La couche <c>NETWORK</c> de WinDivert livre des octets bruts, sans métadonnée d'identité :
/// c'est l'en-tête du paquet qui permet de retrouver le flux, donc le processus, donc la règle.
/// </para>
/// <para>
/// Une erreur de décalage ici ne plante pas. Elle produit un quintuplet faux, donc une
/// recherche de flux infructueuse, donc un paquet non limité — et l'utilisateur constate
/// seulement que sa limite « ne marche pas toujours ». Le piège principal est la longueur
/// d'en-tête IPv4, exprimée en <b>mots de 32 bits</b> : l'oublier fait lire les ports au
/// mauvais endroit dès qu'un paquet porte des options IP.
/// </para>
/// </remarks>
public sealed class PacketHeaderParserTests
{
    private const byte Tcp = 6;
    private const byte Udp = 17;

    private static byte[] IPv4(
        byte protocol = Tcp,
        string source = "192.168.1.10",
        string destination = "93.184.216.34",
        ushort sourcePort = 50000,
        ushort destinationPort = 443,
        int optionWords = 0)
    {
        int headerWords = 5 + optionWords;
        int headerBytes = headerWords * 4;
        byte[] packet = new byte[headerBytes + 20];

        packet[0] = (byte)(0x40 | headerWords);
        packet[9] = protocol;
        IPAddress.Parse(source).GetAddressBytes().CopyTo(packet, 12);
        IPAddress.Parse(destination).GetAddressBytes().CopyTo(packet, 16);

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(headerBytes, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(headerBytes + 2, 2), destinationPort);

        return packet;
    }

    private static byte[] IPv6(
        byte nextHeader = Tcp,
        string source = "fd00::1",
        string destination = "2606:4700:4700::1111",
        ushort sourcePort = 50000,
        ushort destinationPort = 443)
    {
        byte[] packet = new byte[40 + 20];

        packet[0] = 0x60;
        packet[6] = nextHeader;
        IPAddress.Parse(source).GetAddressBytes().CopyTo(packet, 8);
        IPAddress.Parse(destination).GetAddressBytes().CopyTo(packet, 24);

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(40, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(42, 2), destinationPort);

        return packet;
    }

    // -- IPv4 -----------------------------------------------------------------

    [Fact]
    public void PaquetIPv4_RendLeQuintupletComplet()
    {
        PacketHeader? header = PacketHeaderParser.TryParse(IPv4());

        header.Should().NotBeNull();
        header!.Protocol.Should().Be(Tcp);
        header.Transport.Should().Be(TransportProtocol.Tcp);
        header.SourceAddress.Should().Be(IPAddress.Parse("192.168.1.10"));
        header.SourcePort.Should().Be(50000);
        header.DestinationAddress.Should().Be(IPAddress.Parse("93.184.216.34"));
        header.DestinationPort.Should().Be(443);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    public void PaquetIPv4AvecOptions_LitLesPortsAuBonEndroit(int optionWords)
    {
        // LE test qui compte. La longueur d'en-tete est en mots de 32 bits : sans la
        // multiplier par quatre, les ports seraient lus dans les options IP et le flux ne
        // serait jamais retrouve — donc jamais limite.
        PacketHeader? header = PacketHeaderParser.TryParse(
            IPv4(sourcePort: 12345, destinationPort: 80, optionWords: optionWords));

        header.Should().NotBeNull();
        header!.SourcePort.Should().Be(12345);
        header.DestinationPort.Should().Be(80);
    }

    [Fact]
    public void PaquetIPv4Udp_EstReconnu()
    {
        PacketHeader? header = PacketHeaderParser.TryParse(IPv4(protocol: Udp));

        header!.Transport.Should().Be(TransportProtocol.Udp);
        header.SourcePort.Should().Be(50000);
    }

    [Fact]
    public void ProtocoleNonTransport_NAPasDePort()
    {
        // ICMP par exemple : les quatre premiers octets ne sont pas des ports, les lire
        // comme tels inventerait un flux qui n'existe pas.
        PacketHeader? header = PacketHeaderParser.TryParse(IPv4(protocol: 1));

        header.Should().NotBeNull();
        header!.Transport.Should().Be(TransportProtocol.Other);
        header.SourcePort.Should().Be(0);
        header.DestinationPort.Should().Be(0);
    }

    // -- IPv6 -----------------------------------------------------------------

    [Fact]
    public void PaquetIPv6_RendLeQuintupletComplet()
    {
        // FR-008 : IPv6 est soumis aux memes plafonds. Un trou ici laisserait tout le
        // trafic IPv6 non attribue, donc non limite.
        PacketHeader? header = PacketHeaderParser.TryParse(IPv6());

        header.Should().NotBeNull();
        header!.Transport.Should().Be(TransportProtocol.Tcp);
        header.SourceAddress.Should().Be(IPAddress.Parse("fd00::1"));
        header.DestinationAddress.Should().Be(IPAddress.Parse("2606:4700:4700::1111"));
        header.SourcePort.Should().Be(50000);
        header.DestinationPort.Should().Be(443);
    }

    [Fact]
    public void LEnTeteIPv6_EstDeLongueurFixe()
    {
        // Contrairement a IPv4, l'en-tete IPv6 fait toujours 40 octets : les ports suivent
        // immediatement. Appliquer la logique IPv4 ici decalerait tout.
        byte[] packet = IPv6(sourcePort: 1111, destinationPort: 2222);

        PacketHeader? header = PacketHeaderParser.TryParse(packet);

        header!.SourcePort.Should().Be(1111);
        header.DestinationPort.Should().Be(2222);
    }

    // -- Paquets illisibles ---------------------------------------------------

    [Fact]
    public void PaquetVide_NEstPasLu()
    {
        PacketHeaderParser.TryParse([]).Should().BeNull();
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x50)]
    [InlineData(0xF0)]
    public void VersionIPInconnue_NEstPasLue(byte firstByte)
    {
        byte[] packet = new byte[60];
        packet[0] = firstByte;

        PacketHeaderParser.TryParse(packet).Should().BeNull();
    }

    [Fact]
    public void EnTeteIPv4Tronque_NEstPasLu()
    {
        PacketHeaderParser.TryParse([0x45, 0, 0, 40, 0, 0, 0, 0]).Should().BeNull();
    }

    [Fact]
    public void LongueurDEnTeteIPv4Absurde_NEstPasLue()
    {
        // Longueur annoncee inferieure au minimum : un paquet malforme ne doit pas faire
        // lire hors de sa propre zone.
        byte[] packet = IPv4();
        packet[0] = 0x43;   // 3 mots = 12 octets, sous le minimum de 20

        PacketHeaderParser.TryParse(packet).Should().BeNull();
    }

    [Fact]
    public void LongueurDEnTeteDepassantLePaquet_NEstPasLue()
    {
        byte[] packet = IPv4();
        packet[0] = 0x4F;   // 15 mots = 60 octets, plus que le paquet n'en contient

        PacketHeaderParser.TryParse(packet).Should().BeNull();
    }

    [Fact]
    public void PaquetSansCoucheTransport_EstLuSansPorts()
    {
        // En-tete IP complet mais rien derriere : le quintuplet reste exploitable pour la
        // classification d'adresse, sans inventer de ports.
        byte[] packet = new byte[20];
        packet[0] = 0x45;
        packet[9] = Tcp;

        PacketHeader? header = PacketHeaderParser.TryParse(packet);

        header.Should().NotBeNull();
        header!.SourcePort.Should().Be(0);
        header.DestinationPort.Should().Be(0);
    }

    [Fact]
    public void EnTeteIPv6Tronque_NEstPasLu()
    {
        byte[] packet = new byte[20];
        packet[0] = 0x60;

        PacketHeaderParser.TryParse(packet).Should().BeNull();
    }
}
