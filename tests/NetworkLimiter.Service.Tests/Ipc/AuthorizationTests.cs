using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Service.Ipc;

namespace NetworkLimiter.Service.Tests.Ipc;

/// <summary>
/// Autorisation par message — FR-034, FR-034a, contracts/ipc-protocol.md couche 2.
/// </summary>
/// <remarks>
/// <para>
/// La politique est séparée du mécanisme : <see cref="AuthorizationPolicy"/> décide,
/// <c>PipeCallerIdentity</c> constate l'élévation réelle de l'appelant. Cette séparation
/// existe pour que la <b>décision</b> soit testable exhaustivement sans dépendre d'un vrai
/// jeton Windows — la partie qui doit être juste sur tous les messages, pas seulement sur
/// ceux auxquels on aura pensé.
/// </para>
/// <para>
/// Sous UAC, le jeton filtré d'un administrateur non élevé ne porte pas le SID
/// Administrateurs activé. Le comportement exigé par FR-034 découle donc du modèle de
/// sécurité de Windows, sans logique d'autorisation maison.
/// </para>
/// </remarks>
public sealed class AuthorizationTests
{
    // -- Exhaustivité : aucun message d'écriture ne passe sans élévation ------

    [Fact]
    public void AucunMessageDEcriture_NEstAutoriseSansElevation()
    {
        // Parcourt la liste blanche complete plutot qu'une selection : un message ajoute
        // plus tard sera couvert automatiquement.
        foreach (string type in MessageTypes.WriteRequests)
        {
            AuthorizationPolicy.IsAuthorized(type, callerIsElevated: false)
                .Should().BeFalse($"« {type} » modifie l'état et exige l'élévation");
        }
    }

    [Fact]
    public void TousLesMessagesDEcriture_SontAutorisesAvecElevation()
    {
        foreach (string type in MessageTypes.WriteRequests)
        {
            AuthorizationPolicy.IsAuthorized(type, callerIsElevated: true)
                .Should().BeTrue($"« {type} » doit être accepté d'un appelant élevé");
        }
    }

    [Fact]
    public void TousLesMessagesDeLecture_SontAutorisesSansElevation()
    {
        // FR-034 : le monitoring est un outil de diagnostic sans donnee sensible, ouvert
        // a tout utilisateur connecte.
        foreach (string type in MessageTypes.ReadRequests)
        {
            AuthorizationPolicy.IsAuthorized(type, callerIsElevated: false)
                .Should().BeTrue($"« {type} » est une lecture, ouverte à tous");
        }
    }

    // -- Types inconnus -------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("Inconnu")]
    [InlineData("upsertrule")]
    [InlineData("UpsertRule ")]
    [InlineData("../GetState")]
    public void TypeInconnu_EstRefuseMemeAvecElevation(string type)
    {
        // Refuser un type inconnu meme a un appelant eleve evite qu'une faute de frappe
        // ou un message d'une version future soit traite par un chemin par defaut.
        AuthorizationPolicy.IsAuthorized(type, callerIsElevated: true).Should().BeFalse();
        AuthorizationPolicy.IsAuthorized(type, callerIsElevated: false).Should().BeFalse();
    }

    [Fact]
    public void TypeNul_EstRefuse()
    {
        AuthorizationPolicy.IsAuthorized(null, callerIsElevated: true).Should().BeFalse();
    }

    // -- Code d'erreur renvoyé ------------------------------------------------

    [Fact]
    public void RefusPourDefautDElevation_RenvoieElevationRequired()
    {
        // L'interface s'appuie sur ce code precis pour proposer la relance elevee
        // (FR-034a). Un code generique la laisserait sans action a proposer.
        AuthorizationPolicy.GetDenialCode(MessageTypes.UpsertRule, callerIsElevated: false)
            .Should().Be(ErrorCode.ElevationRequired);
    }

    [Fact]
    public void RefusPourTypeInconnu_RenvoieValidationFailed()
    {
        AuthorizationPolicy.GetDenialCode("Inconnu", callerIsElevated: true)
            .Should().Be(ErrorCode.ValidationFailed);
    }

    [Fact]
    public void RequeteAutorisee_NaPasDeCodeDeRefus()
    {
        AuthorizationPolicy.GetDenialCode(MessageTypes.GetState, callerIsElevated: false)
            .Should().BeNull();
    }

    // -- L'autorisation n'est jamais mémorisée --------------------------------

    [Fact]
    public void LaDecision_NeDependQueDesArgumentsFournis()
    {
        // Le contrat impose que le service n'enregistre JAMAIS qu'un appelant a ete
        // autorise une fois : chaque message est evalue seul. Une politique statique et
        // sans etat rend cette regle structurelle plutot que disciplinaire.
        AuthorizationPolicy.IsAuthorized(MessageTypes.UpsertRule, callerIsElevated: true).Should().BeTrue();
        AuthorizationPolicy.IsAuthorized(MessageTypes.UpsertRule, callerIsElevated: false).Should().BeFalse();
        AuthorizationPolicy.IsAuthorized(MessageTypes.UpsertRule, callerIsElevated: true).Should().BeTrue();
        AuthorizationPolicy.IsAuthorized(MessageTypes.UpsertRule, callerIsElevated: false).Should().BeFalse();
    }

    [Fact]
    public void TypeDeLaPolitique_EstStatiqueEtSansEtat()
    {
        Type policy = typeof(AuthorizationPolicy);

        policy.IsAbstract.Should().BeTrue();
        policy.IsSealed.Should().BeTrue();
        policy.GetFields(System.Reflection.BindingFlags.Instance |
                         System.Reflection.BindingFlags.NonPublic |
                         System.Reflection.BindingFlags.Public)
              .Should().BeEmpty("une politique d'autorisation avec de l'état finirait par mémoriser une décision");
    }
}
