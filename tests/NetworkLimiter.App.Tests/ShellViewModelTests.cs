using NetworkLimiter.App.Elevation;
using NetworkLimiter.App.Ipc;
using NetworkLimiter.App.ViewModels;

namespace NetworkLimiter.App.Tests;

/// <summary>
/// État de la fenêtre et disponibilité des commandes — FR-034a, FR-034b, FR-034c.
/// </summary>
/// <remarks>
/// <para>
/// FR-034b exige que les commandes de modification soient <b>visibles mais désactivées</b>,
/// avec la raison affichée. Masquer une commande fait croire qu'elle n'existe pas ; la laisser
/// active pour échouer après saisie fait perdre son travail à l'utilisateur.
/// </para>
/// <para>
/// Ces tests couvrent exhaustivement les combinaisons connexion × élévation. C'est là que
/// naissent les interfaces qui mentent sur leur état — celles qui proposent une élévation alors
/// que le service est arrêté, ou qui affichent « prêt » sans être connectées.
/// </para>
/// </remarks>
public sealed class ShellViewModelTests
{
    private sealed class FakeElevationLauncher(bool isElevated = false) : IElevationLauncher
    {
        public bool IsElevated { get; } = isElevated;

        public ElevationOutcome Outcome { get; set; } = ElevationOutcome.Launched;

        public int RequestCount { get; private set; }

        public ElevationOutcome RequestElevation()
        {
            RequestCount++;
            return Outcome;
        }
    }

    private static ShellViewModel Shell(
        ConnectionState state = ConnectionState.Connected,
        bool elevated = false,
        FakeElevationLauncher? launcher = null)
    {
        var model = new ShellViewModel(launcher ?? new FakeElevationLauncher(elevated))
        {
            ConnectionState = state,
            IsElevated = elevated,
        };

        return model;
    }

    // -- Consultation ouverte, modification verrouillée -----------------------

    [Fact]
    public void ConnecteMaisNonEleve_LaModificationEstBloqueeAvecUneRaison()
    {
        // FR-034 : la consultation est ouverte a tous, la modification demande l'elevation.
        ShellViewModel shell = Shell(ConnectionState.Connected, elevated: false);

        shell.CanEdit.Should().BeFalse();
        shell.EditingBlocked.Should().Be(EditingBlockedReason.ElevationRequired);
        shell.EditingBlockedMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ConnecteEtEleve_LaModificationEstPossible()
    {
        ShellViewModel shell = Shell(ConnectionState.Connected, elevated: true);

        shell.CanEdit.Should().BeTrue();
        shell.EditingBlocked.Should().Be(EditingBlockedReason.None);
        shell.EditingBlockedMessage.Should().BeNull();
    }

    // -- Priorité des causes de blocage ---------------------------------------

    [Theory]
    [InlineData(ConnectionState.Disconnected)]
    [InlineData(ConnectionState.Connecting)]
    [InlineData(ConnectionState.ServiceUnavailable)]
    public void ServiceInjoignable_PrimeSurLeBesoinDElevation(ConnectionState state)
    {
        // Proposer une elevation alors que le service est arrete enverrait l'utilisateur
        // cliquer sur une invite UAC pour rien.
        ShellViewModel shell = Shell(state, elevated: false);

        shell.EditingBlocked.Should().Be(EditingBlockedReason.ServiceUnavailable);
        shell.CanRequestElevation.Should().BeFalse();
    }

    [Fact]
    public void ServiceInjoignableMemeEleve_LaModificationResteBloquee()
    {
        ShellViewModel shell = Shell(ConnectionState.ServiceUnavailable, elevated: true);

        shell.CanEdit.Should().BeFalse();
        shell.EditingBlocked.Should().Be(EditingBlockedReason.ServiceUnavailable);
    }

    [Fact]
    public void VersionIncompatible_EstSignaleeDistinctement()
    {
        // La cause change l'action a proposer : mettre a jour, pas redemarrer le service.
        ShellViewModel shell = Shell(ConnectionState.VersionMismatch, elevated: true);

        shell.EditingBlocked.Should().Be(EditingBlockedReason.VersionMismatch);
        shell.EditingBlockedMessage.Should().Contain("version");
        shell.CanRequestElevation.Should().BeFalse();
    }

    [Fact]
    public void ToutesLesCombinaisons_RendentUneRaisonDefinie()
    {
        // Verrouille l'exhaustivite : une combinaison non traitee tomberait dans un etat
        // par defaut, et l'interface afficherait « pret » sans l'etre.
        foreach (ConnectionState state in Enum.GetValues<ConnectionState>())
        {
            foreach (bool elevated in (bool[])[true, false])
            {
                ShellViewModel shell = Shell(state, elevated);

                bool blocked = shell.EditingBlocked != EditingBlockedReason.None;

                shell.CanEdit.Should().Be(!blocked);
                (shell.EditingBlockedMessage is not null).Should().Be(blocked,
                    $"état {state}, élévation {elevated}");
            }
        }
    }

    // -- Demande d'élévation --------------------------------------------------

    [Fact]
    public void DemanderLElevation_QuandCEstElleQuiBloque_LanceLInstance()
    {
        var launcher = new FakeElevationLauncher(isElevated: false);
        ShellViewModel shell = Shell(ConnectionState.Connected, elevated: false, launcher);

        shell.RequestElevation().Should().Be(ElevationOutcome.Launched);

        launcher.RequestCount.Should().Be(1);
    }

    [Fact]
    public void DemanderLElevation_QuandLeServiceEstInjoignable_NeDeclencheAucuneInvite()
    {
        var launcher = new FakeElevationLauncher(isElevated: false);
        ShellViewModel shell = Shell(ConnectionState.ServiceUnavailable, elevated: false, launcher);

        shell.RequestElevation();

        launcher.RequestCount.Should().Be(0, "aucune invite UAC ne doit s'afficher pour rien");
    }

    [Fact]
    public void ElevationRefusee_LaisseLInterfaceEnLectureSeuleAvecUnMessage()
    {
        // Cas limite « elevation refusee » de la spec : l'etat applique reste celui d'avant
        // la tentative, sans configuration a moitie ecrite.
        var launcher = new FakeElevationLauncher(isElevated: false) { Outcome = ElevationOutcome.Declined };
        ShellViewModel shell = Shell(ConnectionState.Connected, elevated: false, launcher);

        shell.RequestElevation().Should().Be(ElevationOutcome.Declined);

        shell.CanEdit.Should().BeFalse();
        shell.StatusMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void EchecDeLancement_EstSignale()
    {
        var launcher = new FakeElevationLauncher(isElevated: false) { Outcome = ElevationOutcome.Failed };
        ShellViewModel shell = Shell(ConnectionState.Connected, elevated: false, launcher);

        shell.RequestElevation().Should().Be(ElevationOutcome.Failed);

        shell.StatusMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DejaEleve_NeRedemandePasLElevation()
    {
        // FR-034c : une seule invite par session d'edition.
        var launcher = new FakeElevationLauncher(isElevated: true);
        ShellViewModel shell = Shell(ConnectionState.Connected, elevated: true, launcher);

        shell.RequestElevation().Should().Be(ElevationOutcome.AlreadyElevated);

        launcher.RequestCount.Should().Be(0);
    }

    // -- Notifications de changement ------------------------------------------

    [Fact]
    public void ChangerLEtatDeConnexion_NotifieLesProprietesDerivees()
    {
        // Sans notification, l'interface resterait grisee apres reconnexion et l'utilisateur
        // croirait le service toujours absent.
        ShellViewModel shell = Shell(ConnectionState.ServiceUnavailable, elevated: true);
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        shell.ConnectionState = ConnectionState.Connected;

        changed.Should().Contain(nameof(ShellViewModel.CanEdit));
        changed.Should().Contain(nameof(ShellViewModel.EditingBlocked));
        changed.Should().Contain(nameof(ShellViewModel.EditingBlockedMessage));
        shell.CanEdit.Should().BeTrue();
    }

    [Fact]
    public void ChangerLElevation_NotifieLesProprietesDerivees()
    {
        ShellViewModel shell = Shell(ConnectionState.Connected, elevated: false);
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        shell.IsElevated = true;

        changed.Should().Contain(nameof(ShellViewModel.CanEdit));
        shell.CanEdit.Should().BeTrue();
    }

    // -- Robustesse -----------------------------------------------------------

    [Fact]
    public void Constructeur_SansLanceur_Leve()
    {
        Action act = () => _ = new ShellViewModel(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
