using System.Security.AccessControl;
using System.Security.Principal;
using NetworkLimiter.Service.Persistence;

namespace NetworkLimiter.Service.Tests.Persistence;

/// <summary>
/// Permissions du répertoire de données — FR-034, SC-013, contracts/config-schema.md.
/// </summary>
/// <remarks>
/// <para>
/// Ces ACL sont le <b>verrou réel</b> de FR-034. Si un utilisateur standard pouvait écrire le
/// fichier de configuration, il lèverait ses propres limites avec un éditeur de texte, et toute
/// l'autorisation IPC ne serait qu'un verrou d'interface, contournable en quelques secondes.
/// </para>
/// <para>
/// Ces tests vérifient la DACL réellement produite. La preuve complète de SC-013 — un compte
/// standard qui échoue effectivement à écrire — exige un second compte utilisateur et vit dans
/// les tests d'intégration sur VM ; ce qui est vérifiable ici l'est.
/// </para>
/// </remarks>
public sealed class ConfigAclTests : IDisposable
{
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private readonly string _directory;

    public ConfigAclTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nl-acl-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nettoyage opportuniste.
        }
    }

    private static List<FileSystemAccessRule> RulesFor(DirectorySecurity security, SecurityIdentifier sid) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Where(rule => rule.IdentityReference.Equals(sid))
                .ToList();

    // -- Permissions construites ----------------------------------------------

    [Fact]
    public void LHeritageEstDesactive()
    {
        // Sans cela, le repertoire heriterait des permissions de %ProgramData%, qui
        // autorisent les utilisateurs a creer des fichiers — de quoi contourner le verrou
        // par remplacement.
        ConfigAcl.BuildSecurity().AreAccessRulesProtected.Should().BeTrue();
    }

    [Fact]
    public void SystemEtAdministrateurs_OntLeControleTotal()
    {
        DirectorySecurity security = ConfigAcl.BuildSecurity();

        RulesFor(security, System).Should().Contain(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));

        RulesFor(security, Administrators).Should().Contain(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
    }

    [Fact]
    public void Utilisateurs_PeuventLire()
    {
        // L'interface non elevee doit pouvoir lire la configuration pour l'afficher (FR-034).
        RulesFor(ConfigAcl.BuildSecurity(), Users).Should().Contain(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            rule.FileSystemRights.HasFlag(FileSystemRights.Read));
    }

    [Theory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    [InlineData(FileSystemRights.FullControl)]
    public void Utilisateurs_NOntAucunDroitDeModification(FileSystemRights forbidden)
    {
        // Chacun de ces droits suffirait a lever ses propres limites : ecrire le fichier,
        // l'effacer pour repartir sans regles, ou reecrire l'ACL elle-meme.
        RulesFor(ConfigAcl.BuildSecurity(), Users).Should().NotContain(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            rule.FileSystemRights.HasFlag(forbidden));
    }

    [Fact]
    public void LesPermissions_SontHeriteesParLesFichiersDuRepertoire()
    {
        // Sans heritage, le fichier de configuration cree ensuite porterait ses propres
        // permissions par defaut et le verrou ne s'appliquerait pas a lui.
        RulesFor(ConfigAcl.BuildSecurity(), Users).Should().OnlyContain(rule =>
            rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit));
    }

    // -- Détection d'un relâchement -------------------------------------------

    [Fact]
    public void PermissionsConstruites_SontReconnuesConformes()
    {
        ConfigAcl.IsAlreadySecured(ConfigAcl.BuildSecurity()).Should().BeTrue();
    }

    [Fact]
    public void PermissionsNonProtegees_SontReconnuesNonConformes()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);

        ConfigAcl.IsAlreadySecured(security).Should().BeFalse();
    }

    [Theory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.FullControl)]
    public void DroitDEcritureAccordeAuxUtilisateurs_EstDetecte(FileSystemRights granted)
    {
        // Le cas reel : un outil de nettoyage ou un script d'entreprise elargit les
        // permissions. Sans detection, le verrou serait leve sans que personne ne le sache.
        DirectorySecurity security = ConfigAcl.BuildSecurity();
        security.AddAccessRule(new FileSystemAccessRule(
            Users, granted, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));

        ConfigAcl.IsAlreadySecured(security).Should().BeFalse();
    }

    [Fact]
    public void IsAlreadySecured_SansPermissions_Leve()
    {
        Action act = () => ConfigAcl.IsAlreadySecured(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // -- Propriété du répertoire ----------------------------------------------

    [Fact]
    public void PermissionsConstruites_AppartiennentAuxAdministrateurs()
    {
        // Le proprietaire d'un objet Windows conserve implicitement WRITE_DAC : il peut
        // reecrire l'ACL quelles que soient les ACE posees.
        DirectorySecurity security = ConfigAcl.BuildSecurity();
        security.SetOwner(Administrators);

        ConfigAcl.IsOwnedByPrivilegedPrincipal(security).Should().BeTrue();
    }

    [Fact]
    public void RepertoirePossedeParUnUtilisateurStandard_EstReconnuNonConforme()
    {
        // Le cas d'attaque : un utilisateur cree %ProgramData%\NetworkLimiter AVANT la
        // premiere execution du service. Il en devient proprietaire, et toutes les
        // restrictions posees ensuite lui restent contournables. Aucune ACE ne parait
        // anormale — seule la propriete trahit le probleme.
        DirectorySecurity security = ConfigAcl.BuildSecurity();
        security.SetOwner(WindowsIdentity.GetCurrent().User!);

        ConfigAcl.IsOwnedByPrivilegedPrincipal(security).Should().BeFalse();
        ConfigAcl.IsAlreadySecured(security).Should().BeFalse(
            "la propriété emporte le droit de réécrire l'ACL");
    }

    [Fact]
    public void ProprieteSystem_EstAcceptee()
    {
        DirectorySecurity security = ConfigAcl.BuildSecurity();
        security.SetOwner(System);

        ConfigAcl.IsOwnedByPrivilegedPrincipal(security).Should().BeTrue();
    }

    [Fact]
    public void IsOwnedByPrivilegedPrincipal_SansPermissions_Leve()
    {
        Action act = () => ConfigAcl.IsOwnedByPrivilegedPrincipal(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // Les tests qui touchent reellement au disque vivent desormais dans la suite
    // d'integration : poser une ACL avec reprise de propriete exige des privileges
    // administrateur, que la suite unitaire n'a pas et ne doit pas exiger.
}
