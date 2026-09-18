using System.Net;
using System.Net.Sockets;

namespace NetworkLimiter.Core.Classification;

/// <summary>
/// Classe une adresse distante en <see cref="NetworkScope"/>.
/// </summary>
/// <remarks>
/// <para>
/// La classification porte sur l'<b>adresse de destination finale</b>, jamais sur la route
/// empruntée. C'est ce qui résout le cas d'une machine dont la passerelle internet est
/// elle-même en plage privée : une requête vers un serveur public porte une adresse publique
/// en destination, même si le premier saut est un routeur en <c>192.168.x.x</c>.
/// </para>
/// <para>
/// Plages traitées comme locales, conformément à research.md R-006 :
/// <c>10.0.0.0/8</c>, <c>172.16.0.0/12</c>, <c>192.168.0.0/16</c>, <c>169.254.0.0/16</c>,
/// <c>224.0.0.0/4</c>, <c>255.255.255.255</c>, <c>fe80::/10</c>, <c>fc00::/7</c>,
/// <c>ff00::/8</c>. Boucle locale : <c>127.0.0.0/8</c> et <c>::1</c>.
/// </para>
/// </remarks>
public static class NetworkScopeClassifier
{
    /// <summary>
    /// Indique si une portée est soumise aux plafonds. Seul <see cref="NetworkScope.Internet"/>
    /// l'est (FR-040).
    /// </summary>
    public static bool IsSubjectToLimits(NetworkScope scope) => scope == NetworkScope.Internet;

    /// <summary>Classe une adresse distante.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="remoteAddress"/> est <c>null</c>.</exception>
    public static NetworkScope Classify(IPAddress remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);

        // Une pile double-couche presente le trafic IPv4 sous forme d'adresses IPv4 mappees
        // en IPv6. Sans ce repli, le meme flux serait classe differemment selon la facon dont
        // la pile le remonte : du trafic LAN passerait pour internet, ou l'inverse.
        IPAddress address = remoteAddress.IsIPv4MappedToIPv6
            ? remoteAddress.MapToIPv4()
            : remoteAddress;

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => ClassifyIPv4(address),
            AddressFamily.InterNetworkV6 => ClassifyIPv6(address),
            _ => NetworkScope.Internet,
        };
    }

    private static NetworkScope ClassifyIPv4(IPAddress address)
    {
        Span<byte> octets = stackalloc byte[4];
        if (!address.TryWriteBytes(octets, out _))
        {
            return NetworkScope.Internet;
        }

        // 127.0.0.0/8
        if (octets[0] == 127)
        {
            return NetworkScope.Loopback;
        }

        // 255.255.255.255 — diffusion limitee. Teste avant les plages pour ne pas dependre
        // de l'ordre des masques.
        if (octets[0] == 255 && octets[1] == 255 && octets[2] == 255 && octets[3] == 255)
        {
            return NetworkScope.Local;
        }

        // 10.0.0.0/8
        if (octets[0] == 10)
        {
            return NetworkScope.Local;
        }

        // 172.16.0.0/12 — l'erreur classique est de prendre 172.16.0.0/16 et de laisser
        // filer 172.17 a 172.31, qui sont pourtant privees.
        if (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
        {
            return NetworkScope.Local;
        }

        // 192.168.0.0/16
        if (octets[0] == 192 && octets[1] == 168)
        {
            return NetworkScope.Local;
        }

        // 169.254.0.0/16 — lien-local (APIPA)
        if (octets[0] == 169 && octets[1] == 254)
        {
            return NetworkScope.Local;
        }

        // 224.0.0.0/4 — multicast
        if (octets[0] >= 224 && octets[0] <= 239)
        {
            return NetworkScope.Local;
        }

        // 100.64.0.0/10 (CGNAT) n'est deliberement PAS local : c'est du transport
        // d'operateur, donc du trafic internet du point de vue de l'utilisateur.
        return NetworkScope.Internet;
    }

    private static NetworkScope ClassifyIPv6(IPAddress address)
    {
        if (IPAddress.IPv6Loopback.Equals(address))
        {
            return NetworkScope.Loopback;
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out _))
        {
            return NetworkScope.Internet;
        }

        // ff00::/8 — multicast
        if (bytes[0] == 0xFF)
        {
            return NetworkScope.Local;
        }

        // fe80::/10 — lien-local. Le masque porte sur 10 bits : premier octet 0xFE, puis les
        // deux bits de poids fort du second octet a 10.
        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
        {
            return NetworkScope.Local;
        }

        // fc00::/7 — uniques locales (fc00:: a fdff::)
        if ((bytes[0] & 0xFE) == 0xFC)
        {
            return NetworkScope.Local;
        }

        return NetworkScope.Internet;
    }
}
