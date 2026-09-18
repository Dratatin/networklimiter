using FluentAssertions;
using NetworkLimiter.App.ViewModels;
using NetworkLimiter.Contracts.Messages;
using Xunit;

namespace NetworkLimiter.App.Tests;

/// <summary>
/// Vérifie que les listes se rafraîchissent sans maltraiter l'utilisateur.
/// </summary>
/// <remarks>
/// L'interface redemande l'état toutes les dix secondes, et à chaque notification. Si un
/// rafraîchissement reconstruisait ses collections, la sélection disparaîtrait sous le curseur —
/// à un moment que l'utilisateur ne contrôle pas, et sans qu'il comprenne pourquoi son clic
/// vient d'atterrir ailleurs. C'est le genre de défaut qu'aucune relecture de code ne détecte
/// et que dix secondes d'usage rendent insupportable.
/// </remarks>
public sealed class ListMergeTests
{
    [Fact]
    public void LaListeDApplications_ConserveLaSelectionEntreDeuxRafraichissements()
    {
        var list = new AppListViewModel();

        list.Merge([App(@"c:\a\a.exe"), App(@"c:\b\b.exe")]);
        list.Selected = list.Applications.First(app => app.ExecutablePath == @"c:\b\b.exe");

        list.Merge([App(@"c:\a\a.exe"), App(@"c:\b\b.exe"), App(@"c:\c\c.exe")]);

        list.Selected.Should().NotBeNull();
        list.Selected!.ExecutablePath.Should().Be(@"c:\b\b.exe");
    }

    [Fact]
    public void LaListeDApplications_ConserveLesInstancesExistantes()
    {
        var list = new AppListViewModel();
        list.Merge([App(@"c:\a\a.exe")]);

        ObservedAppViewModel first = list.Applications[0];

        list.Merge([App(@"c:\a\a.exe", alreadyRuled: true)]);

        // Meme instance, propriete mise a jour : c'est ce qui permet a la liaison WPF de
        // rafraichir la ligne sans que la ListBox recree ses conteneurs.
        list.Applications[0].Should().BeSameAs(first);
        list.Applications[0].AlreadyRuled.Should().BeTrue();
    }

    [Fact]
    public void LaListeDApplications_RetireCeQuiADisparu()
    {
        var list = new AppListViewModel();
        list.Merge([App(@"c:\a\a.exe"), App(@"c:\b\b.exe")]);

        list.Merge([App(@"c:\b\b.exe")]);

        list.Applications.Should().ContainSingle()
            .Which.ExecutablePath.Should().Be(@"c:\b\b.exe");
    }

    [Fact]
    public void LaListeDeRegles_ConserveLaSelectionEtMetAJourLaLigne()
    {
        var list = new RuleListViewModel();
        Guid id = Guid.NewGuid();

        list.Merge([Rule(id, download: 204_800, applied: true)]);
        list.Selected = list.Rules[0];

        RuleRowViewModel row = list.Rules[0];

        list.Merge([Rule(id, download: 1_048_576, applied: false)]);

        list.Selected.Should().BeSameAs(row);
        list.Rules[0].Should().BeSameAs(row);
        row.IsApplied.Should().BeFalse();
        row.Download.Should().Contain("1 Mo/s");
    }

    [Fact]
    public void UneRegleInactive_ExpliqueEnFrancaisCeQuIlFaudraitFaire()
    {
        var list = new RuleListViewModel();

        list.Merge([Rule(Guid.NewGuid(), 204_800, applied: false, reason: RuleInactiveReasonDto.ApplicationNotRunning)]);

        // FR-026 : afficher « ApplicationNotRunning » reviendrait a ne rien expliquer.
        list.Rules[0].InactiveExplanation.Should().Contain("n'est pas lancée");
    }

    [Fact]
    public void UneRaisonInconnue_EstAffichéeTelleQuelle_PlutotQueMasquee()
    {
        var list = new RuleListViewModel();

        list.Merge([Rule(Guid.NewGuid(), 204_800, applied: false, reason: (RuleInactiveReasonDto)999)]);

        // Un service plus recent peut rendre une raison que cette interface ne connait pas.
        // La taire laisserait une regle inactive sans aucune explication, ce qui est pire
        // qu'un libelle brut.
        list.Rules[0].InactiveExplanation.Should().Be("999");
    }

    private static ObservedAppDto App(string path, bool alreadyRuled = false) => new()
    {
        ExecutablePath = path,
        ExecutableName = Path.GetFileName(path),
        AlreadyRuled = alreadyRuled,
    };

    private static RuleStateDto Rule(Guid id, long download, bool applied, RuleInactiveReasonDto? reason = null) => new()
    {
        Rule = new RuleDto
        {
            Id = id,
            Target = new AppIdentityDto
            {
                ExecutablePath = @"c:\jeux\jeu.exe",
                ExecutableName = "jeu.exe",
                DisplayName = "Jeu",
            },
            DownloadBytesPerSecond = download,
            UploadBytesPerSecond = null,
            Enabled = true,
            ExemptFromGlobal = false,
        },
        Status = applied ? RuleApplicationStatus.Active : RuleApplicationStatus.Inactive,
        InactiveReason = applied ? null : reason ?? RuleInactiveReasonDto.ApplicationNotRunning,
        MatchedProcessCount = applied ? 1 : 0,
    };
}
