using System.Text.Json;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Contracts.Serialization;

namespace NetworkLimiter.Contracts.Tests;

/// <summary>
/// Contrat de sérialisation — contracts/ipc-protocol.md, section « Validation du contenu ».
/// </summary>
/// <remarks>
/// Tout ce qui franchit ce contrat vient d'un processus moins privilégié et doit être traité
/// comme hostile. Ces tests verrouillent les trois garanties qui rendent cette frontière sûre :
/// rejet de tout champ inconnu, absence de désérialisation polymorphe, et bornes vérifiées.
/// </remarks>
public sealed class SerializationTests
{
    // -- Aller-retour ---------------------------------------------------------

    [Fact]
    public void Enveloppe_FaitUnAllerRetourFidele()
    {
        MessageEnvelope original = MessageEnvelope.CreateRequest(
            MessageTypes.Hello,
            new HelloPayload { ProtocolVersion = ProtocolVersion.Current, ClientVersion = "1.0.0" });

        string json = MessageSerializer.Serialize(original);
        MessageEnvelope restored = MessageSerializer.Deserialize(json);

        restored.Type.Should().Be(MessageTypes.Hello);
        restored.Id.Should().Be(original.Id);

        HelloPayload payload = MessageSerializer.ReadPayload<HelloPayload>(restored);
        payload.ProtocolVersion.Should().Be(ProtocolVersion.Current);
        payload.ClientVersion.Should().Be("1.0.0");
    }

    [Fact]
    public void Erreur_FaitUnAllerRetourFidele()
    {
        MessageEnvelope original = MessageEnvelope.CreateError(
            Guid.NewGuid(), ErrorCode.ElevationRequired, "Élévation requise.");

        MessageEnvelope restored = MessageSerializer.Deserialize(MessageSerializer.Serialize(original));

        restored.Type.Should().Be(MessageTypes.Error);
        restored.Ok.Should().BeFalse();
        restored.Error.Should().NotBeNull();
        restored.Error!.Code.Should().Be(ErrorCode.ElevationRequired);
    }

    [Fact]
    public void CodesDErreur_SontSerialisesEnTexte()
    {
        // Un code numerique rendrait les journaux et le deverminage illisibles, et surtout
        // un decalage d'enumeration changerait silencieusement le sens d'un message.
        string json = MessageSerializer.Serialize(
            MessageEnvelope.CreateError(Guid.NewGuid(), ErrorCode.ProtocolVersionMismatch, "x"));

        json.Should().Contain("\"ProtocolVersionMismatch\"");
        json.Should().NotContain("\"code\":1");
    }

    // -- Rejet des champs inconnus --------------------------------------------

    [Fact]
    public void ChampInconnuDansLEnveloppe_EstRejete()
    {
        const string json = """
            {"type":"Hello","id":"3f2a0000-0000-0000-0000-000000000000","extra":"surprise"}
            """;

        Action act = () => MessageSerializer.Deserialize(json);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void ChampInconnuDansLaChargeUtile_EstRejete()
    {
        // Le protocole ne pratique PAS la tolerance aux champs inconnus. Sur une frontiere
        // de privilege, ignorer ce qu'on ne comprend pas est un risque : un champ ignore est
        // un champ dont on ne sait pas s'il devait changer le comportement.
        const string json = """
            {"type":"Hello","id":"3f2a0000-0000-0000-0000-000000000000",
             "payload":{"protocolVersion":1,"clientVersion":"1.0.0","elevated":true}}
            """;

        MessageEnvelope envelope = MessageSerializer.Deserialize(json);
        Action act = () => MessageSerializer.ReadPayload<HelloPayload>(envelope);

        act.Should().Throw<JsonException>();
    }

    // -- Pas de désérialisation polymorphe ------------------------------------

    [Fact]
    public void DiscriminateurDeTypeDotNet_EstIgnoreEtRejete()
    {
        // Le vecteur classique : faire instancier au desserialiseur un type choisi par
        // l'appelant. Le contrat impose que le type soit choisi par le RECEVEUR a partir
        // de la liste blanche, jamais dicte par le message.
        const string json = """
            {"$type":"System.Diagnostics.Process, System.Diagnostics.Process",
             "type":"Hello","id":"3f2a0000-0000-0000-0000-000000000000"}
            """;

        Action act = () => MessageSerializer.Deserialize(json);

        act.Should().Throw<JsonException>();
    }

    // -- Robustesse du désérialiseur ------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("{\"type\":}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"chaine\"")]
    public void JsonMalformeOuInattendu_LeveUneJsonException(string json)
    {
        Action act = () => MessageSerializer.Deserialize(json);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void ProfondeurExcessive_EstRejetee()
    {
        // Sans limite de profondeur, un message impriquant quelques milliers d'objets
        // suffit a epuiser la pile du service. C'est un deni de service a cout nul
        // pour l'attaquant.
        string deep = new string('[', 200) + new string(']', 200);
        string json = $$"""
            {"type":"Hello","id":"3f2a0000-0000-0000-0000-000000000000","payload":{{deep}}}
            """;

        Action act = () => MessageSerializer.Deserialize(json);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void ClesDupliquees_SontRejetees()
    {
        const string json = """
            {"type":"Hello","type":"GetState","id":"3f2a0000-0000-0000-0000-000000000000"}
            """;

        Action act = () => MessageSerializer.Deserialize(json);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void ChampObligatoireAbsent_EstRejete()
    {
        const string json = """{"type":"Hello"}""";

        Action act = () => MessageSerializer.Deserialize(json);

        act.Should().Throw<JsonException>();
    }

    // -- Liste blanche des types de message -----------------------------------

    [Theory]
    [InlineData(MessageTypes.Hello)]
    [InlineData(MessageTypes.GetState)]
    [InlineData(MessageTypes.GetHealth)]
    [InlineData(MessageTypes.SubscribeMetrics)]
    [InlineData(MessageTypes.UpsertRule)]
    [InlineData(MessageTypes.SetSuspended)]
    public void TypesConnus_SontAcceptesParLaListeBlanche(string type) =>
        MessageTypes.IsKnownRequest(type).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("hello")]              // la casse compte : pas de correspondance approximative
    [InlineData("HELLO")]
    [InlineData("UpsertRule ")]
    [InlineData("Unknown")]
    [InlineData("../../etc/passwd")]
    public void TypesInconnus_SontRefusesParLaListeBlanche(string type) =>
        MessageTypes.IsKnownRequest(type).Should().BeFalse();

    // -- Catégorisation lecture / écriture (FR-034) ---------------------------

    [Theory]
    [InlineData(MessageTypes.Hello)]
    [InlineData(MessageTypes.GetState)]
    [InlineData(MessageTypes.GetHealth)]
    [InlineData(MessageTypes.SubscribeMetrics)]
    public void MessagesDeLecture_NExigentPasLElevation(string type) =>
        MessageTypes.RequiresElevation(type).Should().BeFalse();

    [Theory]
    [InlineData(MessageTypes.UpsertRule)]
    [InlineData(MessageTypes.DeleteRule)]
    [InlineData(MessageTypes.SetRuleEnabled)]
    [InlineData(MessageTypes.SetGlobalLimit)]
    [InlineData(MessageTypes.UpsertProfile)]
    [InlineData(MessageTypes.DeleteProfile)]
    [InlineData(MessageTypes.SetActiveProfile)]
    [InlineData(MessageTypes.SetSuspended)]
    [InlineData(MessageTypes.ImportConfig)]
    public void MessagesDEcriture_ExigentLElevation(string type) =>
        MessageTypes.RequiresElevation(type).Should().BeTrue();

    [Fact]
    public void ToutMessageConnu_EstClasseLectureOuEcriture()
    {
        // Verrouille l'exhaustivite : ajouter un message sans le categoriser doit casser
        // le test, pas passer inapercu. Un message non categorise serait traite comme une
        // lecture, donc accessible sans elevation — exactement la faille a eviter.
        foreach (string type in MessageTypes.AllRequests)
        {
            MessageTypes.IsKnownRequest(type).Should().BeTrue($"« {type} » doit être dans la liste blanche");
        }

        MessageTypes.AllRequests.Should().HaveCount(
            MessageTypes.ReadRequests.Count + MessageTypes.WriteRequests.Count);
        MessageTypes.ReadRequests.Should().NotIntersectWith(MessageTypes.WriteRequests);
    }

    [Fact]
    public void TypeInconnu_ExigeLElevationParDefaut()
    {
        // Choix deliberement conservateur : en cas de doute, on refuse plutot que d'ouvrir.
        MessageTypes.RequiresElevation("MessageQuiNExistePas").Should().BeTrue();
    }
}
