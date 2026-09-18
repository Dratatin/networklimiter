using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using NetworkLimiter.Service.Ipc;

namespace NetworkLimiter.Service.Tests.Ipc;

/// <summary>
/// ACL du tuyau nommé — contracts/ipc-protocol.md, « Contrôle d'accès, couche 1 ».
/// </summary>
/// <remarks>
/// <para>
/// Un tuyau nommé Windows est <b>joignable à distance</b> par SMB, sous la forme
/// <c>\\machine\pipe\nom</c>. Sans refus explicite du SID <c>NETWORK</c>, le canal de contrôle
/// d'un service <c>LocalSystem</c> serait exposé au réseau. C'est le détail qui sépare une
/// conception correcte d'une conception sûre, et il ne s'obtient pas par défaut.
/// </para>
/// <para>
/// Ces tests lisent la DACL réelle produite par le code, pas une intention déclarée.
/// </para>
/// </remarks>
public sealed class PipeSecurityTests
{
    private static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkSid, null);
    private static readonly SecurityIdentifier Anonymous = new(WellKnownSidType.AnonymousSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private static PipeSecurity Create() => PipeSecurityFactory.Create();

    private static List<PipeAccessRule> RulesFor(SecurityIdentifier sid) =>
        Create().GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                .Cast<PipeAccessRule>()
                .Where(rule => rule.IdentityReference.Equals(sid))
                .ToList();

    // -- Refus explicites -----------------------------------------------------

    [Fact]
    public void SidNetwork_EstRefuseExplicitement()
    {
        List<PipeAccessRule> rules = RulesFor(Network);

        rules.Should().NotBeEmpty("un tuyau nommé est joignable à distance par SMB");
        rules.Should().OnlyContain(rule => rule.AccessControlType == AccessControlType.Deny);
        rules.Should().Contain(rule => rule.PipeAccessRights.HasFlag(PipeAccessRights.FullControl));
    }

    [Fact]
    public void SidAnonyme_EstRefuseExplicitement()
    {
        List<PipeAccessRule> rules = RulesFor(Anonymous);

        rules.Should().NotBeEmpty();
        rules.Should().OnlyContain(rule => rule.AccessControlType == AccessControlType.Deny);
    }

    [Fact]
    public void LesRefusPrecedentLesAutorisations()
    {
        // Une ACE de refus placee apres une ACE d'autorisation ne sert a rien : Windows
        // evalue la DACL dans l'ordre et s'arrete au premier verdict. L'ordre canonique
        // est donc une exigence fonctionnelle, pas une convention de style.
        string sddl = Create().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        int firstDeny = sddl.IndexOf("(D;", StringComparison.Ordinal);
        int firstAllow = sddl.IndexOf("(A;", StringComparison.Ordinal);

        firstDeny.Should().BeGreaterThanOrEqualTo(0, "la DACL doit porter au moins un refus");
        firstAllow.Should().BeGreaterThanOrEqualTo(0, "la DACL doit porter au moins une autorisation");
        firstDeny.Should().BeLessThan(firstAllow);
    }

    // -- Autorisations --------------------------------------------------------

    [Fact]
    public void LocalSystem_ADroitDeControleTotal()
    {
        List<PipeAccessRule> rules = RulesFor(LocalSystem);

        rules.Should().Contain(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            rule.PipeAccessRights.HasFlag(PipeAccessRights.FullControl));
    }

    [Fact]
    public void UtilisateursLocaux_PeuventLireEtEcrire()
    {
        // La couche 1 laisse passer tout utilisateur local : le filtrage lecture/ecriture
        // se fait a la couche 2, par impersonation. Refuser ici empecherait le monitoring
        // en lecture seule, que FR-034 ouvre a tous.
        List<PipeAccessRule> rules = RulesFor(Users);

        rules.Should().Contain(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            rule.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));
    }

    [Fact]
    public void UtilisateursLocaux_NOntPasLeControleTotal()
    {
        // Le controle total permettrait de modifier l'ACL du tuyau, donc de lever
        // soi-meme la restriction. Ce serait une elevation de privileges par conception.
        List<PipeAccessRule> rules = RulesFor(Users);

        rules.Should().NotContain(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            rule.PipeAccessRights.HasFlag(PipeAccessRights.ChangePermissions));
    }

    // -- Nom du tuyau ---------------------------------------------------------

    [Fact]
    public void NomDuTuyau_PorteLaVersionDuProtocole()
    {
        // Le nom porte la version : deux versions incompatibles ne se rencontrent pas
        // sur le meme canal, ce qui evite tout dialogue de sourds.
        PipeServer.PipeName.Should().Be("NetworkLimiter.v1");
    }

    [Fact]
    public void NombreMaximalDInstances_EstBorne()
    {
        // Sans borne, un utilisateur local ouvrirait autant de connexions qu'il veut et
        // epuiserait les ressources du service.
        PipeServer.MaxServerInstances.Should().Be(4);
    }
}
