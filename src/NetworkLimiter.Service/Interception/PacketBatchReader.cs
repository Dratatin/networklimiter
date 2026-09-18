using System.Buffers.Binary;

namespace NetworkLimiter.Service.Interception;

/// <summary>Position et longueur d'un paquet dans un tampon de lot.</summary>
/// <param name="Offset">Décalage du premier octet du paquet.</param>
/// <param name="Length">Longueur du paquet, en octets.</param>
public readonly record struct PacketSlice(int Offset, int Length);

/// <summary>
/// Découpe un lot de paquets concaténés en paquets individuels.
/// </summary>
/// <remarks>
/// <para>
/// <c>WinDivertRecvEx</c> remplit un unique tampon avec jusqu'à 255 paquets bout à bout, sans
/// séparateur ni table d'index : les frontières ne se déduisent que du champ de longueur de
/// chaque en-tête IP.
/// </para>
/// <para>
/// C'est un point où une erreur est à la fois facile à commettre et difficile à voir. Un
/// décalage d'un octet ne provoque pas de plantage : il produit des paquets mal découpés, donc
/// attribués aux mauvaises applications, donc limités selon les mauvaises règles. La logique
/// est donc isolée ici, sans dépendance au pilote, pour être testable octet par octet.
/// </para>
/// </remarks>
public static class PacketBatchReader
{
    private const int MinimumIPv4HeaderBytes = 20;
    private const int IPv6HeaderBytes = 40;

    /// <summary>
    /// Découpe un lot en paquets.
    /// </summary>
    /// <param name="batch">Tampon reçu, contenant <paramref name="usedBytes"/> octets utiles.</param>
    /// <param name="usedBytes">Nombre d'octets effectivement remplis par WinDivert.</param>
    /// <param name="slices">Tampon de sortie, d'une capacité au moins égale au nombre de paquets attendu.</param>
    /// <returns>Nombre de paquets découpés.</returns>
    /// <remarks>
    /// Un lot tronqué ou incohérent interrompt le découpage sans lever : les paquets déjà
    /// identifiés restent exploitables, et le reste est ignoré. Lever ici ferait perdre tout
    /// le lot pour un seul paquet malformé.
    /// </remarks>
    public static int Split(ReadOnlySpan<byte> batch, int usedBytes, Span<PacketSlice> slices)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(usedBytes);

        if (usedBytes > batch.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(usedBytes), usedBytes, "Le lot annonce plus d'octets que le tampon n'en contient.");
        }

        int offset = 0;
        int count = 0;

        while (offset < usedBytes && count < slices.Length)
        {
            int length = TryGetPacketLength(batch[offset..usedBytes]);

            if (length <= 0 || offset + length > usedBytes)
            {
                break;
            }

            slices[count++] = new PacketSlice(offset, length);
            offset += length;
        }

        return count;
    }

    /// <summary>
    /// Lit la longueur totale d'un paquet depuis son en-tête IP.
    /// </summary>
    /// <returns>La longueur en octets, ou <c>-1</c> si l'en-tête est illisible.</returns>
    public static int TryGetPacketLength(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty)
        {
            return -1;
        }

        // Les quatre bits de poids fort du premier octet portent la version IP.
        int version = packet[0] >> 4;

        return version switch
        {
            4 => TryGetIPv4Length(packet),
            6 => TryGetIPv6Length(packet),
            _ => -1,
        };
    }

    private static int TryGetIPv4Length(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < MinimumIPv4HeaderBytes)
        {
            return -1;
        }

        // Champ « Total Length » : octets 2 et 3, gros-boutiste, en-tete inclus.
        int totalLength = BinaryPrimitives.ReadUInt16BigEndian(packet[2..4]);

        // Un paquet annoncant moins que son propre en-tete minimal est incoherent : le
        // signaler evite de boucler indefiniment sur un decalage nul.
        return totalLength < MinimumIPv4HeaderBytes ? -1 : totalLength;
    }

    private static int TryGetIPv6Length(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < IPv6HeaderBytes)
        {
            return -1;
        }

        // Champ « Payload Length » : octets 4 et 5, gros-boutiste, en-tete EXCLU.
        // C'est la difference avec IPv4 qui rend ce decoupage facile a rater.
        int payloadLength = BinaryPrimitives.ReadUInt16BigEndian(packet[4..6]);

        return IPv6HeaderBytes + payloadLength;
    }
}
