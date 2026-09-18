namespace NetworkLimiter.Service.Resilience;

/// <summary>Cause d'un changement d'environnement réseau.</summary>
public enum NetworkChangeReason
{
    /// <summary>Une adresse ou un adaptateur a changé.</summary>
    AddressChanged,

    /// <summary>La disponibilité réseau a changé.</summary>
    AvailabilityChanged,

    /// <summary>La machine sort de veille.</summary>
    ResumedFromSleep,
}

/// <summary>
/// Regroupe les rafales d'événements réseau en une seule notification.
/// </summary>
/// <remarks>
/// <para>
/// Windows émet ces événements par <b>rafales</b> : une simple bascule Wi-Fi vers Ethernet en
/// produit facilement une dizaine en quelques centaines de millisecondes, et une sortie de
/// veille davantage encore. Réagir à chacun ferait rouvrir les handles d'interception autant de
/// fois, avec à chaque fois une fenêtre pendant laquelle le trafic n'est pas limité — soit
/// exactement l'inverse de l'effet recherché.
/// </para>
/// <para>
/// Le regroupement attend donc un silence avant de notifier. La logique est isolée ici, avec
/// une horloge injectée, pour être testable sans provoquer de vrais changements réseau.
/// </para>
/// </remarks>
public sealed class NetworkChangeCoalescer
{
    /// <summary>Durée de silence exigée avant de notifier.</summary>
    public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _clock;
    private readonly TimeSpan _quietPeriod;

    private long? _lastEventTimestamp;
    private NetworkChangeReason _pendingReason;

    /// <summary>Crée un regroupeur.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> est <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">La période de silence n'est pas positive.</exception>
    public NetworkChangeCoalescer(TimeProvider clock, TimeSpan? quietPeriod = null)
    {
        ArgumentNullException.ThrowIfNull(clock);

        TimeSpan period = quietPeriod ?? DefaultQuietPeriod;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);

        _clock = clock;
        _quietPeriod = period;
    }

    /// <summary>Indique qu'un changement attend d'être notifié.</summary>
    public bool HasPendingChange => _lastEventTimestamp is not null;

    /// <summary>Enregistre un événement réseau.</summary>
    public void Record(NetworkChangeReason reason)
    {
        // Une sortie de veille prime sur les autres causes : c'est la plus perturbante pour
        // la pile reseau, et c'est celle qu'il faut afficher a l'utilisateur si la
        // reapplication prend du temps.
        if (_lastEventTimestamp is null || reason == NetworkChangeReason.ResumedFromSleep)
        {
            _pendingReason = reason;
        }

        _lastEventTimestamp = _clock.GetTimestamp();
    }

    /// <summary>
    /// Rend la cause à traiter si le silence a été assez long, sinon <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Appelée périodiquement par la boucle du service. Consommer la notification remet le
    /// regroupeur à zéro : le changement suivant repartira d'une rafale neuve.
    /// </remarks>
    public NetworkChangeReason? TryConsume()
    {
        if (_lastEventTimestamp is not { } since)
        {
            return null;
        }

        if (_clock.GetElapsedTime(since) < _quietPeriod)
        {
            return null;
        }

        _lastEventTimestamp = null;
        return _pendingReason;
    }

    /// <summary>Oublie tout changement en attente.</summary>
    public void Reset() => _lastEventTimestamp = null;
}
