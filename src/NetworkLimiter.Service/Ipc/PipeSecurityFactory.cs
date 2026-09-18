using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NetworkLimiter.Service.Ipc;

/// <summary>
/// Construit la DACL du tuyau nommé de contrôle.
/// </summary>
/// <remarks>
/// <para>
/// Première des trois couches de contrôle d'accès du contrat IPC. Elle doit tenir seule :
/// les deux autres — autorisation par impersonation et validation du contenu — la complètent
/// sans la remplacer.
/// </para>
/// <para>
/// Le point décisif est le <b>refus explicite du SID <c>NETWORK</c></b>. Un tuyau nommé
/// Windows est joignable à distance par SMB sous la forme <c>\\machine\pipe\nom</c> ; sans
/// cette ACE, le canal de contrôle d'un service <c>LocalSystem</c> serait exposé au réseau.
/// Ce n'est pas le comportement par défaut, il faut le demander.
/// </para>
/// </remarks>
public static class PipeSecurityFactory
{
    /// <summary>
    /// Droits accordés aux utilisateurs locaux : lire, écrire, et se connecter.
    /// </summary>
    /// <remarks>
    /// Volontairement dépourvu de <see cref="PipeAccessRights.ChangePermissions"/> et de
    /// <see cref="PipeAccessRights.TakeOwnership"/> : les accorder permettrait à un
    /// utilisateur de réécrire cette DACL, donc de lever lui-même la restriction. Ce serait
    /// une élévation de privilèges par conception.
    /// </remarks>
    private const PipeAccessRights LocalUserRights =
        PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;

    /// <summary>Construit la sécurité à appliquer au tuyau.</summary>
    public static PipeSecurity Create()
    {
        var security = new PipeSecurity();

        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var anonymous = new SecurityIdentifier(WellKnownSidType.AnonymousSid, null);
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        // Refus d'abord. Windows evalue la DACL dans l'ordre et s'arrete au premier verdict :
        // une ACE de refus placee apres une autorisation ne servirait a rien. .NET remet la
        // liste en ordre canonique, et un test verifie que l'ordre obtenu est bien celui-la.
        security.AddAccessRule(new PipeAccessRule(
            network, PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            anonymous, PipeAccessRights.FullControl, AccessControlType.Deny));

        security.AddAccessRule(new PipeAccessRule(
            localSystem, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            administrators, PipeAccessRights.FullControl, AccessControlType.Allow));

        // Tout utilisateur local peut se connecter : le monitoring en lecture seule est
        // ouvert a tous (FR-034). Le filtrage lecture/ecriture se fait a la couche 2, par
        // impersonation de l'appelant.
        security.AddAccessRule(new PipeAccessRule(
            users, LocalUserRights, AccessControlType.Allow));

        return security;
    }
}
