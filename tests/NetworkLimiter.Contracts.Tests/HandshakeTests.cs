using System.Globalization;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Contracts.Serialization;

namespace NetworkLimiter.Contracts.Tests;

/// <summary>
/// Poignée de main — contracts/ipc-protocol.md, section « Poignée de main ».
/// </summary>
/// <remarks>
/// Deux règles structurantes y sont vérifiées. D'abord, <c>Hello</c> est le premier message
/// obligatoire : toute autre requête avant lui ferme la connexion, ce qui empêche d'exécuter
/// quoi que ce soit sur un canal dont la version n'est pas encore établie. Ensuite, une
/// version incompatible est refusée <b>proprement</b>, avec un message actionnable — jamais
/// par un échec silencieux plus loin dans le dialogue.
/// </remarks>
public sealed class HandshakeTests
{
    private static MessageEnvelope Hello(int protocolVersion, string clientVersion = "1.0.0") =>
        MessageEnvelope.CreateRequest(
            MessageTypes.Hello,
            new HelloPayload { ProtocolVersion = protocolVersion, ClientVersion = clientVersion });

    // -- Cas nominal ----------------------------------------------------------

    [Fact]
    public void MemeVersion_EstAcceptee()
    {
        HandshakeResult result = HandshakeValidator.Validate(Hello(ProtocolVersion.Current));

        result.Outcome.Should().Be(HandshakeOutcome.Accepted);
        result.ErrorCode.Should().BeNull();
    }

    // -- Incompatibilité de version -------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void VersionDifferente_EstRefuseeProprement(int version)
    {
        HandshakeResult result = HandshakeValidator.Validate(Hello(version));

        result.Outcome.Should().Be(HandshakeOutcome.VersionMismatch);
        result.ErrorCode.Should().Be(ErrorCode.ProtocolVersionMismatch);
        result.ShouldCloseConnection.Should().BeTrue();
        result.Diagnostic.Should().NotBeNullOrWhiteSpace(
            "le message doit être actionnable, pas un échec muet");
    }

    [Fact]
    public void VersionDifferente_LeMessageNommeLesDeuxVersions()
    {
        HandshakeResult result = HandshakeValidator.Validate(Hello(42));

        result.Diagnostic.Should().Contain("42");
        result.Diagnostic.Should().Contain(ProtocolVersion.Current.ToString(CultureInfo.InvariantCulture));
    }

    // -- Hello obligatoire en premier -----------------------------------------

    [Theory]
    [InlineData(MessageTypes.GetState)]
    [InlineData(MessageTypes.GetHealth)]
    [InlineData(MessageTypes.UpsertRule)]
    [InlineData(MessageTypes.SetSuspended)]
    public void RequeteAvantHello_FermeLaConnexion(string type)
    {
        HandshakeResult result = HandshakeValidator.Validate(MessageEnvelope.CreateRequest(type));

        result.Outcome.Should().Be(HandshakeOutcome.HelloExpected);
        result.ShouldCloseConnection.Should().BeTrue();
    }

    [Fact]
    public void TypeInconnuEnPremierMessage_FermeLaConnexion()
    {
        HandshakeResult result = HandshakeValidator.Validate(MessageEnvelope.CreateRequest("Inconnu"));

        result.Outcome.Should().Be(HandshakeOutcome.HelloExpected);
        result.ShouldCloseConnection.Should().BeTrue();
    }

    // -- Charge utile absente ou illisible ------------------------------------

    [Fact]
    public void HelloSansChargeUtile_EstRefuse()
    {
        HandshakeResult result = HandshakeValidator.Validate(MessageEnvelope.CreateRequest(MessageTypes.Hello));

        result.Outcome.Should().Be(HandshakeOutcome.Malformed);
        result.ErrorCode.Should().Be(ErrorCode.ValidationFailed);
        result.ShouldCloseConnection.Should().BeTrue();
    }

    [Fact]
    public void HelloAvecChargeUtileIllisible_EstRefuseSansExceptionNonGeree()
    {
        // Le validateur est le premier code a toucher une entree non fiable : il doit
        // traduire toute malformation en verdict, jamais la laisser remonter en exception.
        MessageEnvelope envelope = MessageSerializer.Deserialize("""
            {"type":"Hello","id":"3f2a0000-0000-0000-0000-000000000000","payload":{"protocolVersion":"pas un entier","clientVersion":"1.0.0"}}
            """);

        HandshakeResult result = HandshakeValidator.Validate(envelope);

        result.Outcome.Should().Be(HandshakeOutcome.Malformed);
        result.ShouldCloseConnection.Should().BeTrue();
    }

    [Fact]
    public void Validate_EnveloppeNulle_Leve()
    {
        Action act = () => HandshakeValidator.Validate(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // -- Le client ne peut pas se déclarer élevé ------------------------------

    [Fact]
    public void ClientSeDeclarantEleve_EstRejeteCommeChampInconnu()
    {
        // Le champ « elevated » n'existe pas dans HelloPayload : il est calcule par le
        // service a partir du jeton reel. Un client qui l'ajoute voit son message rejete,
        // il ne se voit pas accorder l'elevation.
        MessageEnvelope envelope = MessageSerializer.Deserialize("""
            {"type":"Hello","id":"3f2a0000-0000-0000-0000-000000000000","payload":{"protocolVersion":1,"clientVersion":"1.0.0","elevated":true}}
            """);

        HandshakeResult result = HandshakeValidator.Validate(envelope);

        result.Outcome.Should().Be(HandshakeOutcome.Malformed);
    }

    [Fact]
    public void ReponseDeHandshake_PorteLElevationCalculeeParLeService()
    {
        HelloResultPayload payload = HandshakeValidator.CreateResult(serviceVersion: "1.0.0", elevated: false);

        payload.ProtocolVersion.Should().Be(ProtocolVersion.Current);
        payload.Elevated.Should().BeFalse();
        payload.ServiceVersion.Should().Be("1.0.0");
    }
}
