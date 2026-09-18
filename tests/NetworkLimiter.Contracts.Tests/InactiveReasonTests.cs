using FluentAssertions;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Contracts.Serialization;
using Xunit;

namespace NetworkLimiter.Contracts.Tests;

/// <summary>
/// Le contrat des raisons d'inactivité (FR-026).
/// </summary>
/// <remarks>
/// <para>
/// L'invariant vaut d'être énoncé seul : une règle qui ne s'applique pas doit <b>toujours</b>
/// dire pourquoi. Une règle inactive sans raison est le pire des deux mondes — l'interface
/// annonce « ne s'applique pas » et ne peut rien ajouter, laissant l'utilisateur devant un
/// problème sans prise. C'est exactement la défaillance silencieuse que le principe VI exclut.
/// </para>
/// <para>
/// Le vocabulaire appartient au contrat parce qu'il traverse la frontière IPC. Il voyageait
/// auparavant sous forme de chaîne obtenue par <c>ToString()</c> sur une énumération interne au
/// service : un simple renommage y cassait l'interface sans qu'aucun compilateur ne bronche.
/// </para>
/// </remarks>
public sealed class InactiveReasonTests
{
    [Fact]
    public void UneRegleInactiveSansRaison_EstRefuseeParLeContrat()
    {
        RuleStateDto rule = Build(RuleApplicationStatus.Inactive, reason: null);

        rule.Validate().Should().NotBeNull(
            "une règle inactive muette laisse l'utilisateur sans aucune prise sur son problème");
    }

    [Fact]
    public void UneRegleActiveAvecUneRaison_EstRefuseeParLeContrat()
    {
        RuleStateDto rule = Build(
            RuleApplicationStatus.Active, RuleInactiveReasonDto.ApplicationNotRunning);

        // L'incoherence inverse compte autant : elle ferait afficher une explication
        // d'inactivite sous une regle qui limite bel et bien.
        rule.Validate().Should().NotBeNull();
    }

    [Theory]
    [InlineData(RuleInactiveReasonDto.InterceptionUnavailable)]
    [InlineData(RuleInactiveReasonDto.GloballySuspended)]
    [InlineData(RuleInactiveReasonDto.RuleDisabled)]
    [InlineData(RuleInactiveReasonDto.ExecutablePathNotFound)]
    [InlineData(RuleInactiveReasonDto.MatchedByFallbackName)]
    [InlineData(RuleInactiveReasonDto.PackagedAppUnsupported)]
    [InlineData(RuleInactiveReasonDto.ApplicationNotRunning)]
    public void ChaqueRaisonConnue_EstAcceptee(RuleInactiveReasonDto reason)
    {
        Build(RuleApplicationStatus.Inactive, reason).Validate().Should().BeNull();
    }

    [Fact]
    public void UneRegleActiveSansRaison_EstAcceptee()
    {
        Build(RuleApplicationStatus.Active, reason: null).Validate().Should().BeNull();
    }

    [Fact]
    public void UneValeurHorsEnumeration_EstRefusee()
    {
        // Une valeur fabriquee hors de l'enumeration passerait un simple controle de nullite.
        RuleStateDto rule = Build(RuleApplicationStatus.Inactive, (RuleInactiveReasonDto)999);

        rule.Validate().Should().NotBeNull();
    }

    [Fact]
    public void LesRaisons_VoyagentEnToutesLettres()
    {
        // Le serialiseur de PRODUCTION, pas une instance ad hoc : un test qui fabrique ses
        // propres options prouve ce que ces options font, pas ce qui circule reellement.
        string json = MessageSerializer.Serialize(
            MessageEnvelope.CreateResult(
                Guid.NewGuid(),
                MessageTypes.GetState + MessageTypes.ResultSuffix,
                Build(RuleApplicationStatus.Inactive, RuleInactiveReasonDto.GloballySuspended)));

        // En numerique, reordonner l'enumeration changerait silencieusement le sens pour un
        // client plus ancien — sans qu'aucune version de protocole ne bouge.
        json.Should().Contain("GloballySuspended");
    }

    [Fact]
    public void LEtatSerialise_SeRelitIdentique()
    {
        RuleStateDto original = Build(
            RuleApplicationStatus.Inactive, RuleInactiveReasonDto.ExecutablePathNotFound);

        MessageEnvelope envelope = MessageEnvelope.CreateResult(
            Guid.NewGuid(), MessageTypes.GetState + MessageTypes.ResultSuffix, original);

        RuleStateDto roundTripped = MessageSerializer.ReadPayload<RuleStateDto>(
            MessageSerializer.Deserialize(MessageSerializer.SerializeToUtf8Bytes(envelope)));

        roundTripped.Status.Should().Be(original.Status);
        roundTripped.InactiveReason.Should().Be(original.InactiveReason);
        roundTripped.Validate().Should().BeNull();
    }

    [Fact]
    public void LaPriorite_VaDuPlusEnglobantAuPlusLocal()
    {
        // L'ordre de declaration EST la priorite. « Application non lancee » annonce a
        // quelqu'un dont toute la limitation est suspendue l'enverrait chercher au mauvais
        // endroit — et c'est le genre de detail qu'un renommage casse sans bruit.
        RuleInactiveReasonDto[] byPriority = Enum.GetValues<RuleInactiveReasonDto>();

        byPriority[0].Should().Be(RuleInactiveReasonDto.InterceptionUnavailable);
        byPriority[1].Should().Be(RuleInactiveReasonDto.GloballySuspended);
        byPriority[^1].Should().Be(RuleInactiveReasonDto.ApplicationNotRunning);
    }

    private static RuleStateDto Build(RuleApplicationStatus status, RuleInactiveReasonDto? reason) => new()
    {
        Rule = new RuleDto
        {
            Id = Guid.NewGuid(),
            Target = new AppIdentityDto
            {
                ExecutablePath = @"c:\jeux\jeu.exe",
                ExecutableName = "jeu.exe",
                DisplayName = "Jeu",
            },
            DownloadBytesPerSecond = 204_800,
            UploadBytesPerSecond = null,
            Enabled = true,
            ExemptFromGlobal = false,
        },
        Status = status,
        InactiveReason = reason,
        MatchedProcessCount = 0,
    };
}
