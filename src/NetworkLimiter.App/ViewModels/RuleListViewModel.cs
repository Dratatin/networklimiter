using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NetworkLimiter.Contracts.Messages;

namespace NetworkLimiter.App.ViewModels;

/// <summary>Une règle telle qu'elle s'affiche dans la liste.</summary>
public sealed partial class RuleRowViewModel : ObservableObject
{
    /// <summary>Crée la ligne.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> est <c>null</c>.</exception>
    public RuleRowViewModel(RuleStateDto state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    [ObservableProperty]
    private RuleStateDto _state;

    /// <summary>Identifiant de la règle.</summary>
    public Guid Id => State.Rule.Id;

    /// <summary>Nom affichable de l'application visée.</summary>
    public string DisplayName => State.Rule.Target.DisplayName;

    /// <summary>Chemin visé, affiché en second plan.</summary>
    public string ExecutablePath => State.Rule.Target.ExecutablePath;

    /// <summary>La règle est activée par l'utilisateur.</summary>
    public bool Enabled => State.Rule.Enabled;

    /// <summary>Plafond descendant, en toutes lettres.</summary>
    public string Download => Describe(State.Rule.DownloadBytesPerSecond);

    /// <summary>Plafond montant, en toutes lettres.</summary>
    public string Upload => Describe(State.Rule.UploadBytesPerSecond);

    /// <summary>La règle s'applique réellement en ce moment.</summary>
    public bool IsApplied => State.Status == RuleApplicationStatus.Active;

    /// <summary>
    /// Ce que l'interface affiche quand la règle ne s'applique pas (FR-026).
    /// </summary>
    /// <remarks>
    /// Chaque raison est traduite en une phrase qui dit <b>quoi faire</b>. Afficher le nom de
    /// l'énumération laisserait l'utilisateur devant un mot anglais sans indication d'action,
    /// ce qui reviendrait à ne rien expliquer.
    /// </remarks>
    public string? InactiveExplanation => State.InactiveReason switch
    {
        null => null,

        RuleInactiveReasonDto.ApplicationNotRunning =>
            "L'application n'est pas lancée. La limite s'appliquera dès son démarrage.",

        RuleInactiveReasonDto.ExecutablePathNotFound =>
            "L'exécutable est introuvable à ce chemin. Il a peut-être été déplacé ou désinstallé.",

        RuleInactiveReasonDto.MatchedByFallbackName =>
            "Le chemin enregistré n'existe plus ; la limite s'applique par le nom de l'exécutable.",

        RuleInactiveReasonDto.RuleDisabled => "Règle désactivée.",

        RuleInactiveReasonDto.GloballySuspended => "Toute la limitation est suspendue.",

        RuleInactiveReasonDto.InterceptionUnavailable =>
            "L'interception n'est pas opérationnelle. Aucune limite n'est appliquée.",

        RuleInactiveReasonDto.PackagedAppUnsupported =>
            "Les applications du Microsoft Store ne sont pas prises en charge.",

        // Un service plus recent peut rendre une raison que cette interface ignore. La taire
        // laisserait une regle inactive sans aucune explication, ce qui est pire qu'un libelle
        // brut : l'utilisateur saurait au moins quoi chercher.
        var other => other.ToString(),
    };

    /// <summary>Nombre de processus couverts, pour lever le doute sur « pourquoi 0 ? ».</summary>
    public int MatchedProcessCount => State.MatchedProcessCount;

    /// <summary>Met à jour la ligne sans la remplacer, pour préserver la sélection.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> est <c>null</c>.</exception>
    public void Update(RuleStateDto state)
    {
        ArgumentNullException.ThrowIfNull(state);

        State = state;

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ExecutablePath));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Download));
        OnPropertyChanged(nameof(Upload));
        OnPropertyChanged(nameof(IsApplied));
        OnPropertyChanged(nameof(InactiveExplanation));
        OnPropertyChanged(nameof(MatchedProcessCount));
    }

    private static string Describe(long? bytesPerSecond) =>
        bytesPerSecond is { } value
            ? Core.Units.ByteRate.FromBytesPerSecond(value).ToString()
            : "illimité";
}

/// <summary>
/// Les règles du profil actif.
/// </summary>
/// <remarks>
/// Comme <see cref="AppListViewModel"/>, la mise à jour fusionne : la liste se rafraîchit toutes
/// les dix secondes et à chaque notification, et reconstruire la collection ferait sauter la
/// sélection sous le curseur.
/// </remarks>
public sealed partial class RuleListViewModel : ObservableObject
{
    /// <summary>Règles du profil actif.</summary>
    public ObservableCollection<RuleRowViewModel> Rules { get; } = [];

    [ObservableProperty]
    private RuleRowViewModel? _selected;

    /// <summary>Aucune règle n'est définie.</summary>
    public bool IsEmpty => Rules.Count == 0;

    /// <summary>Fusionne l'état venu du service.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="incoming"/> est <c>null</c>.</exception>
    public void Merge(IReadOnlyList<RuleStateDto> incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        Dictionary<Guid, RuleStateDto> byId = incoming.ToDictionary(state => state.Rule.Id);
        Guid? selectedId = Selected?.Id;

        for (int index = Rules.Count - 1; index >= 0; index--)
        {
            RuleRowViewModel existing = Rules[index];

            if (byId.Remove(existing.Id, out RuleStateDto? updated))
            {
                existing.Update(updated);
                continue;
            }

            Rules.RemoveAt(index);
        }

        foreach (RuleStateDto state in incoming.Where(state => byId.ContainsKey(state.Rule.Id)))
        {
            Rules.Add(new RuleRowViewModel(state));
        }

        if (selectedId is { } id && Selected is null)
        {
            Selected = Rules.FirstOrDefault(rule => rule.Id == id);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}
