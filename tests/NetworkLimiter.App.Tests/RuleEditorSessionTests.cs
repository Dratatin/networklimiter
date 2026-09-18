using FluentAssertions;
using NetworkLimiter.App.ViewModels;
using NetworkLimiter.Contracts.Messages;
using Xunit;

namespace NetworkLimiter.App.Tests;

/// <summary>
/// Vérifie l'éditeur de règle (T073).
/// </summary>
/// <remarks>
/// Le point de vigilance n'est pas « la saisie valide produit-elle la bonne règle » — c'est
/// « qu'est-ce que l'éditeur refuse ». Une interface qui accepte une saisie que le service
/// rejettera, ou pire une saisie que le service acceptera sans qu'elle limite quoi que ce soit,
/// fait croire à l'utilisateur qu'il a posé une limite.
/// </remarks>
public sealed class RuleEditorSessionTests
{
    [Fact]
    public void UneRegleSansAucunPlafond_EstRefusee_AvecUneExplication()
    {
        RuleEditorSessionViewModel session = RuleEditorSessionViewModel.Create(SomeApp);

        // Les deux sens sont « illimité » par défaut : c'est exactement l'état dans lequel
        // l'éditeur s'ouvre, donc celui qu'il faut refuser en premier.
        session.CanSave.Should().BeFalse();
        session.BlockingMessage.Should().Contain("au moins un plafond");
    }

    [Fact]
    public void UnPlafondSousLeMinimum_EstRefuseALaSaisie()
    {
        RuleEditorSessionViewModel session = RuleEditorSessionViewModel.Create(SomeApp);

        session.Download.Unlimited = false;
        session.Download.Unit = RateUnit.KilobytesPerSecond;
        session.Download.Text = "1";

        session.CanSave.Should().BeFalse();

        // FR-007 : les bornes sont rappelées PENDANT la frappe, pas après validation.
        session.BlockingMessage.Should().Contain("10");
    }

    [Fact]
    public void UnPlafondValide_ProduitUneRegleEnregistrable()
    {
        RuleEditorSessionViewModel session = RuleEditorSessionViewModel.Create(SomeApp);

        session.Download.Unlimited = false;
        session.Download.Unit = RateUnit.KilobytesPerSecond;
        session.Download.Text = "200";

        session.CanSave.Should().BeTrue();

        UpsertRulePayload payload = session.ToPayload();

        payload.RuleId.Should().BeNull("il s'agit d'une création");
        payload.Rule.DownloadBytesPerSecond.Should().Be(204_800);
        payload.Rule.UploadBytesPerSecond.Should().BeNull();
        payload.Rule.Target.ExecutablePath.Should().Be(SomeApp.ExecutablePath);
    }

    [Fact]
    public void LEditionDUneRegleExistante_ConserveSonIdentifiant()
    {
        var rule = new RuleDto
        {
            Id = Guid.NewGuid(),
            Target = new AppIdentityDto
            {
                ExecutablePath = @"c:\jeux\jeu.exe",
                ExecutableName = "jeu.exe",
                DisplayName = "Jeu",
            },
            DownloadBytesPerSecond = 1_048_576,
            UploadBytesPerSecond = null,
            Enabled = true,
            ExemptFromGlobal = false,
        };

        RuleEditorSessionViewModel session = RuleEditorSessionViewModel.Edit(rule);

        // Perdre l'identifiant creerait une SECONDE regle visant la meme application, que le
        // service refuserait pour conflit — apres que l'utilisateur a tout ressaisi.
        session.RuleId.Should().Be(rule.Id);
        session.ToPayload().Rule.Id.Should().Be(rule.Id);

        // La saisie doit refleter la regle ouverte, pas repartir a vide.
        session.Download.Unlimited.Should().BeFalse();
        session.Download.BytesPerSecond.Should().Be(1_048_576);
    }

    [Fact]
    public void UneSaisieInvalidePuisCorrigee_RedevientEnregistrable()
    {
        RuleEditorSessionViewModel session = RuleEditorSessionViewModel.Create(SomeApp);

        session.Upload.Unlimited = false;
        session.Upload.Text = "n'importe quoi";
        session.CanSave.Should().BeFalse();

        session.Upload.Text = "2";

        // L'etat de blocage doit se LEVER : un editeur qui reste bloque apres correction
        // oblige a tout recommencer, et c'est la que les gens abandonnent.
        session.CanSave.Should().BeTrue();
        session.BlockingMessage.Should().BeNull();
    }

    [Fact]
    public void ToPayload_LeveSiLaSaisieNEstPasEnregistrable()
    {
        RuleEditorSessionViewModel session = RuleEditorSessionViewModel.Create(SomeApp);

        // Garde-fou : meme si une vue appelait ToPayload sans verifier CanSave, aucune regle
        // vide ne doit partir vers le service.
        Action act = () => session.ToPayload();

        act.Should().Throw<InvalidOperationException>();
    }

    private static ObservedAppDto SomeApp => new()
    {
        ExecutablePath = @"c:\jeux\jeu.exe",
        ExecutableName = "jeu.exe",
        AlreadyRuled = false,
    };
}
