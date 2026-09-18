using System.Diagnostics.CodeAnalysis;
using System.Security.Principal;
using System.Windows;
using NetworkLimiter.App.Elevation;
using NetworkLimiter.App.Ipc;
using NetworkLimiter.App.ViewModels;
using NetworkLimiter.App.Views;

namespace NetworkLimiter.App;

/// <summary>
/// Point d'entrée de l'interface.
/// </summary>
/// <remarks>
/// <b>Jamais élevée par manifeste.</b> L'interface démarre avec les droits de l'utilisateur et
/// ne demande une élévation que s'il modifie quelque chose (FR-034, principe I). Un manifeste
/// <c>requireAdministrator</c> imposerait une invite UAC pour simplement consulter des limites,
/// et ferait tourner en permanence une fenêtre privilégiée sans nécessité.
/// </remarks>
public sealed partial class App : Application, IDisposable
{
    /// <inheritdoc />
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification =
            "La propriété du client passe au modèle de vue, puis à la fenêtre, qui le libère " +
            "à sa fermeture. Le libérer ici fermerait la liaison avant le premier affichage.")]
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var model = new MainViewModel(
            new PipeClient(),
            new ElevationLauncher(IsCurrentProcessElevated),
            new WpfDispatcher());

        var window = new MainWindow(model);

        _tray = new TrayIcon();
        _tray.OpenRequested += (_, _) => Show(window);
        _tray.ToggleRequested += (_, _) => model.ToggleSuspensionCommand.Execute(null);
        _tray.ExitRequested += (_, _) => Shutdown();

        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.Tray))
            {
                _tray.Update(model.Tray);
            }
        };

        // Fermer la fenetre MASQUE l'application au lieu de la quitter. C'est ce qui donne un
        // sens a l'icone : un outil de surveillance qu'on ferme par reflexe ne doit pas cesser
        // de fonctionner, et la sortie reste explicite par le menu « Quitter ».
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        window.Closing += (_, args) =>
        {
            if (!_exiting)
            {
                args.Cancel = true;
                window.Hide();
            }
        };

        Exit += (_, _) => _exiting = true;

        window.Show();
    }

    private TrayIcon? _tray;
    private bool _exiting;

    private static void Show(MainWindow window)
    {
        window.Show();

        // Une fenetre masquee puis reaffichee revient parfois derriere les autres, et
        // l'utilisateur croit que le clic n'a rien fait.
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    /// <summary>
    /// Constate l'élévation réelle du processus courant.
    /// </summary>
    /// <remarks>
    /// Sous UAC, le jeton filtré d'un administrateur non élevé ne porte pas le SID
    /// Administrateurs activé : la réponse est donc <c>false</c> pour une interface lancée
    /// normalement. C'est le même mécanisme que le service applique de son côté — l'interface
    /// n'a aucune raison d'en avoir un autre, et deux logiques divergeraient tôt ou tard.
    /// </remarks>
    private static bool IsCurrentProcessElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    /// <summary>Retire l icone de la zone de notification.</summary>
    /// <remarks>
    /// Idempotent : une icone dont le processus meurt sans l avoir retiree reste affichee
    /// jusqu a ce qu on survole la zone de notification, et laisse croire que l outil tourne
    /// encore. Mieux vaut la retirer deux fois qu une seule fois de trop peu.
    /// </remarks>
    public void Dispose()
    {
        _tray?.Dispose();
        _tray = null;
    }
}
