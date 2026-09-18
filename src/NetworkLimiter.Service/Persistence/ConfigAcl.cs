using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NetworkLimiter.Service.Persistence;

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
/// L'héritage est désactivé et les ACE sont explicites. Un répertoire qui hériterait des
/// permissions de <c>%ProgramData%</c> laisserait les utilisateurs créer des fichiers, ce qui
/// suffirait à contourner le verrou par remplacement.
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
    /// <param name="Diagnostic">Description de la correction appliquée, le cas échéant.</param>
    public sealed record AclCheckResult(bool WasCorrect, string? Diagnostic);

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

        bool wasCorrect = IsAlreadySecured(security);

        if (wasCorrect)
        {
            return new AclCheckResult(WasCorrect: true, Diagnostic: null);
        }

        directory.SetAccessControl(BuildSecurity());

        return new AclCheckResult(
            WasCorrect: false,
            Diagnostic: $"Les permissions de « {directoryPath} » n'étaient pas conformes et ont été " +
                        "rétablies : seuls SYSTEM et les administrateurs peuvent écrire.");
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

        return security;
    }

    /// <summary>Indique si des permissions données sont conformes à l'attendu.</summary>
    public static bool IsAlreadySecured(DirectorySecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);

        if (!security.AreAccessRulesProtected)
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
