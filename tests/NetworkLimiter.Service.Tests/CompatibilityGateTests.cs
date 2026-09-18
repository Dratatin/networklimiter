using System.Runtime.InteropServices;
using NetworkLimiter.Service;

namespace NetworkLimiter.Service.Tests;

/// <summary>
/// Vérification de compatibilité au démarrage — FR-035, principe II.
/// </summary>
/// <remarks>
/// <para>
/// Le principe II impose de refuser explicitement de démarrer hors matrice, plutôt que de
/// dégrader silencieusement. Un limiteur qui démarre sans pouvoir limiter est pire qu'un
/// limiteur absent : l'utilisateur croit sa connexion protégée alors qu'elle ne l'est pas.
/// </para>
/// <para>
/// La décision est une fonction pure de la plateforme observée, ce qui permet de tester toute
/// la matrice — y compris ARM64, qu'aucune machine de développement ne peut exercer.
/// </para>
/// </remarks>
public sealed class CompatibilityGateTests
{
    private static PlatformInfo Platform(
        int build = 22631,
        Architecture architecture = Architecture.X64,
        bool isWindows = true) =>
        new(build, architecture, isWindows);

    // -- Cibles supportées ----------------------------------------------------

    [Theory]
    [InlineData(19045)]   // Windows 10 22H2, borne basse exacte
    [InlineData(22000)]   // Windows 11 21H2
    [InlineData(22631)]   // Windows 11 23H2
    [InlineData(26100)]   // Windows 11 24H2
    public void BuildsDansLaMatrice_SontSupportes(int build)
    {
        CompatibilityResult result = CompatibilityGate.Evaluate(Platform(build: build));

        result.Verdict.Should().Be(CompatibilityVerdict.Supported);
        result.Diagnostic.Should().BeNull();
    }

    // -- Version trop ancienne ------------------------------------------------

    [Theory]
    [InlineData(19044)]   // Windows 10 21H2, juste sous la borne
    [InlineData(19041)]   // Windows 10 2004
    [InlineData(10240)]   // Windows 10 1507
    [InlineData(9600)]    // Windows 8.1
    public void BuildsTropAnciens_SontRefuses(int build)
    {
        CompatibilityResult result = CompatibilityGate.Evaluate(Platform(build: build));

        result.Verdict.Should().Be(CompatibilityVerdict.UnsupportedOsVersion);
        result.Diagnostic.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void RefusPourVersion_NommeLaVersionObserveeEtLaVersionMinimale()
    {
        CompatibilityResult result = CompatibilityGate.Evaluate(Platform(build: 19044));

        result.Diagnostic.Should().Contain("19044");
        result.Diagnostic.Should().Contain("19045");
    }

    // -- Architecture ---------------------------------------------------------

    [Theory]
    [InlineData(Architecture.Arm64)]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.X86)]
    public void ArchitecturesNonSupportees_SontRefusees(Architecture architecture)
    {
        CompatibilityResult result = CompatibilityGate.Evaluate(Platform(architecture: architecture));

        result.Verdict.Should().Be(CompatibilityVerdict.UnsupportedArchitecture);
        result.Diagnostic.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void RefusArm64_NommeLAbsenceDePiloteWinDivert()
    {
        // La cause doit etre nommee : « architecture non supportee » laisserait croire a un
        // oubli reparable, alors que WinDivert ne publie aucun pilote ARM64 et qu'un pilote
        // noyau ne s'execute pas sous emulation. L'utilisateur doit comprendre que ce n'est
        // pas un reglage a changer.
        CompatibilityResult result = CompatibilityGate.Evaluate(Platform(architecture: Architecture.Arm64));

        result.Diagnostic.Should().Contain("ARM64");
        result.Diagnostic.Should().Contain("WinDivert");
    }

    // -- Système non Windows --------------------------------------------------

    [Fact]
    public void SystemeNonWindows_EstRefuse()
    {
        CompatibilityResult result = CompatibilityGate.Evaluate(Platform(isWindows: false));

        result.Verdict.Should().Be(CompatibilityVerdict.UnsupportedOperatingSystem);
    }

    // -- Priorité des refus ---------------------------------------------------

    [Fact]
    public void ArchitectureEtVersionInvalides_LArchitecturePrime()
    {
        // L'architecture est redhibitoire et definitive ; la version se corrige par une mise
        // a jour. Nommer la cause insurmontable en premier evite d'envoyer l'utilisateur
        // mettre a jour Windows pour rien.
        CompatibilityResult result = CompatibilityGate.Evaluate(
            Platform(build: 9600, architecture: Architecture.Arm64));

        result.Verdict.Should().Be(CompatibilityVerdict.UnsupportedArchitecture);
    }

    // -- Constante et cohérence -----------------------------------------------

    [Fact]
    public void BuildMinimal_CorrespondAuPlan() =>
        CompatibilityGate.MinimumOsBuild.Should().Be(19045);

    [Fact]
    public void AucuneLimite_NEstAppliqueeQuandLaPlateformeEstRefusee()
    {
        // Verrouille l'intention : un verdict de refus doit interdire la limitation, jamais
        // laisser le service continuer « au mieux ».
        foreach (CompatibilityVerdict verdict in Enum.GetValues<CompatibilityVerdict>())
        {
            bool mayLimit = CompatibilityGate.AllowsInterception(verdict);

            mayLimit.Should().Be(verdict == CompatibilityVerdict.Supported);
        }
    }

    [Fact]
    public void PlateformeCourante_EstLisibleSansLever()
    {
        // La machine de developpement doit etre dans la matrice ; si ce test echoue, c'est
        // l'environnement qui est hors matrice, pas le code.
        PlatformInfo current = CompatibilityGate.GetCurrentPlatform();

        current.OsBuild.Should().BeGreaterThan(0);
        current.IsWindows.Should().BeTrue();
    }
}
