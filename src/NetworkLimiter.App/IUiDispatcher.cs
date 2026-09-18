using System.Threading.Tasks;
using System.Windows;

namespace NetworkLimiter.App;

/// <summary>
/// Exécute une action sur le fil de l'interface.
/// </summary>
/// <remarks>
/// Abstraction nécessaire, pas décorative : sans elle, les modèles de vue dépendraient de WPF et
/// ne seraient testables qu'en ouvrant une fenêtre. Les cas qui comptent — connexion perdue,
/// notification reçue pendant une écriture — ne se reproduisent pas à la main dans une fenêtre.
/// </remarks>
public interface IUiDispatcher
{
    /// <summary>Exécute une action sur le fil de l'interface et attend son achèvement.</summary>
    Task InvokeAsync(Action action);
}

/// <summary>Répartiteur réel, adossé à celui de WPF.</summary>
public sealed class WpfDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Application? application = Application.Current;

        if (application is null)
        {
            // Hors application WPF — tests, arret en cours : executer sur place vaut mieux que
            // perdre l'action en silence.
            action();
            return Task.CompletedTask;
        }

        return application.Dispatcher.InvokeAsync(action).Task;
    }
}
