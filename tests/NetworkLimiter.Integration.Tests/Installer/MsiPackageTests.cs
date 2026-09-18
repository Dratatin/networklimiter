using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;

namespace NetworkLimiter.Integration.Tests.Installer;

/// <summary>
/// Vérifie le contenu réel du MSI produit (T122, T123).
/// </summary>
/// <remarks>
/// <para>
/// Ces tests existent parce qu'un installeur qui se construit sans erreur peut ne rien
/// appliquer. Trois pièges ont été rencontrés en écrivant ce paquet, tous silencieux :
/// </para>
/// <list type="bullet">
///   <item>WiX n'inclut que les fragments <b>référencés</b> et écarte les autres sans un mot :
///   les conditions de lancement — blocage ARM64 compris — ne figuraient pas dans le paquet ;</item>
///   <item>un raccourci déclaré au niveau du composant prend pour cible le <b>répertoire</b> de
///   celui-ci : il ouvrait l'explorateur au lieu de lancer l'application ;</item>
///   <item>seul l'exécutable du service était empaqueté, sans ses dépendances : le service
///   installé n'aurait pas démarré.</item>
/// </list>
/// <para>
/// Aucun de ces trois défauts ne produit d'erreur de construction. Seule l'inspection des tables
/// du paquet les révèle, et c'est ce que ces tests automatisent. Ils n'exigent aucune élévation :
/// lire un fichier MSI n'est pas l'installer.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MsiPackageTests
{
    [Fact]
    public void LesConditionsDeLancement_SontDansLePaquet()
    {
        List<string> conditions = Query("SELECT `Condition` FROM `LaunchCondition`");

        // Le blocage ARM64 est le plus important : WinDivert n'a pas de pilote ARM64 signe, et
        // sans cette condition l'installation reussirait pour produire un service incapable de
        // demarrer, sur une machine ou rien ne pourra jamais y remedier.
        conditions.Should().Contain(condition => condition.Contains("ARM64", StringComparison.Ordinal));
        conditions.Should().Contain(condition => condition.Contains("VersionNT64", StringComparison.Ordinal));
        conditions.Should().Contain(condition => condition.Contains("19045", StringComparison.Ordinal));
        conditions.Should().Contain(condition => condition.Contains("Privileged", StringComparison.Ordinal));
    }

    [Fact]
    public void LesRecherchesDeRegistre_SontOrdonnancees()
    {
        // Une condition qui reference une propriete jamais renseignee est toujours vraie : le
        // blocage ARM64 ne bloquerait alors rien du tout.
        List<string> searches = Query("SELECT `Property` FROM `AppSearch`");

        searches.Should().Contain("MACHINEARCHITECTURE");
        searches.Should().Contain("WINDOWSBUILDNUMBER");
    }

    [Fact]
    public void LeRaccourci_ViseLExecutable_PasUnRepertoire()
    {
        List<string> shortcuts = Query("SELECT `Target` FROM `Shortcut`");

        shortcuts.Should().ContainSingle();

        // « [#Identifiant] » designe un FICHIER. Un « [REPERTOIRE] » ouvrirait l'explorateur,
        // ce qui se construit sans la moindre erreur.
        shortcuts[0].Should().StartWith("[#", "le raccourci doit cibler un fichier");
    }

    [Fact]
    public void LeServiceEtSesDependances_SontEmpaquetes()
    {
        List<string> files = Query("SELECT `FileName` FROM `File`");

        // La colonne porte « nom court|nom long ».
        string[] longNames = [.. files.Select(name => name.Contains('|', StringComparison.Ordinal)
            ? name[(name.IndexOf('|', StringComparison.Ordinal) + 1)..]
            : name)];

        longNames.Should().Contain("NetworkLimiter.Service.exe");
        longNames.Should().Contain("NetworkLimiter.App.exe");
        longNames.Should().Contain("WinDivert.dll");
        longNames.Should().Contain("WinDivert64.sys");

        // Sans son fichier de configuration d'execution, un executable .NET ne demarre pas.
        // C'est exactement ce qui manquait quand seul l'exe etait empaquete.
        longNames.Should().Contain("NetworkLimiter.Service.runtimeconfig.json");
        longNames.Should().Contain("NetworkLimiter.App.runtimeconfig.json");
        longNames.Should().Contain("NetworkLimiter.Core.dll");
        longNames.Should().Contain("NetworkLimiter.Contracts.dll");
    }

    [Fact]
    public void LeServiceEstArrete_ALaDesinstallation()
    {
        List<string> rows = Query(
            "SELECT `Name`,`Event` FROM `ServiceControl` WHERE `Name`='NetworkLimiter'");

        rows.Should().ContainSingle();

        int events = int.Parse(rows[0].Split(char.Parse("|"))[1], CultureInfo.InvariantCulture);

        // Bit 0x20 : arreter le service a la DESINSTALLATION. C'est la propriete de surete de
        // T123 : la mort du processus ferme ses handles WinDivert, donc rend le trafic. Sans ce
        // bit, une desinstallation pourrait laisser du trafic etrangle sans plus aucun
        // composant pour le liberer — et cette fois sans retour possible (principe IV).
        (events & 0x20).Should().NotBe(0, "le service doit être arrêté à la désinstallation");

        // Bit 0x80 : retirer l'enregistrement du service a la desinstallation (SC-010).
        (events & 0x80).Should().NotBe(0, "aucun composant système ne doit subsister");
    }

    [Fact]
    public void LePiloteEstArrete_ALaDesinstallation()
    {
        List<string> rows = Query(
            "SELECT `Name`,`Event` FROM `ServiceControl` WHERE `Name`='WinDivert'");

        rows.Should().ContainSingle();

        int events = int.Parse(rows[0].Split(char.Parse("|"))[1], CultureInfo.InvariantCulture);

        (events & 0x20).Should().NotBe(0, "le pilote doit être arrêté à la désinstallation");
    }

    [Fact]
    public void LeServiceEstArreteAvantLePilote()
    {
        // L'ordre de la table ServiceControl decoule de l'ordre des composants dans Package.wxs.
        // Windows Installer ne permet pas de l'exprimer autrement, d'ou ce test : la convention
        // est verifiee plutot que supposee.
        List<string> rows = Query("SELECT `Name` FROM `ServiceControl`");

        int service = rows.FindIndex(name => name == "NetworkLimiter");
        int driver = rows.FindIndex(name => name == "WinDivert");

        service.Should().BeGreaterThanOrEqualTo(0);
        driver.Should().BeGreaterThanOrEqualTo(0);

        service.Should().BeLessThan(
            driver,
            "arrêter le pilote pendant que le service tient ses handles laisse un état que rien " +
            "ne garantit propre");
    }

    [Fact]
    public void LePiloteEstEnregistre_ADemandeEtNonAuDemarrage()
    {
        List<string> values = Query(
            "SELECT `Name`,`Value` FROM `Registry` " +
            "WHERE `Key`='SYSTEM\\CurrentControlSet\\Services\\WinDivert'");

        // Type 1 = pilote noyau, Start 3 = a la demande. Un Start 2 chargerait le pilote a
        // chaque demarrage de Windows, y compris quand l'outil ne sert pas.
        values.Should().Contain("Type|#1");
        values.Should().Contain("Start|#3");
    }

    /// <summary>
    /// Localise le MSI produit.
    /// </summary>
    /// <remarks>
    /// Échoue si le paquet est absent, plutôt que de passer en silence. Un test qui s'ignore
    /// quand son sujet manque reste vert en ne vérifiant rien — ce projet en a déjà fait
    /// l'expérience avec un garde-fou dont le filtre ne correspondait à aucun test.
    /// </remarks>
    private static string LocatePackage()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && directory.GetFiles("NetworkLimiter.slnx").Length == 0)
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Racine du dépôt introuvable depuis " + AppContext.BaseDirectory);
        }

        FileInfo[] packages = new DirectoryInfo(Path.Combine(directory.FullName, "src", "NetworkLimiter.Installer", "bin"))
            .GetFiles("NetworkLimiter.msi", SearchOption.AllDirectories);

        if (packages.Length == 0)
        {
            throw new InvalidOperationException(
                "Aucun NetworkLimiter.msi construit. Construisez src/NetworkLimiter.Installer " +
                "avant ces tests : ils vérifient le contenu du paquet, pas son source.");
        }

        return packages.OrderByDescending(file => file.LastWriteTimeUtc).First().FullName;
    }

    /// <summary>
    /// Interroge une table du MSI par liaison tardive.
    /// </summary>
    /// <remarks>
    /// Par réflexion sur l'objet COM de Windows Installer, présent sur toute machine Windows :
    /// aucun paquet supplémentaire à faire entrer dans la chaîne d'approvisionnement pour lire
    /// un fichier que l'on vient soi-même de produire.
    /// </remarks>
    private static List<string> Query(string sql)
    {
        Type installerType = Type.GetTypeFromProgID("WindowsInstaller.Installer")
            ?? throw new InvalidOperationException("Windows Installer n'est pas disponible sur cette machine.");

        object installer = Activator.CreateInstance(installerType)
            ?? throw new InvalidOperationException("Création de l'objet Windows Installer impossible.");

        object database = Invoke(installer, "OpenDatabase", LocatePackage(), 0);
        object view = Invoke(database, "OpenView", sql);

        // TryInvoke et non Invoke : « Execute » ne rend rien, et exiger un resultat de toute
        // methode COM ferait echouer les appels de commande.
        TryInvoke(view, "Execute");

        var rows = new List<string>();

        while (true)
        {
            object? record = TryInvoke(view, "Fetch");

            if (record is null)
            {
                break;
            }

            int fields = (int)GetProperty(record, "FieldCount");
            var columns = new List<string>(fields);

            for (int index = 1; index <= fields; index++)
            {
                columns.Add((string)GetProperty(record, "StringData", index));
            }

            rows.Add(string.Join('|', columns));
        }

        return rows;
    }

    private static object Invoke(object target, string method, params object[] arguments) =>
        TryInvoke(target, method, arguments)
            ?? throw new InvalidOperationException($"« {method} » n'a rien rendu.");

    // Culture invariante explicite : la liaison tardive resout les noms de membres selon la
    // culture courante, et une machine en locale turque ne trouverait pas « Fetch » — le « i »
    // sans point y change la mise en minuscules. Defaut celebre, et silencieux.
    private static object? TryInvoke(object target, string method, params object[] arguments) =>
        target.GetType().InvokeMember(
            method, BindingFlags.InvokeMethod, null, target, arguments, CultureInfo.InvariantCulture);

    private static object GetProperty(object target, string property, params object[] arguments) =>
        target.GetType().InvokeMember(
            property, BindingFlags.GetProperty, null, target, arguments, CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException($"Propriété « {property} » absente.");
}
