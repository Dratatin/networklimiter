using System.Security.AccessControl;
using System.Security.Principal;
using NetworkLimiter.Service.Persistence;

namespace NetworkLimiter.Integration.Tests.Security;

/// <summary>
/// SC-013 — un utilisateur standard ne peut pas modifier les limites.
/// </summary>
/// <remarks>
/// <para>
/// C'est la preuve que FR-034 tient réellement. L'autorisation IPC refuse les écritures d'un
/// appelant non élevé, mais si ce même utilisateur pouvait éditer le fichier de configuration
/// avec le Bloc-notes, tout ce contrôle ne serait qu'un verrou d'interface, contournable en
/// quelques secondes.
/// </para>
/// <para>
/// Le test s'exécute sous un <b>jeton restreint</b> dérivé du jeton courant, SID
/// Administrateurs désactivé — le mécanisme qu'emploie UAC lui-même. Créer un second compte
/// Windows aurait rendu ce test impossible en intégration continue, donc réservé à une
/// vérification manuelle occasionnelle, c'est-à-dire en pratique jamais exécuté.
/// </para>
/// <para>
/// Il exige des privilèges administrateur pour <i>poser</i> les ACL. Sans eux, il échoue avec
/// un message explicite plutôt que d'être ignoré : un test de sécurité silencieusement sauté
/// est pire qu'un test absent.
/// </para>
/// </remarks>
[Trait("Requires", "Elevation")]
public sealed class ConfigAclTests : IDisposable
{
    private readonly string _directory;
    private readonly string _configPath;

    public ConfigAclTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nl-sc013-" + Guid.NewGuid().ToString("N"));
        _configPath = Path.Combine(_directory, "config.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                var info = new DirectoryInfo(_directory);
                DirectorySecurity security = info.GetAccessControl();
                security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
                info.SetAccessControl(security);

                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nettoyage opportuniste.
        }
    }

    private void ArrangeSecuredConfig()
    {
        // Volontairement une assertion et non un « skip ». Un test de sécurité silencieusement
        // sauté est pire qu'un test absent : la suite reste verte et personne ne remarque que
        // SC-013 n'est plus vérifié. Ici, l'exécution sans élévation échoue bruyamment avec
        // la marche à suivre.
        //
        // Toute la suite d'intégration exige l'élévation — elle enregistre un pilote noyau et
        // pilote un service — et c'est pourquoi elle vit dans integration.yml, sur un runner
        // dédié, et non dans la CI ordinaire.
        RestrictedToken.CurrentIsAdministrator().Should().BeTrue(
            "la suite d'intégration doit s'exécuter depuis une invite élevée : poser une ACL " +
            "restrictive puis vérifier qu'un utilisateur standard s'y heurte exige des " +
            "privilèges administrateur");

        ConfigAcl.EnsureSecured(_directory);
        File.WriteAllText(_configPath, """{"schemaVersion":1}""");
    }

    private static void ShouldBeDenied(Action act, string because)
    {
        act.Should().Throw<UnauthorizedAccessException>(because);
    }

    // -- Écriture directe -----------------------------------------------------

    [Fact]
    public void UtilisateurStandard_NePeutPasEcrireLaConfiguration()
    {
        ArrangeSecuredConfig();

        bool ran = RestrictedToken.TryRunWithoutAdministrator(() =>
            ShouldBeDenied(
                () => File.WriteAllText(_configPath, """{"schemaVersion":1,"pirate":true}"""),
                "sinon l'utilisateur lèverait ses propres limites avec un éditeur de texte"));

        ran.Should().BeTrue("le jeton restreint doit avoir pu être créé");
    }

    [Fact]
    public void UtilisateurStandard_NePeutPasSupprimerLaConfiguration()
    {
        // Supprimer le fichier reviendrait a repartir sans aucune regle : aussi efficace
        // que de l'editer, pour lever ses limites.
        ArrangeSecuredConfig();

        bool ran = RestrictedToken.TryRunWithoutAdministrator(() =>
            ShouldBeDenied(() => File.Delete(_configPath), "la suppression équivaut à une levée des limites"));

        ran.Should().BeTrue();
    }

    [Fact]
    public void UtilisateurStandard_NePeutPasRemplacerLaConfiguration()
    {
        // Le contournement classique : ecrire ailleurs puis remplacer.
        ArrangeSecuredConfig();
        string substitute = Path.Combine(Path.GetTempPath(), "nl-sub-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(substitute, """{"schemaVersion":1,"pirate":true}""");

        try
        {
            bool ran = RestrictedToken.TryRunWithoutAdministrator(() =>
                ShouldBeDenied(
                    () => File.Move(substitute, _configPath, overwrite: true),
                    "remplacer le fichier contourne l'interdiction d'écriture"));

            ran.Should().BeTrue();
        }
        finally
        {
            File.Delete(substitute);
        }
    }

    [Fact]
    public void UtilisateurStandard_NePeutPasCreerUnFichierDansLeRepertoire()
    {
        // Pouvoir creer un fichier permettrait de deposer un config.json de remplacement
        // apres suppression, ou d'interferer avec le fichier temporaire d'ecriture atomique.
        ArrangeSecuredConfig();

        bool ran = RestrictedToken.TryRunWithoutAdministrator(() =>
            ShouldBeDenied(
                () => File.WriteAllText(Path.Combine(_directory, "intrus.json"), "x"),
                "créer un fichier ici permettrait d'interférer avec l'écriture atomique"));

        ran.Should().BeTrue();
    }

    [Fact]
    public void UtilisateurStandard_NePeutPasModifierLesPermissions()
    {
        // Le contournement ultime : reecrire l'ACL pour s'accorder le droit d'ecrire.
        ArrangeSecuredConfig();

        bool ran = RestrictedToken.TryRunWithoutAdministrator(() =>
        {
            Action act = () =>
            {
                var info = new DirectoryInfo(_directory);
                DirectorySecurity security = info.GetAccessControl();
                security.AddAccessRule(new FileSystemAccessRule(
                    WindowsIdentity.GetCurrent().User!,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
                info.SetAccessControl(security);
            };

            act.Should().Throw<Exception>(
                "réécrire l'ACL permettrait de lever soi-même toutes les restrictions");
        });

        ran.Should().BeTrue();
    }

    // -- La lecture reste possible --------------------------------------------

    [Fact]
    public void UtilisateurStandard_PeutLireLaConfiguration()
    {
        // FR-034 : la consultation est ouverte a tous. Interdire la lecture casserait le
        // monitoring en lecture seule de l'interface non elevee.
        ArrangeSecuredConfig();

        bool ran = RestrictedToken.TryRunWithoutAdministrator(() =>
        {
            Action act = () => File.ReadAllText(_configPath);

            act.Should().NotThrow("le monitoring en lecture seule est ouvert à tous");
        });

        ran.Should().BeTrue();
    }

    // -- L'administrateur, lui, peut écrire -----------------------------------

    [Fact]
    public void Administrateur_PeutToujoursEcrire()
    {
        // Verrouille l'autre sens : des ACL si strictes que le service lui-meme ne pourrait
        // plus ecrire rendraient le produit inutilisable.
        ArrangeSecuredConfig();

        Action act = () => File.WriteAllText(_configPath, """{"schemaVersion":1,"legitime":true}""");

        act.Should().NotThrow();
    }

    // -- Pose réelle des permissions sur le disque ----------------------------

    [Fact]
    public void EnsureSecured_PoseLesPermissionsEtLaPropriete()
    {
        ArrangeSecuredConfig();

        DirectorySecurity applied = new DirectoryInfo(_directory).GetAccessControl();

        applied.AreAccessRulesProtected.Should().BeTrue();
        ConfigAcl.IsOwnedByPrivilegedPrincipal(applied).Should().BeTrue();
        ConfigAcl.IsAlreadySecured(applied).Should().BeTrue();
    }

    [Fact]
    public void EnsureSecured_EstIdempotent()
    {
        // Appele a chaque demarrage : le second passage ne doit signaler aucune correction,
        // sans quoi le journal se remplirait d'avertissements sans objet.
        ArrangeSecuredConfig();

        ConfigAcl.AclCheckResult second = ConfigAcl.EnsureSecured(_directory);

        second.WasCorrect.Should().BeTrue();
        second.Diagnostic.Should().BeNull();
    }

    [Fact]
    public void RepertoireSquatteParUnUtilisateur_EstRepris()
    {
        // Le scenario d'attaque complet : l'utilisateur cree le repertoire AVANT le service,
        // en devient proprietaire, et conserve donc WRITE_DAC quelles que soient les ACE.
        // Le service doit reprendre la propriete, pas seulement reecrire les permissions.
        RestrictedToken.CurrentIsAdministrator().Should().BeTrue(
            "la suite d'intégration doit s'exécuter depuis une invite élevée");

        Directory.CreateDirectory(_directory);
        var info = new DirectoryInfo(_directory);
        DirectorySecurity squatted = info.GetAccessControl();
        squatted.SetOwner(WindowsIdentity.GetCurrent().User!);
        info.SetAccessControl(squatted);

        ConfigAcl.AclCheckResult result = ConfigAcl.EnsureSecured(_directory);

        result.WasCorrect.Should().BeFalse();

        // Asserté sur un champ structuré et non sur la prose du diagnostic : une assertion
        // sur une phrase française casse à la première reformulation — ou, comme ici, sur une
        // simple majuscule de début de phrase.
        result.OwnershipReclaimed.Should().BeTrue();
        result.Diagnostic.Should().NotBeNullOrWhiteSpace();

        ConfigAcl.IsOwnedByPrivilegedPrincipal(info.GetAccessControl()).Should().BeTrue();
    }

    [Fact]
    public void RelachementDesPermissions_EstCorrigeEtSignale()
    {
        ArrangeSecuredConfig();

        var info = new DirectoryInfo(_directory);
        DirectorySecurity relaxed = info.GetAccessControl();
        relaxed.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(relaxed);

        ConfigAcl.AclCheckResult result = ConfigAcl.EnsureSecured(_directory);

        result.WasCorrect.Should().BeFalse();
        result.Diagnostic.Should().NotBeNullOrWhiteSpace();

        // Un relâchement de permissions n'est PAS un squattage : la propriété était correcte,
        // seules les ACE avaient dérivé. Distinguer les deux permet de les journaliser à des
        // niveaux différents — une tentative de contournement n'a pas le même poids qu'un
        // outil de nettoyage trop zélé.
        result.OwnershipReclaimed.Should().BeFalse();

        ConfigAcl.IsAlreadySecured(info.GetAccessControl()).Should().BeTrue();
    }
}
