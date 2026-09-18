using System.Diagnostics.CodeAnalysis;
using System.Security.Principal;
using System.Windows;
using NetworkLimiter.App.Elevation;
using NetworkLimiter.App.Ipc;
using NetworkLimiter.App.ViewModels;

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
public partial class App : Application
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

        new MainWindow(model).Show();
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

}
