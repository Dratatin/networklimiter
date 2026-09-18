using CommunityToolkit.Mvvm.ComponentModel;
using NetworkLimiter.App.Elevation;
using NetworkLimiter.App.Ipc;

namespace NetworkLimiter.App.ViewModels;

/// <summary>Raison pour laquelle les commandes de modification sont indisponibles.</summary>
public enum EditingBlockedReason
{
    /// <summary>La modification est possible.</summary>
    None,

    /// <summary>L'interface n'est pas élevée (FR-034a).</summary>
    ElevationRequired,

    /// <summary>Le service est injoignable.</summary>
    ServiceUnavailable,

    /// <summary>Le service parle une version de protocole incompatible.</summary>
    VersionMismatch,
}

/// <summary>
/// État global de la fenêtre : connexion, élévation, disponibilité des commandes.
/// </summary>
/// <remarks>
/// <para>
/// Porte FR-034b : les commandes de modification sont <b>visibles mais désactivées</b>, avec la
/// raison affichée, plutôt que masquées ou laissées actives pour échouer après saisie. Un
/// utilisateur qui ne voit pas une commande croit qu'elle n'existe pas ; un utilisateur dont
/// la saisie échoue après coup a perdu son travail.
/// </para>
/// <para>
/// Aucune dépendance à WPF : ce modèle de vue se teste sans fenêtre, ce qui permet de couvrir
/// exhaustivement les combinaisons connexion × élévation — précisément là où naissent les
/// interfaces qui mentent sur leur état.
/// </para>
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IElevationLauncher _elevationLauncher;

    [ObservableProperty]
    private ConnectionState _connectionState = ConnectionState.Disconnected;

    [ObservableProperty]
    private bool _isElevated;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Crée le modèle de vue.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="elevationLauncher"/> est <c>null</c>.</exception>
    public ShellViewModel(IElevationLauncher elevationLauncher)
    {
        ArgumentNullException.ThrowIfNull(elevationLauncher);

        _elevationLauncher = elevationLauncher;
        _isElevated = elevationLauncher.IsElevated;
    }

    /// <summary>Les commandes de modification sont utilisables.</summary>
    public bool CanEdit => EditingBlocked == EditingBlockedReason.None;

    /// <summary>Raison pour laquelle la modification est impossible.</summary>
    public EditingBlockedReason EditingBlocked => ConnectionState switch
    {
        // L'indisponibilite du service prime : proposer une elevation alors que le service
        // est arrete enverrait l'utilisateur cliquer sur une invite UAC pour rien.
        ConnectionState.VersionMismatch => EditingBlockedReason.VersionMismatch,
        not ConnectionState.Connected => EditingBlockedReason.ServiceUnavailable,
        _ when !IsElevated => EditingBlockedReason.ElevationRequired,
        _ => EditingBlockedReason.None,
    };

    /// <summary>Texte affiché à côté des commandes désactivées (FR-034b).</summary>
    public string? EditingBlockedMessage => EditingBlocked switch
    {
        EditingBlockedReason.ElevationRequired =>
            "La consultation est ouverte à tous ; modifier une limite demande une élévation.",
        EditingBlockedReason.ServiceUnavailable =>
            "Le service NetworkLimiter est injoignable. Aucune limite n'est appliquée.",
        EditingBlockedReason.VersionMismatch =>
            "Le service et l'interface ne parlent pas la même version. Mettez-les à jour ensemble.",
        _ => null,
    };

    /// <summary>
    /// Proposer l'élévation n'a de sens que si c'est bien elle qui bloque.
    /// </summary>
    public bool CanRequestElevation => EditingBlocked == EditingBlockedReason.ElevationRequired;

    /// <summary>Demande l'élévation et rend le résultat.</summary>
    public ElevationOutcome RequestElevation()
    {
        if (!CanRequestElevation)
        {
            return _elevationLauncher.IsElevated
                ? ElevationOutcome.AlreadyElevated
                : ElevationOutcome.Failed;
        }

        ElevationOutcome outcome = _elevationLauncher.RequestElevation();

        StatusMessage = outcome switch
        {
            // Un refus n'est pas une erreur : l'interface reste en lecture seule, sans etat a
            // moitie modifie (cas limite « elevation refusee » de la spec).
            ElevationOutcome.Declined => "Élévation refusée. L'interface reste en consultation.",
            ElevationOutcome.Failed => "L'instance élevée n'a pas pu être lancée.",
            _ => null,
        };

        return outcome;
    }

    partial void OnConnectionStateChanged(ConnectionState value) => RaiseEditingState();

    partial void OnIsElevatedChanged(bool value) => RaiseEditingState();

    private void RaiseEditingState()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(EditingBlocked));
        OnPropertyChanged(nameof(EditingBlockedMessage));
        OnPropertyChanged(nameof(CanRequestElevation));
    }
}
