namespace NetworkLimiter.Service.Safety;

/// <summary>États du service.</summary>
public enum ServiceState
{
    /// <summary>Arrêté.</summary>
    Stopped,

    /// <summary>Vérification de la matrice de compatibilité en cours.</summary>
    CheckingCompatibility,

    /// <summary>Plateforme hors matrice : le service refuse de fonctionner, sans appliquer de limite.</summary>
    Refused,

    /// <summary>Chargement de la configuration en cours.</summary>
    LoadingConfig,

    /// <summary>Fonctionnement dégradé : configuration corrompue ou pilote absent. Aucune limite.</summary>
    Degraded,

    /// <summary>Ouverture des handles d'interception en cours.</summary>
    OpeningHandles,

    /// <summary>Mise en forme active. Seul état où des limites s'appliquent.</summary>
    Shaping,

    /// <summary>Suspension demandée par l'utilisateur. Règles conservées, aucune limite appliquée.</summary>
    Suspended,

    /// <summary>Défaillance : handles fermés, trafic libre.</summary>
    FailOpen,
}

/// <summary>Ferme les handles d'interception.</summary>
public interface IHandleCloser
{
    /// <summary>
    /// Ferme tous les handles ouverts. Doit être idempotent et ne jamais bloquer.
    /// </summary>
    void CloseAll();
}

/// <summary>
/// Machine à états du service, garante du fail-open.
/// </summary>
/// <remarks>
/// <para>
/// Traduction directe du principe IV. Un seul état — <see cref="ServiceState.Shaping"/> —
/// applique des limites ; tous les autres rendent le réseau à l'utilisateur.
/// </para>
/// <para>
/// La règle la plus importante est un <b>ordre</b>, pas seulement un effet : quitter
/// <see cref="ServiceState.Shaping"/> ferme les handles <i>avant</i> de changer d'état. Si
/// l'ordre était inverse, une exception levée entre les deux laisserait le service se croire
/// arrêté alors que le trafic reste étranglé, sans plus personne pour le libérer. C'est
/// exactement le mode de défaillance que le principe IV interdit.
/// </para>
/// </remarks>
public sealed class ServiceStateMachine
{
    /// <summary>
    /// Délai au-delà duquel une boucle de mise en forme sans progression est réputée bloquée.
    /// </summary>
    public static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(5);

    private readonly IHandleCloser _handles;
    private readonly TimeProvider _clock;
    private long _lastProgressTimestamp;

    /// <summary>Crée la machine à états.</summary>
    /// <exception cref="ArgumentNullException">Un argument est <c>null</c>.</exception>
    public ServiceStateMachine(IHandleCloser handles, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(clock);

        _handles = handles;
        _clock = clock;
        _lastProgressTimestamp = clock.GetTimestamp();
    }

    /// <summary>État courant.</summary>
    public ServiceState Current { get; private set; } = ServiceState.Stopped;

    /// <summary>Indique si des limites sont effectivement appliquées.</summary>
    public bool LimitsApplied => Current == ServiceState.Shaping;

    /// <summary>Dernière défaillance observée, à fin de diagnostic.</summary>
    public Exception? LastFailure { get; private set; }

    /// <summary>Passe à l'état demandé, en libérant le trafic si nécessaire.</summary>
    public void TransitionTo(ServiceState next)
    {
        if (Current == ServiceState.Shaping && next != ServiceState.Shaping)
        {
            ReleaseTraffic();
        }

        Current = next;

        if (next == ServiceState.Shaping)
        {
            _lastProgressTimestamp = _clock.GetTimestamp();
        }
    }

    /// <summary>Signale une défaillance : les handles sont fermés et le trafic libéré.</summary>
    public void Fail(Exception failure)
    {
        LastFailure = failure;
        TransitionTo(ServiceState.FailOpen);
    }

    /// <summary>Signale que la boucle de mise en forme progresse.</summary>
    public void ReportProgress() => _lastProgressTimestamp = _clock.GetTimestamp();

    /// <summary>
    /// Vérifie le chien de garde et bascule en fail-open si la boucle est bloquée.
    /// </summary>
    /// <remarks>
    /// Hors <see cref="ServiceState.Shaping"/>, aucune limite n'est appliquée : il n'y a rien
    /// à libérer, et déclencher produirait des transitions parasites.
    /// </remarks>
    public void CheckWatchdog()
    {
        if (Current != ServiceState.Shaping)
        {
            return;
        }

        TimeSpan sinceProgress = _clock.GetElapsedTime(_lastProgressTimestamp);

        if (sinceProgress > WatchdogTimeout)
        {
            Fail(new TimeoutException(
                $"La boucle de mise en forme n'a pas progressé depuis {sinceProgress}. " +
                "Le trafic est libéré."));
        }
    }

    private void ReleaseTraffic()
    {
        try
        {
            _handles.CloseAll();
        }
        catch (Exception exception)
        {
            // Si la fermeture elle-meme echoue, l'etat doit quand meme basculer. Rester en
            // Shaping laisserait le service croire qu'il limite encore, et empecherait toute
            // tentative ulterieure de liberation. On consigne et on continue : le pire ici
            // serait de propager et d'interrompre la sequence de liberation.
            LastFailure = exception;
        }
    }
}
