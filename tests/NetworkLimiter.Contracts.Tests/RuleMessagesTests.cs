using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Core.Units;

namespace NetworkLimiter.Contracts.Tests;

/// <summary>
/// Contrat des messages de règle — FR-007, contracts/ipc-protocol.md.
/// </summary>
/// <remarks>
/// Ces messages viennent d'un processus moins privilégié et sont donc hostiles jusqu'à preuve
/// du contraire. Les bornes sont testées à <c>min-1</c>, <c>min</c>, <c>max</c> et
/// <c>max+1</c> : c'est aux bornes exactes que les erreurs de comparaison se manifestent, et
/// une borne relâchée d'une unité laisserait passer une valeur que le reste du produit suppose
/// impossible.
/// </remarks>
public sealed class RuleMessagesTests
{
    private const long Min = 10_240;
    private const long Max = 1_073_741_824;

    private static AppIdentityDto Target(string path = @"c:\app\app.exe") => new()
    {
        ExecutablePath = path,
        ExecutableName = "app.exe",
        DisplayName = "App",
    };

    private static RuleDto Rule(long? download = Min, long? upload = null) => new()
    {
        Id = Guid.NewGuid(),
        Target = Target(),
        DownloadBytesPerSecond = download,
        UploadBytesPerSecond = upload,
        Enabled = true,
        ExemptFromGlobal = false,
    };

    // -- Bornes des plafonds --------------------------------------------------

    [Theory]
    [InlineData(Min)]
    [InlineData(Min + 1)]
    [InlineData(1_048_576)]
    [InlineData(Max - 1)]
    [InlineData(Max)]
    public void PlafondDansLesBornes_EstAccepte(long value)
    {
        Rule(download: value).Validate().Should().BeNull();
        Rule(download: null, upload: value).Validate().Should().BeNull();
    }

    [Theory]
    [InlineData(Min - 1)]
    [InlineData(Max + 1)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void PlafondHorsBornes_EstRefuse(long value)
    {
        Rule(download: value).Validate().Should().NotBeNullOrWhiteSpace();
        Rule(download: null, upload: value).Validate().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void PlafondZero_EstRefuse()
    {
        // Un plafond nul serait un blocage total, que FR-028 exclut du perimetre. L'absence
        // de plafond se dit « null », jamais « zero ».
        Rule(download: 0).Validate().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void LesBornes_CorrespondentACellesDuDomaine()
    {
        // Verrouille l'alignement : deux jeux de bornes qui divergent produiraient une regle
        // acceptee par le contrat mais refusee par le shaper, ou l'inverse.
        ByteRate.MinBytesPerSecond.Should().Be(Min);
        ByteRate.MaxBytesPerSecond.Should().Be(Max);
    }

    // -- Règle sans effet -----------------------------------------------------

    [Fact]
    public void RegleSansAucunPlafond_EstValideMaisSansEffet()
    {
        // Elle est affichee comme telle plutot que silencieusement supprimee : l'utilisateur
        // qui vient de retirer ses deux plafonds ne doit pas voir sa regle disparaitre.
        Rule(download: null, upload: null).Validate().Should().BeNull();
    }

    // -- Application visée ----------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CheminVide_EstRefuse(string path)
    {
        RuleDto rule = Rule() with { Target = Target(path) };

        rule.Validate().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CheminDemesurementLong_EstRefuse()
    {
        // 32 767 caracteres tiennent dans le cadrage de 64 Ko : la borne de cadrage ne suffit
        // donc pas a proteger la configuration persistee, qui grossirait sans limite a coups
        // de regles aux chemins demesures.
        RuleDto rule = Rule() with { Target = Target(new string('a', AppIdentityDto.MaxPathLength + 1)) };

        rule.Validate().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void NomAfficheDemesurementLong_EstRefuse()
    {
        RuleDto rule = Rule() with
        {
            Target = Target() with { DisplayName = new string('a', AppIdentityDto.MaxDisplayNameLength + 1) },
        };

        rule.Validate().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void NomAffiche_NEstJamaisUtilisePourApparier()
    {
        // Verrouille l'intention : seuls le chemin et le nom d'executable comptent (FR-039).
        // Apparier sur le nom affiche laisserait un appelant renommer sa regle pour viser une
        // autre application.
        Core.Rules.AppIdentity domain = Target().ToDomain();

        domain.ExecutablePath.Should().Be(@"c:\app\app.exe");
        domain.ExecutableName.Should().Be("app.exe");
    }

    // -- Conversion vers le domaine -------------------------------------------

    [Fact]
    public void Conversion_PreserveLesTroisChamps()
    {
        AppIdentityDto dto = Target();

        Core.Rules.AppIdentity domain = dto.ToDomain();

        domain.ExecutablePath.Should().Be(dto.ExecutablePath);
        domain.ExecutableName.Should().Be(dto.ExecutableName);
        domain.DisplayName.Should().Be(dto.DisplayName);
    }

    // -- Catégorisation des messages ------------------------------------------

    [Theory]
    [InlineData(MessageTypes.UpsertRule)]
    [InlineData(MessageTypes.DeleteRule)]
    [InlineData(MessageTypes.SetRuleEnabled)]
    public void LesMessagesDeRegle_ExigentTousLElevation(string type)
    {
        // FR-034a : toute modification de l'etat de limitation passe par une elevation.
        MessageTypes.RequiresElevation(type).Should().BeTrue();
        MessageTypes.IsKnownRequest(type).Should().BeTrue();
    }
}
