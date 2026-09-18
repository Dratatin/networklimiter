using System.Buffers.Binary;

namespace NetworkLimiter.Contracts.Serialization;

/// <summary>
/// Message dont la taille annoncée ou réelle dépasse le plafond du protocole.
/// </summary>
public sealed class MessageTooLargeException : Exception
{
    /// <summary>Crée l'exception pour une taille donnée.</summary>
    public MessageTooLargeException(long announcedBytes)
        : base($"Message de {announcedBytes} octets : le plafond est de {MessageFraming.MaxMessageBytes} octets.") =>
        AnnouncedBytes = announcedBytes;

    /// <inheritdoc cref="Exception(string)" />
    public MessageTooLargeException(string message) : base(message)
    {
    }

    /// <inheritdoc cref="Exception(string, Exception)" />
    public MessageTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Crée l'exception sans détail.</summary>
    public MessageTooLargeException()
    {
    }

    /// <summary>Taille annoncée ou observée, en octets.</summary>
    public long AnnouncedBytes { get; }
}

/// <summary>
/// Cadrage des messages sur un flux : préfixe de longueur puis charge utile.
/// </summary>
/// <remarks>
/// <para>
/// Format : 4 octets de longueur, entier non signé petit-boutiste, suivis d'exactement ce
/// nombre d'octets de JSON UTF-8.
/// </para>
/// <para>
/// C'est la première chose qu'un message rencontre en franchissant la frontière de privilège,
/// <b>avant</b> toute désérialisation. Le plafond est donc vérifié sur le seul en-tête : un
/// appelant non privilégié ne doit jamais pouvoir faire allouer au service un tampon de la
/// taille qu'il déclare.
/// </para>
/// </remarks>
public static class MessageFraming
{
    /// <summary>Taille maximale d'un message, en octets, en-tête exclu.</summary>
    public const int MaxMessageBytes = 65_536;

    private const int HeaderBytes = 4;

    /// <summary>Écrit un message cadré sur le flux.</summary>
    /// <exception cref="ArgumentException">La charge utile est vide.</exception>
    /// <exception cref="MessageTooLargeException">La charge utile dépasse le plafond.</exception>
    public static async ValueTask WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (payload.IsEmpty)
        {
            throw new ArgumentException("Un message vide n'a pas de sens.", nameof(payload));
        }

        // Verifie AVANT d'ecrire quoi que ce soit : un message refuse ne doit pas laisser
        // un en-tete orphelin sur le flux, qui desynchroniserait le pair.
        if (payload.Length > MaxMessageBytes)
        {
            throw new MessageTooLargeException(payload.Length);
        }

        byte[] header = new byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lit un message cadré.
    /// </summary>
    /// <returns>
    /// La charge utile, ou <c>null</c> si le flux s'est terminé proprement — le pair a fermé
    /// la connexion, ce qui n'est pas une erreur.
    /// </returns>
    /// <exception cref="MessageTooLargeException">L'en-tête annonce une taille au-delà du plafond.</exception>
    /// <exception cref="InvalidDataException">L'en-tête annonce une taille nulle.</exception>
    /// <exception cref="EndOfStreamException">Le flux s'interrompt au milieu d'un message.</exception>
    public static async ValueTask<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] header = new byte[HeaderBytes];
        int headerRead = await ReadAtLeastAsync(stream, header, cancellationToken).ConfigureAwait(false);

        if (headerRead == 0)
        {
            return null;
        }

        if (headerRead < HeaderBytes)
        {
            throw new EndOfStreamException(
                $"En-tête tronqué : {headerRead} octet(s) lu(s) sur {HeaderBytes} attendus.");
        }

        uint announced = BinaryPrimitives.ReadUInt32LittleEndian(header);

        // Le plafond est verifie ici, sur la seule valeur annoncee, avant toute allocation.
        // C'est ce qui empeche un appelant d'annoncer 0xFFFFFFFF et de faire tomber le service.
        if (announced > MaxMessageBytes)
        {
            throw new MessageTooLargeException(announced);
        }

        if (announced == 0)
        {
            throw new InvalidDataException("L'en-tête annonce un message de taille nulle.");
        }

        byte[] payload = new byte[announced];
        int payloadRead = await ReadAtLeastAsync(stream, payload, cancellationToken).ConfigureAwait(false);

        if (payloadRead < announced)
        {
            throw new EndOfStreamException(
                $"Charge utile tronquée : {payloadRead} octet(s) lu(s) sur {announced} annoncés.");
        }

        return payload;
    }

    private static async ValueTask<int> ReadAtLeastAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int total = 0;

        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
