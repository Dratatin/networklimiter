using System.Buffers.Binary;
using System.Text;
using NetworkLimiter.Contracts.Serialization;

namespace NetworkLimiter.Contracts.Tests;

/// <summary>
/// Cadrage des messages — contracts/ipc-protocol.md, section « Transport ».
/// </summary>
/// <remarks>
/// Le cadrage est la toute première chose qu'un message rencontre en franchissant la frontière
/// de privilège : il s'exécute <b>avant</b> toute désérialisation. C'est donc lui, et lui seul,
/// qui protège contre un message annonçant une taille absurde pour faire allouer des gigaoctets
/// au service. Le plafond doit être vérifié sur l'en-tête, jamais après lecture.
/// </remarks>
public sealed class FramingTests
{
    private const int MaxBytes = 65_536;

    private static byte[] Payload(int size) => Enumerable.Repeat((byte)'a', size).ToArray();

    // -- Aller-retour ---------------------------------------------------------

    [Fact]
    public async Task AllerRetour_RendLesMemesOctets()
    {
        byte[] original = Encoding.UTF8.GetBytes("""{"type":"Hello"}""");
        using var stream = new MemoryStream();

        await MessageFraming.WriteAsync(stream, original, CancellationToken.None);
        stream.Position = 0;
        byte[]? read = await MessageFraming.ReadAsync(stream, CancellationToken.None);

        read.Should().Equal(original);
    }

    [Fact]
    public async Task MessagesConsecutifs_SontRelusDansLOrdre()
    {
        using var stream = new MemoryStream();
        byte[] first = Encoding.UTF8.GetBytes("premier");
        byte[] second = Encoding.UTF8.GetBytes("second");

        await MessageFraming.WriteAsync(stream, first, CancellationToken.None);
        await MessageFraming.WriteAsync(stream, second, CancellationToken.None);
        stream.Position = 0;

        (await MessageFraming.ReadAsync(stream, CancellationToken.None)).Should().Equal(first);
        (await MessageFraming.ReadAsync(stream, CancellationToken.None)).Should().Equal(second);
    }

    // -- Format de l'en-tête --------------------------------------------------

    [Fact]
    public async Task EnTete_EstUnEntierNonSigneSur4OctetsPetitBoutiste()
    {
        byte[] payload = Payload(300);
        using var stream = new MemoryStream();

        await MessageFraming.WriteAsync(stream, payload, CancellationToken.None);

        byte[] written = stream.ToArray();
        written.Should().HaveCount(4 + 300);
        BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(0, 4)).Should().Be(300);
    }

    // -- Plafond de taille ----------------------------------------------------

    [Fact]
    public void Constante_CorrespondAuContrat() =>
        MessageFraming.MaxMessageBytes.Should().Be(MaxBytes);

    [Fact]
    public async Task Ecriture_DUnMessageTropGrand_EstRefusee()
    {
        using var stream = new MemoryStream();

        Func<Task> act = async () =>
            await MessageFraming.WriteAsync(stream, Payload(MaxBytes + 1), CancellationToken.None);

        await act.Should().ThrowAsync<MessageTooLargeException>();
        stream.Length.Should().Be(0, "rien ne doit être écrit quand le message est refusé");
    }

    [Fact]
    public async Task Ecriture_ALaTailleMaximale_EstAcceptee()
    {
        using var stream = new MemoryStream();

        await MessageFraming.WriteAsync(stream, Payload(MaxBytes), CancellationToken.None);

        stream.Length.Should().Be(4 + MaxBytes);
    }

    [Fact]
    public async Task Lecture_DUnEnTeteAnnoncantTropGrand_EchoueSansLireLaCharge()
    {
        // Le point critique : la taille annoncee est refusee sur la seule lecture de
        // l'en-tete. Le flux ne contient volontairement AUCUNE charge utile — si
        // l'implementation tentait de lire ou d'allouer 65 537 octets avant de verifier,
        // ce test bloquerait ou echouerait autrement.
        using var stream = new MemoryStream();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, MaxBytes + 1);
        stream.Write(header);
        stream.Position = 0;

        Func<Task> act = async () => await MessageFraming.ReadAsync(stream, CancellationToken.None);

        await act.Should().ThrowAsync<MessageTooLargeException>();
    }

    [Fact]
    public async Task Lecture_DUnEnTeteAnnoncantLaTailleMaximaleDUnEntier_EchoueSansAllouer()
    {
        // 0xFFFFFFFF : la tentative naive d'allouer un tampon de cette taille tuerait le
        // service. Un client non privilegie ne doit pas pouvoir provoquer cela.
        using var stream = new MemoryStream();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue);
        stream.Write(header);
        stream.Position = 0;

        Func<Task> act = async () => await MessageFraming.ReadAsync(stream, CancellationToken.None);

        await act.Should().ThrowAsync<MessageTooLargeException>();
    }

    [Fact]
    public async Task Lecture_DUnEnTeteAnnoncantZero_EstRefusee()
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[4]);
        stream.Position = 0;

        Func<Task> act = async () => await MessageFraming.ReadAsync(stream, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    // -- Flux tronqués et fin de flux -----------------------------------------

    [Fact]
    public async Task Lecture_DUnFluxVide_RendNull()
    {
        // Fin de flux propre : le pair a ferme la connexion. Ce n'est pas une erreur.
        using var stream = new MemoryStream();

        byte[]? read = await MessageFraming.ReadAsync(stream, CancellationToken.None);

        read.Should().BeNull();
    }

    [Fact]
    public async Task Lecture_DUnEnTeteTronque_Echoue()
    {
        using var stream = new MemoryStream([0x10, 0x00]);

        Func<Task> act = async () => await MessageFraming.ReadAsync(stream, CancellationToken.None);

        await act.Should().ThrowAsync<EndOfStreamException>();
    }

    [Fact]
    public async Task Lecture_DUneChargeTronquee_Echoue()
    {
        using var stream = new MemoryStream();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 100);
        stream.Write(header);
        stream.Write(Payload(40));   // 40 octets au lieu des 100 annonces
        stream.Position = 0;

        Func<Task> act = async () => await MessageFraming.ReadAsync(stream, CancellationToken.None);

        await act.Should().ThrowAsync<EndOfStreamException>();
    }

    // -- Annulation coopérative -----------------------------------------------

    [Fact]
    public async Task Lecture_AnnuleeAvantDeCommencer_Leve()
    {
        using var stream = new MemoryStream(Payload(1000));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = async () => await MessageFraming.ReadAsync(stream, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Ecriture_ChargeVide_EstRefusee()
    {
        using var stream = new MemoryStream();

        Func<Task> act = async () =>
            await MessageFraming.WriteAsync(stream, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
