using System.IO.Pipes;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Contracts.Serialization;
using NetworkLimiter.Service.Ipc;

namespace NetworkLimiter.Service.Tests.Ipc;

/// <summary>
/// Poignée de main de bout en bout sur un vrai tuyau nommé.
/// </summary>
/// <remarks>
/// Les tests de <see cref="HandshakeTests"/> valident les règles du protocole en mémoire.
/// Ceux-ci vérifient qu'elles tiennent réellement à travers un tuyau : cadrage, sérialisation,
/// asynchronisme et fermeture de connexion compris. C'est là que se manifestent les défauts
/// qu'aucun test en mémoire ne révèle — décalage de cadrage, blocage sur lecture, réponse
/// jamais vidée.
/// </remarks>
public sealed class HandshakeRoundTripTests
{
    private sealed class FakeCallerIdentity(bool elevated) : ICallerIdentity
    {
        public bool IsElevatedAdministrator { get; } = elevated;
    }

    /// <summary>Nom unique par test : les exécutions parallèles ne doivent pas se marcher dessus.</summary>
    private static string UniquePipeName() => $"NetworkLimiter.Test.{Guid.NewGuid():N}";

    private static async Task<MessageEnvelope?> ExchangeAsync(
        MessageEnvelope clientMessage,
        bool callerElevated = false)
    {
        string pipeName = UniquePipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        Task serverTask = Task.Run(
            async () =>
            {
                await server.WaitForConnectionAsync(cts.Token);
                var handler = new HandshakeHandler("1.0.0");
                await handler.PerformAsync(server, new FakeCallerIdentity(callerElevated), cts.Token);
            },
            cts.Token);

        await client.ConnectAsync(cts.Token);
        await MessageFraming.WriteAsync(
            client, MessageSerializer.SerializeToUtf8Bytes(clientMessage), cts.Token);

        byte[]? response = await MessageFraming.ReadAsync(client, cts.Token);
        await serverTask;

        return response is null ? null : MessageSerializer.Deserialize(response);
    }

    [Fact]
    public async Task VersionCompatible_LeServiceRepondEtLaConnexionSert()
    {
        MessageEnvelope? response = await ExchangeAsync(
            MessageEnvelope.CreateRequest(
                MessageTypes.Hello,
                new HelloPayload { ProtocolVersion = ProtocolVersion.Current, ClientVersion = "1.0.0" }));

        response.Should().NotBeNull();
        response!.Ok.Should().BeTrue();
        response.Type.Should().Be("HelloResult");

        HelloResultPayload payload = MessageSerializer.ReadPayload<HelloResultPayload>(response);
        payload.ProtocolVersion.Should().Be(ProtocolVersion.Current);
        payload.ServiceVersion.Should().Be("1.0.0");
    }

    [Fact]
    public async Task ElevationRenvoyee_EstCelleConstateeParLeService()
    {
        // Le client a envoye exactement le meme Hello dans les deux cas : la valeur ne peut
        // donc venir que du jeton constate cote service.
        MessageEnvelope hello = MessageEnvelope.CreateRequest(
            MessageTypes.Hello,
            new HelloPayload { ProtocolVersion = ProtocolVersion.Current, ClientVersion = "1.0.0" });

        MessageEnvelope? asStandard = await ExchangeAsync(hello, callerElevated: false);
        MessageEnvelope? asElevated = await ExchangeAsync(hello, callerElevated: true);

        MessageSerializer.ReadPayload<HelloResultPayload>(asStandard!).Elevated.Should().BeFalse();
        MessageSerializer.ReadPayload<HelloResultPayload>(asElevated!).Elevated.Should().BeTrue();
    }

    [Fact]
    public async Task VersionIncompatible_LeServiceRefuseAvecUnMessageActionnable()
    {
        MessageEnvelope? response = await ExchangeAsync(
            MessageEnvelope.CreateRequest(
                MessageTypes.Hello,
                new HelloPayload { ProtocolVersion = 999, ClientVersion = "9.9.9" }));

        response.Should().NotBeNull();
        response!.Ok.Should().BeFalse();
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(ErrorCode.ProtocolVersionMismatch);
        response.Error.Message.Should().Contain("999");
    }

    [Fact]
    public async Task RequeteAvantHello_EstRefusee()
    {
        MessageEnvelope? response = await ExchangeAsync(
            MessageEnvelope.CreateRequest(MessageTypes.GetState));

        response.Should().NotBeNull();
        response!.Ok.Should().BeFalse();
        response.Error!.Code.Should().Be(ErrorCode.ValidationFailed);
    }

    [Fact]
    public async Task MessageIllisible_EstRefuseSansTuerLaBoucle()
    {
        // Un octet de charge utile qui n'est pas du JSON : le service doit repondre une
        // erreur, pas lever une exception qui remonterait dans la boucle de connexion.
        string pipeName = UniquePipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        Task<bool> serverTask = Task.Run(
            async () =>
            {
                await server.WaitForConnectionAsync(cts.Token);
                var handler = new HandshakeHandler("1.0.0");
                return await handler.PerformAsync(server, new FakeCallerIdentity(false), cts.Token);
            },
            cts.Token);

        await client.ConnectAsync(cts.Token);
        await MessageFraming.WriteAsync(client, "pas du json"u8.ToArray(), cts.Token);

        byte[]? raw = await MessageFraming.ReadAsync(client, cts.Token);
        bool accepted = await serverTask;

        accepted.Should().BeFalse();
        raw.Should().NotBeNull();
        MessageSerializer.Deserialize(raw!).Error!.Code.Should().Be(ErrorCode.ValidationFailed);
    }

    [Fact]
    public async Task ClientQuiFermeAvantDEnvoyer_NeFaitPasEchouerLeService()
    {
        string pipeName = UniquePipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Task<bool> serverTask = Task.Run(
            async () =>
            {
                await server.WaitForConnectionAsync(cts.Token);
                var handler = new HandshakeHandler("1.0.0");
                return await handler.PerformAsync(server, new FakeCallerIdentity(false), cts.Token);
            },
            cts.Token);

        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cts.Token);
        await client.DisposeAsync();

        (await serverTask).Should().BeFalse();
    }
}
