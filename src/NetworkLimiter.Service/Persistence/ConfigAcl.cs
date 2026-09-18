using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NetworkLimiter.Service.Persistence;

/// <summary>Le répertoire de données ne peut pas être sécurisé.</summary>
/// <remarks>
/// Volontairement fatale pour la mise en forme : le service passe en mode dégradé sans
/// appliquer de limite. Mieux vaut ne pas limiter que limiter derrière un verrou qu'on sait
/// contournable — l'utilisateur croirait sa configuration protégée alors qu'elle ne l'est pas.
/// </remarks>
public sealed class ConfigNotSecurableException : Exception
{
    /// <inheritdoc cref="Exception(string, Exception)" />
    public ConfigNotSecurableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <inheritdoc cref="Exception(string)" />
    public ConfigNotSecurableException(string message) : base(message)
    {
    }

    /// <summary>Crée l'exception sans détail.</summary>
    public ConfigNotSecurableException()
    {
    }
}

/// <summary>
/// Pose et vérifie les permissions du répertoire de données.
/// </summary>
/// <remarks>
/// <para>
/// Ces ACL sont un <b>contrôle de sécurité</b>, pas une commodité de rangement. Elles sont le
/// verrou réel de FR-034 : si un utilisateur standard pouvait écrire ici, il lèverait ses
/// propres limites avec un éditeur de texte, et toute l'autorisation IPC ne serait qu'un
/// verrou d'interface — contournable en quelques secondes.
/// </para>
/// <para>
/// Trois éléments concourent au verrou, et il en manque un si l'on n'y pense pas : l'héritage
/// désactivé, des ACE explicites, et la <b>propriété</b> du répertoire. Le propriétaire d'un
/// objet Windows conserve <c>WRITE_DAC</c> quoi qu'il arrive : un répertoire créé par un
/// utilisateur standard avant la première exécution du service lui reste ouvert, quelles que
/// soient les permissions posées ensuite.
/// </para>
/// <para>
/// La vérification est refaite à <b>chaque démarrage</b> : une permission élargie par un tiers
/// — outil de nettoyage, script d'entreprise, manipulation manuelle — ne doit pas passer
/// inaperçue.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ConfigAcl
{
    /// <summary>Résultat d'une vérification d'ACL.</summary>
    /// <param name="WasCorrect">Les permissions étaient déjà conformes.</param>
    /// <param name="OwnershipReclaimed">
    /// Le répertoire appartenait à un compte non privilégié et sa propriété a été reprise.
    /// </param>
    /// <param name="Diagnostic">Description de la correction appliquée, le cas échéant.</param>
    /// <remarks>
    /// <paramref name="OwnershipReclaimed"/> est un champ distinct et non une nuance du
    /// message : un squattage de répertoire n'est pas un simple relâchement de permissions,
    /// c'est une tentative de contournement, et l'appelant doit pouvoir la journaliser à un
    /// niveau différent sans analyser une phrase en français.
    /// </remarks>
    public sealed record AclCheckResult(bool WasCorrect, bool OwnershipReclaimed, string? Diagnostic);

    /// <summary>
    /// Applique les permissions attendues au répertoire, en corrigeant ce qui diverge.
    /// </summary>
    /// <returns>Le constat, à journaliser si une correction a eu lieu.</returns>
    public static AclCheckResult EnsureSecured(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        Directory.CreateDirectory(directoryPath);

        var directory = new DirectoryInfo(directoryPath);
        DirectorySecurity security = directory.GetAccessControl();

        if (IsAlreadySecured(security))
        {
            return new AclCheckResult(WasCorrect: true, OwnershipReclaimed: false, Diagnostic: null);
        }

        bool ownerWasWrong = !IsOwnedByPrivilegedPrincipal(security);

        try
        {
            // BuildSecurity pose deja la propriete : un repertoire cree par un utilisateur
            // standard avant la premiere execution du service lui appartient, et la propriete
            // emporte WRITE_DAC — sans reprise, il pourrait defaire les restrictions qu'on
            // vient de poser.
            directory.SetAccessControl(BuildSecurity());
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
        {
            // Echouer bruyamment plutot que de laisser croire la configuration protegee. Le
            // service passe alors en mode degrade, sans appliquer de limite : mieux vaut ne
            // pas limiter que limiter avec un verrou qu'on sait contournable.
            throw new ConfigNotSecurableException(
                $"Impossible de sécuriser « {directoryPath} » : {exception.Message} " +
                "Le service doit s'exécuter avec des privilèges suffisants pour poser les " +
                "permissions du répertoire de données.",
                exception);
        }

        string diagnostic = ownerWasWrong
            ? $"Le répertoire « {directoryPath} » appartenait à un compte non privilégié, ce qui " +
              "permettait d'en réécrire les permissions. Propriété et permissions ont été rétablies."
            : $"Les permissions de « {directoryPath} » n'étaient pas conformes et ont été " +
              "rétablies : seuls SYSTEM et les administrateurs peuvent écrire.";

        return new AclCheckResult(WasCorrect: false, OwnershipReclaimed: ownerWasWrong, Diagnostic: diagnostic);
    }

    /// <summary>Construit les permissions attendues.</summary>
    public static DirectorySecurity BuildSecurity()
    {
        var security = new DirectorySecurity();

        // Heritage desactive : sans cela, le repertoire heriterait des permissions de
        // %ProgramData%, qui autorisent les utilisateurs a creer des fichiers — de quoi
        // contourner le verrou par remplacement.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        const InheritanceFlags Inheritance =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, Inheritance, PropagationFlags.None, AccessControlType.Allow));

        security.AddAccessRule(new FileSystemAccessRule(
            administrators, FileSystemRights.FullControl, Inheritance, PropagationFlags.None, AccessControlType.Allow));

        // Lecture seule pour les utilisateurs : l'interface non elevee doit pouvoir lire la
        // configuration pour l'afficher (FR-034), mais jamais la modifier.
        security.AddAccessRule(new FileSystemAccessRule(
            users, FileSystemRights.ReadAndExecute, Inheritance, PropagationFlags.None, AccessControlType.Allow));

        // La propriete fait partie integrante de la securite attendue, pas d'un reglage a
        // part : le proprietaire conserve WRITE_DAC et peut donc defaire toutes les ACE
        // ci-dessus. La poser ici garantit qu'aucun appelant ne l'oublie.
        security.SetOwner(administrators);

        return security;
    }

    /// <summary>
    /// Indique si le propriétaire d'un répertoire est un principal privilégié.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Vérification indispensable, et facile à oublier : le <b>propriétaire</b> d'un objet
    /// Windows conserve implicitement <c>WRITE_DAC</c>, c'est-à-dire le droit de réécrire
    /// l'ACL — quelles que soient les ACE posées.
    /// </para>
    /// <para>
    /// Conséquence concrète : si un utilisateur standard crée
    /// <c>%ProgramData%\NetworkLimiter</c> <i>avant</i> la première exécution du service, il en
    /// devient propriétaire, et toutes les restrictions posées ensuite lui restent
    /// contournables. C'est un cas classique de squattage de répertoire, et le verrou de
    /// FR-034 y tomberait sans qu'aucune ACE ne paraisse anormale.
    /// </para>
    /// </remarks>
    public static bool IsOwnedByPrivilegedPrincipal(DirectorySecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);

        IdentityReference? owner = security.GetOwner(typeof(SecurityIdentifier));
        if (owner is not SecurityIdentifier ownerSid)
        {
            return false;
        }

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var trustedInstaller = new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

        return ownerSid.Equals(system) ||
               ownerSid.Equals(administrators) ||
               ownerSid.Equals(trustedInstaller);
    }

    /// <summary>Indique si des permissions données sont conformes à l'attendu.</summary>
    public static bool IsAlreadySecured(DirectorySecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);

        if (!security.AreAccessRulesProtected)
        {
            return false;
        }

        if (!IsOwnedByPrivilegedPrincipal(security))
        {
            return false;
        }

        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        const FileSystemRights ForbiddenForUsers =
            FileSystemRights.WriteData |
            FileSystemRights.AppendData |
            FileSystemRights.Delete |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;

        foreach (FileSystemAccessRule rule in security
                     .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                     .Cast<FileSystemAccessRule>())
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            // Toute autorisation d'ecriture accordee aux utilisateurs — directement ou via un
            // groupe qui les contient — rend le verrou inoperant.
            if (rule.IdentityReference.Equals(users) && (rule.FileSystemRights & ForbiddenForUsers) != 0)
            {
                return false;
            }
        }

        return true;
    }
}
