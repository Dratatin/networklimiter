using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NetworkLimiter.Contracts.Messages;

namespace NetworkLimiter.App.ViewModels;

/// <summary>Une application proposée à la limitation.</summary>
public sealed partial class ObservedAppViewModel : ObservableObject
{
    /// <summary>Crée l'entrée.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> est <c>null</c>.</exception>
    public ObservedAppViewModel(ObservedAppDto app)
    {
        ArgumentNullException.ThrowIfNull(app);

        ExecutablePath = app.ExecutablePath;
        ExecutableName = app.ExecutableName;
        _alreadyRuled = app.AlreadyRuled;
    }

    /// <summary>Chemin normalisé, identité réelle de l'application (FR-039).</summary>
    public string ExecutablePath { get; }

    /// <summary>Nom de fichier, affiché en premier parce que c'est ce qu'on reconnaît.</summary>
    public string ExecutableName { get; }

    [ObservableProperty]
    private bool _alreadyRuled;
}

/// <summary>
/// Liste des applications ayant une activité réseau, pour choisir quoi limiter.
/// </summary>
/// <remarks>
/// <para>
/// Répond à FR-001 et à SC-001 : l'utilisateur doit pouvoir limiter une application sans
/// connaître le chemin de son exécutable. Sans cette liste, il faudrait le saisir à la main —
/// ce qui suppose de savoir où Windows a installé le programme, et d'orthographier un chemin
/// sans se tromper.
/// </para>
/// <para>
/// La mise à jour <b>fusionne</b> au lieu de remplacer. Reconstruire la collection à chaque
/// rafraîchissement — toutes les dix secondes — ferait disparaître la sélection en cours sous
/// le curseur de l'utilisateur, exactement au moment où il s'apprête à cliquer.
/// </para>
/// </remarks>
public sealed partial class AppListViewModel : ObservableObject
{
    /// <summary>Applications vues, triées par le service.</summary>
    public ObservableCollection<ObservedAppViewModel> Applications { get; } = [];

    [ObservableProperty]
    private ObservedAppViewModel? _selected;

    /// <summary>Aucune application n'a encore été vue communiquer.</summary>
    public bool IsEmpty => Applications.Count == 0;

    /// <summary>Texte affiché quand la liste est vide.</summary>
    /// <remarks>
    /// Une liste vide n'est presque jamais une panne : c'est un service qui vient de démarrer et
    /// n'a encore rien observé. Le dire évite de chercher un problème là où il n'y en a pas.
    /// </remarks>
    public static string EmptyHint =>
        "Aucune application n'a encore communiqué depuis le démarrage du service. " +
        "Ouvrez une page ou lancez un téléchargement.";

    /// <summary>Fusionne une observation venue du service dans la liste affichée.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="observed"/> est <c>null</c>.</exception>
    public void Merge(IReadOnlyList<ObservedAppDto> observed)
    {
        ArgumentNullException.ThrowIfNull(observed);

        Dictionary<string, ObservedAppDto> incoming = observed.ToDictionary(
            app => app.ExecutablePath,
            StringComparer.Ordinal);

        string? selectedPath = Selected?.ExecutablePath;

        for (int index = Applications.Count - 1; index >= 0; index--)
        {
            ObservedAppViewModel existing = Applications[index];

            if (incoming.Remove(existing.ExecutablePath, out ObservedAppDto? updated))
            {
                existing.AlreadyRuled = updated.AlreadyRuled;
                continue;
            }

            Applications.RemoveAt(index);
        }

        foreach (ObservedAppDto app in observed.Where(app => incoming.ContainsKey(app.ExecutablePath)))
        {
            Applications.Add(new ObservedAppViewModel(app));
        }

        // Restaure la selection par chemin : l'objet a pu etre retire puis remis, et comparer
        // les references perdrait la selection sans raison visible pour l'utilisateur.
        if (selectedPath is not null && Selected is null)
        {
            Selected = Applications.FirstOrDefault(
                app => string.Equals(app.ExecutablePath, selectedPath, StringComparison.Ordinal));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}
