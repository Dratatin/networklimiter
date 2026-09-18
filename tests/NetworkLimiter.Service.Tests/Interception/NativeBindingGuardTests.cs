using System.Text.RegularExpressions;

namespace NetworkLimiter.Service.Tests.Interception;

/// <summary>
/// Garde-fou des liaisons natives.
/// </summary>
/// <remarks>
/// <para>
/// Né d'un vrai défaut : <c>WinDivertOpen</c> avait été déclaré <b>sans</b>
/// <c>SetLastError = true</c>, alors que les cinq autres liaisons le portaient. Conséquence,
/// un échec d'ouverture remontait un code d'erreur résiduel arbitraire — 203 une fois, 0 la
/// suivante — au lieu de la vraie cause. Le diagnostic conçu précisément pour identifier
/// pourquoi le pilote ne se charge pas était devenu incapable de le dire.
/// </para>
/// <para>
/// Rien dans le compilateur, les analyseurs ou les tests existants ne signale cet oubli : la
/// signature est valide, le code compile, et l'appel fonctionne. Seul l'échec devient muet.
/// C'est exactement le genre de défaut qu'un garde-fou doit attraper, parce qu'aucune revue
/// ne le verra sur une ligne au milieu de six déclarations similaires.
/// </para>
/// </remarks>
public sealed class NativeBindingGuardTests
{
    private static string ReadNativeBindingSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName, "src", "NetworkLimiter.Service", "Interception", "WinDivertNative.cs");

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"WinDivertNative.cs introuvable depuis « {AppContext.BaseDirectory} ». " +
            "Ce garde-fou balaye les sources : il ne peut pas s'exécuter sans elles.");
    }

    [Fact]
    public void ToutesLesLiaisonsNatives_CapturentLErreurWindows()
    {
        string source = ReadNativeBindingSource();

        // Chaque attribut LibraryImport, de son ouverture a sa parenthese fermante.
        MatchCollection imports = Regex.Matches(
            source,
            @"\[LibraryImport\((?<args>[^\]]*)\)\]",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(2));

        imports.Should().NotBeEmpty("le fichier doit contenir des liaisons natives");

        var missing = new List<string>();

        foreach (Match import in imports)
        {
            string arguments = import.Groups["args"].Value;

            if (!arguments.Contains("SetLastError", StringComparison.Ordinal))
            {
                // Extrait le point d'entree pour nommer la liaison fautive.
                Match entryPoint = Regex.Match(
                    arguments, @"EntryPoint\s*=\s*""(?<name>[^""]+)""",
                    RegexOptions.None, TimeSpan.FromSeconds(1));

                missing.Add(entryPoint.Success ? entryPoint.Groups["name"].Value : arguments.Trim());
            }
        }

        missing.Should().BeEmpty(
            "sans SetLastError, Marshal.GetLastWin32Error() rend une valeur résiduelle " +
            "arbitraire et un échec devient indiagnosticable");
    }

    [Fact]
    public void LeBalayage_TrouveBienLesSixLiaisonsAttendues()
    {
        // Sans ce controle, une expression reguliere devenue fausse ferait passer le test
        // precedent en ne trouvant rien — reproduisant le defaut qu'il est cense empecher.
        string source = ReadNativeBindingSource();

        MatchCollection imports = Regex.Matches(
            source, @"\[LibraryImport\(", RegexOptions.None, TimeSpan.FromSeconds(2));

        imports.Should().HaveCountGreaterThanOrEqualTo(6,
            "Open, Close, Shutdown, SetParam, RecvEx, SendEx et HelperCompileFilter");
    }
}
