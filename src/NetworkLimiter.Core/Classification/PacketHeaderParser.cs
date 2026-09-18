using System.Buffers.Binary;
using System.Net;
using NetworkLimiter.Core.Shaping;

namespace NetworkLimiter.Core.Classification;

/// <summary>Quintuplet extrait d'un paquet IP.</summary>
/// <param name="Protocol">Numéro de protocole IP.</param>
/// <param name="Transport">Protocole de transport reconnu.</param>
/// <param name="SourceAddress">Adresse source.</param>
/// <param name="SourcePort">Port source, ou 0 si le protocole n'en a pas.</param>
/// <param name="DestinationAddress">Adresse destination.</param>
/// <param name="DestinationPort">Port destination, ou 0.</param>
public sealed record PacketHeader(
    byte Protocol,
    TransportProtocol Transport,
    IPAddress SourceAddress,
    ushort SourcePort,
    IPAddress DestinationAddress,
    ushort DestinationPort);

/// <summary>
/// Extrait le quintuplet d'un paquet IP brut.
/// </summary>
/// <remarks>
/// <para>
/// Nécessaire parce que la couche <c>NETWORK</c> de WinDivert livre des octets bruts sans
/// aucune métadonnée d'identité : c'est l'en-tête du paquet lui-même qui permet de retrouver
/// le flux, donc le processus, donc la règle.
/// </para>
/// <para>
/// Logique volontairement pure et isolée. Une erreur de décalage ici n'entraîne aucun
/// plantage : elle produit un quintuplet faux, donc une recherche de flux infructueuse, donc
/// un paquet non limité. L'utilisateur constaterait simplement que sa limite « ne marche pas
/// toujours », sans aucune trace exploitable.
/// </para>
/// </remarks>
public static class PacketHeaderParser
{
    private const byte ProtocolTcp = 6;
    private const byte ProtocolUdp = 17;

    private const int MinimumIPv4HeaderBytes = 20;
    private const int IPv6HeaderBytes = 40;

    /// <summary>
    /// Tente d'extraire le quintuplet.
    /// </summary>
    /// <returns><c>null</c> si le paquet est illisible ; il sera alors laissé passer sans limitation.</returns>
    public static PacketHeader? TryParse(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty)
        {
            return null;
        }

        return (packet[0] >> 4) switch
        {
            4 => ParseIPv4(packet),
            6 => ParseIPv6(packet),
            _ => null,
        };
    }

    private static PacketHeader? ParseIPv4(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < MinimumIPv4HeaderBytes)
        {
            return null;
        }

        // Les quatre bits de poids faible du premier octet donnent la longueur de l'en-tete
        // en MOTS DE 32 BITS. Oublier de multiplier par quatre est l'erreur classique, et elle
        // ferait lire les ports au mauvais endroit sur tout paquet portant des options IP.
        int headerLength = (packet[0] & 0x0F) * 4;

        if (headerLength < MinimumIPv4HeaderBytes || packet.Length < headerLength)
        {
            return null;
        }

        byte protocol = packet[9];
        var source = new IPAddress(packet.Slice(12, 4).ToArray());
        var destination = new IPAddress(packet.Slice(16, 4).ToArray());

        return BuildHeader(packet[headerLength..], protocol, source, destination);
    }

    private static PacketHeader? ParseIPv6(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < IPv6HeaderBytes)
        {
            return null;
        }

        byte nextHeader = packet[6];
        var source = new IPAddress(packet.Slice(8, 16).ToArray());
        var destination = new IPAddress(packet.Slice(24, 16).ToArray());

        // Les en-tetes d'extension IPv6 ne sont pas parcourus : un paquet qui en porte est
        // traite comme sans port, donc laisse passer. Les parcourir demanderait de gerer une
        // chaine de longueur arbitraire — surface d'attaque pour un gain marginal, ces
        // en-tetes etant rares sur du trafic applicatif courant.
        return BuildHeader(packet[IPv6HeaderBytes..], nextHeader, source, destination);
    }

    private static PacketHeader? BuildHeader(
        ReadOnlySpan<byte> transport,
        byte protocol,
        IPAddress source,
        IPAddress destination)
    {
        TransportProtocol kind = protocol switch
        {
            ProtocolTcp => TransportProtocol.Tcp,
            ProtocolUdp => TransportProtocol.Udp,
            _ => TransportProtocol.Other,
        };

        ushort sourcePort = 0;
        ushort destinationPort = 0;

        // TCP comme UDP portent les deux ports sur leurs quatre premiers octets, en
        // gros-boutiste. Au-dela, leurs en-tetes divergent, mais nous n'en avons pas besoin.
        if (kind != TransportProtocol.Other && transport.Length >= 4)
        {
            sourcePort = BinaryPrimitives.ReadUInt16BigEndian(transport[..2]);
            destinationPort = BinaryPrimitives.ReadUInt16BigEndian(transport.Slice(2, 2));
        }

        return new PacketHeader(protocol, kind, source, sourcePort, destination, destinationPort);
    }
}
