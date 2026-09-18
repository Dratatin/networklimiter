using FluentAssertions;
using NetworkLimiter.App.ViewModels;
using NetworkLimiter.Contracts.Messages;
using Xunit;

namespace NetworkLimiter.App.Tests;

/// <summary>
/// Messages de mode dégradé (T120, FR-026).
/// </summary>
/// <remarks>
/// <para>
/// Ce qui est éprouvé ici n'est pas l'affichage mais le <b>diagnostic</b> : face à « aucune
/// limite ne s'applique », l'outil doit nommer la bonne cause et une seule. Quatre situations
/// sans rien de commun produisent le même symptôme — machine hors matrice, pilote absent,
/// limitation suspendue, application non lancée — et se tromper de cause envoie l'utilisateur
/// chercher pendant une heure au mauvais endroit.
/// </para>
/// <para>
/// Chaque constat doit porter une <b>action</b>. « Pilote non chargé » informe sans aider :
/// l'utilisateur n'a pas à connaître WinDivert pour se servir de cet outil.
/// </para>
/// </remarks>
public sealed class HealthViewModelTests
{
    [Fact]
    public void ServiceInjoignable_EstDitSansDetour()
    {
        var model = new HealthViewModel();

        // Etat initial : aucune reponse recue. Ne rien afficher laisserait croire que tout va
        // bien alors qu'absolument rien ne s'applique.
        model.Severity.Should().Be(HealthSeverity.Blocking);
        model.Summary.Should().Contain("injoignable");
        model.Findings[0].Action.Should().NotBeNull();
    }

    [Fact]
    public void MachineHorsMatrice_EclipseToutLeReste()
    {
        var model = new HealthViewModel
        {
            Health = Build(supported: false, driverLoaded: false, diagnostic: "ARM64 non pris en charge."),
        };

        // Parler de pilote a quelqu'un dont l'architecture n'a jamais eu de pilote disponible
        // l'enverrait en chercher un qui n'existe pas.
        model.Findings.Should().ContainSingle();
        model.Findings[0].Severity.Should().Be(HealthSeverity.Blocking);
        model.Findings[0].Action.Should().Contain("ARM64");
    }

    [Fact]
    public void PiloteAbsent_DonneUnePisteConcrete()
    {
        var model = new HealthViewModel { Health = Build(supported: true, driverLoaded: false) };

        model.Severity.Should().Be(HealthSeverity.Blocking);
        model.Summary.Should().Contain("pilote");

        // L'integrite de la memoire est la cause la plus frequente et la moins devinable :
        // la nommer evite une reinstallation inutile.
        model.Findings[0].Action.Should().Contain("intégrité de la mémoire");
    }

    [Fact]
    public void PiloteAbsentAvecRaisonPrecise_PrefereLaRaisonReelle()
    {
        var model = new HealthViewModel
        {
            Health = Build(supported: true, driverLoaded: false, degraded: "Erreur 577 : signature refusée."),
        };

        // Une cause constatee vaut toujours mieux qu'un conseil generique.
        model.Findings[0].Action.Should().Contain("577");
    }

    [Fact]
    public void Suspension_EstUnAvertissement_PasUnePanne()
    {
        var model = new HealthViewModel
        {
            Health = Build(supported: true, driverLoaded: true, suspended: true),
        };

        // C'est un choix de l'utilisateur. L'afficher comme une panne lui ferait chercher un
        // probleme qu'il a lui-meme provoque.
        model.Severity.Should().Be(HealthSeverity.Warning);
        model.Summary.Should().Contain("suspendue");
        model.Findings[0].Action.Should().Contain("conservées");
    }

    [Fact]
    public void ToutVaBien_LeDitExplicitement()
    {
        var model = new HealthViewModel
        {
            Health = Build(supported: true, driverLoaded: true, interceptionActive: true, activeRules: 3),
        };

        model.IsHealthy.Should().BeTrue();
        model.Summary.Should().Contain("3");

        // Aucune action a proposer quand il n'y a rien a faire : inventer un conseil ferait
        // douter d'un etat sain.
        model.Findings[0].Action.Should().BeNull();
    }

    [Fact]
    public void AucuneRegle_EstDistingueDeAucuneRegleAppliquee()
    {
        var model = new HealthViewModel
        {
            Health = Build(supported: true, driverLoaded: true, interceptionActive: true, activeRules: 0),
        };

        model.IsHealthy.Should().BeTrue();
        model.Summary.Should().Contain("Aucune règle n'est définie");
    }

    [Fact]
    public void ReglesInactives_SontSignalees_SansMasquerLeResteDeLEtat()
    {
        var model = new HealthViewModel
        {
            Health = Build(
                supported: true, driverLoaded: true, interceptionActive: true,
                activeRules: 1, inactiveRules: 2),
        };

        model.Severity.Should().Be(HealthSeverity.Warning);
        model.Findings.Should().Contain(finding => finding.Title.Contains('2', StringComparison.Ordinal));
    }

    [Fact]
    public void AvertissementVpn_EstRepris_AvecUneActionMesuree()
    {
        var model = new HealthViewModel
        {
            Health = Build(
                supported: true, driverLoaded: true, interceptionActive: true,
                vpnWarning: "Un VPN semble actif (Mon VPN)."),
        };

        model.Severity.Should().Be(HealthSeverity.Warning);

        HealthFinding finding = model.Findings.Single(item => item.Title.Contains("VPN", StringComparison.Ordinal));

        // L'action reste mesuree : la detection est heuristique, on invite a verifier, pas a
        // desinstaller quoi que ce soit.
        finding.Action.Should().Contain("Vérifiez");
    }

    [Fact]
    public void LesConstats_SontOrdonnes_DuPlusEnglobantAuPlusLocal()
    {
        var model = new HealthViewModel
        {
            Health = Build(
                supported: true, driverLoaded: true, interceptionActive: true,
                inactiveRules: 1, vpnWarning: "Un VPN semble actif."),
        };

        // Le VPN precede les regles inactives : il peut EXPLIQUER pourquoi elles le sont.
        model.Findings[0].Title.Should().Contain("VPN");
    }

    private static HealthResultPayload Build(
        bool supported,
        bool driverLoaded,
        bool interceptionActive = false,
        bool suspended = false,
        int activeRules = 0,
        int inactiveRules = 0,
        string? degraded = null,
        string? diagnostic = null,
        string? vpnWarning = null) => new()
        {
            InterceptionActive = interceptionActive,
            DriverLoaded = driverLoaded,
            Suspended = suspended,
            ActiveRuleCount = activeRules,
            InactiveRuleCount = inactiveRules,
            QueuePressure = 0,
            VpnDetected = vpnWarning is not null,
            VpnWarning = vpnWarning,
            ConfigWarning = null,
            DegradedReason = degraded,
            Compatibility = new CompatibilityDto
            {
                Supported = supported,
                OsBuild = 26200,
                Architecture = "X64",
                Diagnostic = diagnostic,
            },
            ServiceVersion = "1.0.0",
        };
}
