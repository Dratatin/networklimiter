using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetworkLimiter.App.Elevation;
using NetworkLimiter.App.Ipc;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Contracts.Serialization;

namespace NetworkLimiter.App.ViewModels;

/// <summary>
/// Orchestre la fenêtre : connexion, rafraîchissement, commandes.
/// </summary>
/// <remarks>
/// <para>
/// L'interface ne détient <b>aucun</b> état de son cru. Tout ce qu'elle affiche vient du dernier
/// <c>GetState</c>, et toute modification repart au service avant d'être affichée. Une interface
/// qui anticiperait le résultat d'une écriture finirait par montrer des limites que le service a
/// refusées — c'est-à-dire par mentir sur ce qui s'applique.
/// </para>
/// <para>
/// Le rafraîchissement périodique n'est pas seulement du confort : le contrat ferme les
/// connexions muettes au bout de trente secondes. Il maintient donc la liaison autant qu'il
/// rattrape une notification perdue.
/// </para>
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly PipeClient _client;
    private readonly IElevationLauncher _elevation;
    private readonly IUiDispatcher _dispatcher;
    private readonly CancellationTokenSource _stopping = new();

    private Task? _poller;

    /// <summary>Crée le modèle de vue.</summary>
    /// <exception cref="ArgumentNullException">Un argument est <c>null</c>.</exception>
    public MainViewModel(PipeClient client, IElevationLauncher elevation, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(elevation);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _client = client;
        _elevation = elevation;
        _dispatcher = dispatcher;

        Shell = new ShellViewModel(elevation);

        _client.Notification += OnNotification;
        _client.Disconnected += OnDisconnected;
    }

    /// <summary>État de connexion et d'élévation.</summary>
    public ShellViewModel Shell { get; }

    /// <summary>Applications vues communiquer.</summary>
    public AppListViewModel Apps { get; } = new();

    /// <summary>Règles du profil actif.</summary>
    public RuleListViewModel Rules { get; } = new();

    [ObservableProperty]
    private RuleEditorSessionViewModel? _editor;

    [ObservableProperty]
    private bool _interceptionAvailable = true;

    [ObservableProperty]
    private bool _suspended;

    /// <summary>Libellé du bouton d'arrêt d'urgence, qui dit ce qu'il va faire.</summary>
    /// <remarks>
    /// Le libellé décrit l'<b>action</b>, jamais l'état. Un bouton marqué « Suspendu » laisse
    /// l'utilisateur deviner si c'est un constat ou une commande — au moment précis où il veut
    /// récupérer sa connexion sans réfléchir.
    /// </remarks>
    public string SuspensionLabel => Suspended
        ? "Réappliquer les limites"
        : "Tout suspendre";

    partial void OnSuspendedChanged(bool value) => OnPropertyChanged(nameof(SuspensionLabel));

    /// <summary>Se connecte au service et démarre le rafraîchissement.</summary>
    public async Task StartAsync()
    {
        await ConnectAsync().ConfigureAwait(false);

        _poller = Task.Run(() => PollLoopAsync(_stopping.Token));
    }

    private async Task ConnectAsync()
    {
        ConnectionResult result = await _client
            .ConnectAsync(TimeSpan.FromSeconds(5), _stopping.Token)
            .ConfigureAwait(false);

        await _dispatcher.InvokeAsync(() =>
        {
            Shell.ConnectionState = result.State;
            Shell.IsElevated = result.Elevated;
            Shell.StatusMessage = result.Diagnostic;
        }).ConfigureAwait(false);

        if (result.State == ConnectionState.Connected)
        {
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PipeClient.PollInterval);

        while (await SafeWaitAsync(timer, cancellationToken).ConfigureAwait(false))
        {
            if (_client.State == ConnectionState.Connected)
            {
                await RefreshAsync().ConfigureAwait(false);
                continue;
            }

            // Reconnexion silencieuse : un service redemarre ne doit pas obliger l'utilisateur
            // a fermer et rouvrir l'interface pour la voir revivre.
            await ConnectAsync().ConfigureAwait(false);
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Redemande l'état complet au service et l'affiche.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            MessageEnvelope response = await _client
                .SendAsync(MessageEnvelope.CreateRequest(MessageTypes.GetState), _stopping.Token)
                .ConfigureAwait(false);

            if (response.Ok != true)
            {
                await SetStatusAsync(response.Error?.Message).ConfigureAwait(false);
                return;
            }

            GetStateResultPayload state =
                MessageSerializer.ReadPayload<GetStateResultPayload>(response);

            await _dispatcher.InvokeAsync(() => Apply(state)).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or System.Text.Json.JsonException)
        {
            await SetStatusAsync($"État indisponible : {exception.Message}").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Fermeture en cours.
        }
    }

    private void Apply(GetStateResultPayload state)
    {
        ProfileStateDto? active = state.Profiles
            .FirstOrDefault(profile => profile.Id == state.ActiveProfileId);

        Rules.Merge(active?.Rules ?? []);
        Apps.Merge(state.ObservedApplications);

        InterceptionAvailable = state.InterceptionAvailable;
        Suspended = state.Suspended;

        // L'ordre des messages suit celui des causes : l'interception hors service prime sur
        // la suspension, puisqu'elle rend cette derniere sans objet. Dire « suspendu » a
        // quelqu'un dont le pilote n'a pas demarre l'enverrait chercher au mauvais endroit.
        Shell.StatusMessage = (state.InterceptionAvailable, state.Suspended) switch
        {
            (false, _) => "L'interception n'est pas opérationnelle : aucune limite n'est appliquée.",
            (true, true) => "Limitation suspendue. Vos règles sont conservées et seront réappliquées à la reprise.",
            _ => null,
        };
    }

    private void OnNotification(object? sender, MessageEnvelope message)
    {
        if (!string.Equals(message.Type, MessageTypes.StateChanged, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            StateChangedPayload payload =
                MessageSerializer.ReadPayload<StateChangedPayload>(message);

            _ = _dispatcher.InvokeAsync(() => Apply(payload.State));
        }
        catch (System.Text.Json.JsonException)
        {
            // Notification illisible : le rafraichissement periodique rattrapera l'etat. Rien
            // ne justifie de rompre la liaison pour un message qu'on sait remplacer.
        }
    }

    private void OnDisconnected(object? sender, string reason) =>
        _ = _dispatcher.InvokeAsync(() =>
        {
            Shell.ConnectionState = ConnectionState.ServiceUnavailable;
            Shell.StatusMessage = reason;
        });

    /// <summary>Ouvre la création d'une règle pour l'application sélectionnée.</summary>
    [RelayCommand]
    private void CreateRule()
    {
        if (Apps.Selected is not { } app)
        {
            return;
        }

        Editor = RuleEditorSessionViewModel.Create(new ObservedAppDto
        {
            ExecutablePath = app.ExecutablePath,
            ExecutableName = app.ExecutableName,
            AlreadyRuled = app.AlreadyRuled,
        });
    }

    /// <summary>Ouvre l'édition de la règle sélectionnée.</summary>
    [RelayCommand]
    private void EditRule()
    {
        if (Rules.Selected is { } row)
        {
            Editor = RuleEditorSessionViewModel.Edit(row.State.Rule);
        }
    }

    /// <summary>Ferme l'éditeur sans enregistrer.</summary>
    [RelayCommand]
    private void CancelEdit() => Editor = null;

    /// <summary>Enregistre la règle en cours d'édition.</summary>
    [RelayCommand]
    private async Task SaveRuleAsync()
    {
        if (Editor is not { CanSave: true } editor)
        {
            return;
        }

        if (await SendWriteAsync(MessageTypes.UpsertRule, editor.ToPayload()).ConfigureAwait(false))
        {
            await _dispatcher.InvokeAsync(() => Editor = null).ConfigureAwait(false);
        }
    }

    /// <summary>Supprime la règle sélectionnée.</summary>
    [RelayCommand]
    private async Task DeleteRuleAsync()
    {
        if (Rules.Selected is { } row)
        {
            await SendWriteAsync(
                MessageTypes.DeleteRule, new DeleteRulePayload { RuleId = row.Id })
                .ConfigureAwait(false);
        }
    }

    /// <summary>Active ou désactive la règle sélectionnée.</summary>
    [RelayCommand]
    private async Task ToggleRuleAsync()
    {
        if (Rules.Selected is { } row)
        {
            await SendWriteAsync(
                MessageTypes.SetRuleEnabled,
                new SetRuleEnabledPayload { RuleId = row.Id, Enabled = !row.Enabled })
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Suspend toute la limitation, ou la reprend (FR-024).
    /// </summary>
    /// <remarks>
    /// L'arrêt d'urgence du produit. Rien n'est demandé à confirmer : quelqu'un dont la
    /// connexion ne répond plus doit pouvoir la rendre en un clic, pas en deux. La reprise est
    /// symétrique et les règles n'ont jamais été perdues.
    /// </remarks>
    [RelayCommand]
    private async Task ToggleSuspensionAsync() =>
        await SendWriteAsync(
            MessageTypes.SetSuspended,
            new SetSuspendedPayload { Suspended = !Suspended })
            .ConfigureAwait(false);

    /// <summary>Relance l'interface avec élévation.</summary>
    [RelayCommand]
    private void RequestElevation()
    {
        if (Shell.RequestElevation() == ElevationOutcome.Launched)
        {
            // L'instance elevee prend le relais ; celle-ci n'a plus lieu d'etre. La fermeture
            // est decidee par la vue, qui seule sait comment fermer une fenetre.
            ElevationLaunched?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Émis quand une instance élevée a été lancée et doit remplacer celle-ci.</summary>
    public event EventHandler? ElevationLaunched;

    private async Task<bool> SendWriteAsync<TPayload>(string type, TPayload payload)
        where TPayload : class
    {
        try
        {
            MessageEnvelope response = await _client
                .SendAsync(MessageEnvelope.CreateRequest(type, payload), _stopping.Token)
                .ConfigureAwait(false);

            if (response.Ok == true)
            {
                // Pas de mise a jour optimiste : on redemande l'etat. Le service peut avoir
                // normalise ou refuse une partie de ce qu'on a envoye, et l'affichage doit
                // montrer ce qui s'applique, pas ce qu'on esperait.
                await RefreshAsync().ConfigureAwait(false);
                return true;
            }

            await SetStatusAsync(response.Error?.Message ?? "Modification refusée.").ConfigureAwait(false);
            return false;
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            await SetStatusAsync($"Modification impossible : {exception.Message}").ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private Task SetStatusAsync(string? message) =>
        _dispatcher.InvokeAsync(() => Shell.StatusMessage = message);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _client.Notification -= OnNotification;
        _client.Disconnected -= OnDisconnected;

        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_poller is not null)
        {
            try
            {
                await _poller.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Arret demande.
            }
        }

        _stopping.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
