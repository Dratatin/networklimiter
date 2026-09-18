using FluentAssertions;
using NetworkLimiter.App.ViewModels;
using Xunit;

namespace NetworkLimiter.App.Tests;

/// <summary>
/// Aspect de l'icône de zone de notification (T121).
/// </summary>
/// <remarks>
/// <para>
/// La fenêtre reste fermée la plupart du temps : cette icône est souvent la <b>seule</b> chose
/// que l'utilisateur voit de l'outil. Le défaut à exclure absolument est qu'elle laisse croire
/// que tout va bien alors que rien ne s'applique.
/// </para>
/// <para>
/// Suspension et panne partagent un symptôme — aucune limite — mais pas une couleur : l'une est
/// un choix, l'autre un problème. Les confondre ferait chercher un incident à quelqu'un qui a
/// lui-même suspendu, ou l'inverse, bien plus grave.
/// </para>
/// </remarks>
public sealed class TrayPresentationTests
{
    [Fact]
    public void ServiceInjoignable_NeRessembleJamaisAUnEtatSain()
    {
        TrayPresentation tray = TrayPresentation.Compose(
            connected: false, suspended: false, interceptionActive: false, activeRuleCount: 0);

        tray.Appearance.Should().Be(TrayAppearance.Impaired);
        tray.Tooltip.Should().Contain("injoignable");

        // Proposer de suspendre ce qui ne tourne pas ferait cliquer dans le vide.
        tray.CanToggle.Should().BeFalse();
    }

    [Fact]
    public void Suspension_SeDistingueVisuellementDUnePanne()
    {
        TrayPresentation suspended = TrayPresentation.Compose(
            connected: true, suspended: true, interceptionActive: false, activeRuleCount: 2);

        TrayPresentation impaired = TrayPresentation.Compose(
            connected: true, suspended: false, interceptionActive: false, activeRuleCount: 2);

        suspended.Appearance.Should().Be(TrayAppearance.Suspended);
        impaired.Appearance.Should().Be(TrayAppearance.Impaired);
        suspended.Appearance.Should().NotBe(impaired.Appearance);
    }

    [Fact]
    public void Suspension_ProposeLaReprise_PasLaSuspension()
    {
        TrayPresentation tray = TrayPresentation.Compose(
            connected: true, suspended: true, interceptionActive: false, activeRuleCount: 1);

        // Le libelle decrit l'ACTION : « Suspendre » sur un etat deja suspendu serait un piege.
        tray.ToggleLabel.Should().Contain("Réappliquer");
        tray.CanToggle.Should().BeTrue();
    }

    [Fact]
    public void EtatSain_AnnonceLeNombreDeLimites()
    {
        TrayPresentation tray = TrayPresentation.Compose(
            connected: true, suspended: false, interceptionActive: true, activeRuleCount: 3);

        tray.Appearance.Should().Be(TrayAppearance.Limiting);
        tray.Tooltip.Should().Contain("3");
        tray.ToggleLabel.Should().Contain("Suspendre");
    }

    [Fact]
    public void AucuneRegle_NEstPasUnePanne()
    {
        TrayPresentation tray = TrayPresentation.Compose(
            connected: true, suspended: false, interceptionActive: true, activeRuleCount: 0);

        // Ne rien avoir a limiter est un etat parfaitement normal.
        tray.Appearance.Should().Be(TrayAppearance.Limiting);
        tray.Tooltip.Should().Contain("aucune limite");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9999)]
    public void LInfobulle_RespecteLaLimiteDeWindows(int ruleCount)
    {
        TrayPresentation tray = TrayPresentation.Compose(
            connected: true, suspended: false, interceptionActive: true, activeRuleCount: ruleCount);

        // Windows tronque silencieusement au-dela de 64 caracteres, et c'est la FIN du texte
        // qui disparait — donc l'information.
        tray.Tooltip.Length.Should().BeLessThanOrEqualTo(TrayPresentation.MaxTooltipLength);
    }

    [Fact]
    public void DeuxEtatsIdentiques_SontEgaux()
    {
        // L'egalite sert a ne PAS rafraichir l'icone quand rien n'a change : la reaffecter la
        // fait clignoter dans la zone de notification, dix fois par minute, pour rien.
        TrayPresentation first = TrayPresentation.Compose(true, false, true, 2);
        TrayPresentation second = TrayPresentation.Compose(true, false, true, 2);

        first.Should().Be(second);
    }
}
