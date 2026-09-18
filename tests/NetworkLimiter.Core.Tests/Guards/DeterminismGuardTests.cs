using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using NetworkLimiter.Core.Shaping;

namespace NetworkLimiter.Core.Tests.Guards;

/// <summary>
/// Garde-fou du principe III — déterminisme et absence de dépendance à Windows.
/// </summary>
/// <remarks>
/// <para>
/// Le principe III est non négociable : la logique de calcul vit dans des composants purs,
/// sans dépendance à Windows, pilotés par une horloge injectée, et sans une seule attente
/// réelle. Ces règles ne tiennent pas toutes seules — elles se contournent par un simple
/// <c>using</c> ajouté un jour de fatigue.
/// </para>
/// <para>
/// Ces tests portent la catégorie <c>Determinism</c> et sont exécutés isolément par la CI. Sans
/// eux, le filtre correspondant ne trouvait aucun test et le job passait au vert <b>sans rien
/// vérifier</b> — une porte verte qui ne garde rien est pire qu'une porte absente, parce qu'on
/// cesse de se poser la question.
/// </para>
/// </remarks>
[Trait("Category", "Determinism")]
public sealed class DeterminismGuardTests
{
    /// <summary>Motifs qui trahissent une attente réelle ou une horloge non injectée.</summary>
    private static readonly (string Pattern, string Why)[] ForbiddenInTests =
    [
        (@"\bThread\s*\.\s*Sleep\b", "Thread.Sleep rend les tests lents puis instables puis désactivés"),
        (@"\bTask\s*\.\s*Delay\b", "Task.Delay introduit une attente réelle"),
        (@"\bDateTime\s*\.\s*Now\b", "DateTime.Now dépend de l'horloge système"),
        (@"\bDateTime\s*\.\s*UtcNow\b", "DateTime.UtcNow dépend de l'horloge système"),
        (@"\bDateTimeOffset\s*\.\s*Now\b", "DateTimeOffset.Now dépend de l'horloge système"),
        (@"\bDateTimeOffset\s*\.\s*UtcNow\b", "DateTimeOffset.UtcNow dépend de l'horloge système"),
        (@"\bTimeProvider\s*\.\s*System\b", "TimeProvider.System est l'horloge réelle"),
        (@"new\s+Stopwatch\b", "Stopwatch mesure du temps réel"),
    ];

    /// <summary>Assemblys dont la présence trahirait une dépendance à Windows.</summary>
    private static readonly string[] WindowsAssemblies =
    [
        "Microsoft.Win32.Registry",
        "Microsoft.Win32.SystemEvents",
        "System.Management",
        "System.ServiceProcess.ServiceController",
        "System.Security.AccessControl",
        "System.Security.Principal.Windows",
        "System.IO.Pipes.AccessControl",
        "PresentationFramework",
        "WindowsBase",
    ];

    // -- Core ne dépend pas de Windows ----------------------------------------

    [Fact]
    public void Core_NeCibleAucunePlateformeWindows()
    {
        // Le TFM est la barriere la plus solide : « net10.0-windows » rendrait les API
        // Windows disponibles, et l'interdiction ne reposerait plus que sur la discipline.
        Assembly core = typeof(TokenBucket).Assembly;
        string? framework = core.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

        framework.Should().NotBeNull();
        framework.Should().NotContain("windows",
            "NetworkLimiter.Core doit rester compilable sans Windows (principe III)");
    }

    [Fact]
    public void Core_NeReferenceAucunAssemblyWindows()
    {
        Assembly core = typeof(TokenBucket).Assembly;

        IEnumerable<string> referenced = core.GetReferencedAssemblies()
                                             .Select(name => name.Name ?? string.Empty);

        referenced.Should().NotIntersectWith(WindowsAssemblies,
            "une dépendance à Windows dans Core rendrait la logique de mise en forme " +
            "intestable sans machine Windows");
    }

    [Fact]
    public void Core_NeContientAucunPInvoke()
    {
        // Tout l'interop natif est confine au service. Un DllImport dans Core signifierait
        // que du code non verifiable a franchi la frontiere.
        Assembly core = typeof(TokenBucket).Assembly;

        IEnumerable<MethodInfo> nativeMethods = core
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.Attributes.HasFlag(MethodAttributes.PinvokeImpl));

        nativeMethods.Should().BeEmpty("l'interop natif est confiné au service");
    }

    // -- Les tests de Core sont déterministes ---------------------------------

    [Fact]
    public void LesTestsDeCore_NUtilisentNiAttenteReelleNiHorlogeSysteme()
    {
        // Balaye les sources plutot que l'IL : le message d'echec nomme alors le fichier et
        // la ligne fautifs, ce qui rend la regle corrigeable en quelques secondes.
        DirectoryInfo testRoot = LocateSourceDirectory();

        var violations = new List<string>();

        foreach (FileInfo file in testRoot.GetFiles("*.cs", SearchOption.AllDirectories))
        {
            // Ce fichier contient les motifs interdits sous forme d'expressions regulieres.
            if (string.Equals(file.Name, "DeterminismGuardTests.cs", StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file.FullName);

            for (int index = 0; index < lines.Length; index++)
            {
                foreach ((string pattern, string why) in ForbiddenInTests)
                {
                    if (Regex.IsMatch(lines[index], pattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
                    {
                        violations.Add($"{file.Name}:{index + 1} — {why}");
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            "le principe III interdit toute attente réelle et toute horloge système dans les " +
            "tests : ils doivent rester rapides et reproductibles, sinon ils deviennent " +
            "instables puis désactivés");
    }

    [Fact]
    public void LeBalayageDeSources_TrouveBienDesFichiers()
    {
        // Sans ce controle, une erreur de chemin ferait passer le test precedent en ne
        // balayant rien — exactement le defaut que ces gardes-fous existent pour empecher.
        DirectoryInfo testRoot = LocateSourceDirectory();

        testRoot.GetFiles("*.cs", SearchOption.AllDirectories)
                .Should().HaveCountGreaterThan(5, "le balayage doit voir les fichiers de test");
    }

    /// <summary>Remonte depuis le répertoire de sortie jusqu'aux sources du projet de test.</summary>
    private static DirectoryInfo LocateSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.GetFiles("NetworkLimiter.Core.Tests.csproj").Length > 0)
            {
                return directory;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Sources du projet de test introuvables depuis « {AppContext.BaseDirectory} ». " +
            "Ce garde-fou balaye les sources : il ne peut pas s'exécuter sans elles.");
    }
}
