using System.Windows;
using NetworkLimiter.App.ViewModels;

namespace NetworkLimiter.App;

/// <summary>Fenêtre principale.</summary>
/// <remarks>
/// Ne porte aucune logique : elle relie un modèle de vue, lance sa connexion, et se ferme quand
/// une instance élevée prend le relais. Tout ce qui mérite d'être vérifié vit dans les modèles
/// de vue, testables sans ouvrir de fenêtre.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly MainViewModel _model;

    /// <summary>Crée la fenêtre autour de son modèle.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> est <c>null</c>.</exception>
    public MainWindow(MainViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _model = model;

        InitializeComponent();

        DataContext = model;
        model.ElevationLaunched += OnElevationLaunched;

        Loaded += async (_, _) => await model.StartAsync().ConfigureAwait(true);

        // La fenetre possede le modele, donc la liaison. Abandonner une connexion occuperait
        // une des quatre instances du tuyau jusqu'au delai d'inactivite, au detriment d'une
        // autre fenetre — dont l'instance elevee qui vient peut-etre de prendre le relais.
        Closed += (_, _) => model.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private void OnElevationLaunched(object? sender, EventArgs args)
    {
        // L'instance elevee est lancee et va se connecter a son tour. Garder celle-ci ouverte
        // afficherait deux fenetres dont une en lecture seule, sans que rien ne distingue
        // laquelle fait foi.
        _model.ElevationLaunched -= OnElevationLaunched;
        Close();
    }
}
