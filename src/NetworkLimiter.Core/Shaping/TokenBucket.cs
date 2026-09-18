using NetworkLimiter.Core.Units;

namespace NetworkLimiter.Core.Shaping;

/// <summary>
/// Seau à jetons : autorise un débit moyen tout en tolérant une rafale bornée.
/// </summary>
/// <remarks>
/// <para>
/// Cœur de la mise en forme. Le seau se recharge au débit cible et se vide à chaque paquet
/// émis ; un paquet ne passe que si assez de jetons sont disponibles. La capacité borne la
/// rafale : sans elle, une minute d'inactivité autoriserait ensuite une minute de trafic à
/// pleine vitesse.
/// </para>
/// <para>
/// Aucune dépendance à Windows, horloge injectée, aucun état global : toute la logique dont
/// une erreur serait invisible à l'œil nu est ici, et elle est testable en temps simulé
/// (principe III).
/// </para>
/// <para>
/// Cette classe n'est pas sûre vis-à-vis des accès concurrents ; elle est utilisée depuis la
/// seule boucle de mise en forme.
/// </para>
/// </remarks>
public sealed class TokenBucket
{
    /// <summary>Unité de transmission maximale retenue pour le calcul de capacité.</summary>
    public const int MaximumTransmissionUnit = 1500;

    private static readonly TimeSpan BurstWindow = TimeSpan.FromMilliseconds(100);

    private readonly TimeProvider _clock;
    private readonly TokenBucket? _parent;

    private ByteRate _rate;
    private long _capacityBytes;
    private double _availableTokens;
    private long _lastRefillTimestamp;

    /// <summary>Crée un seau.</summary>
    /// <param name="rate">Débit cible.</param>
    /// <param name="clock">Horloge, injectée pour rendre la mise en forme testable sans attente réelle.</param>
    /// <param name="parent">
    /// Seau parent, typiquement le plafond global. Un paquet n'est émis que si ce seau
    /// <b>et</b> son parent ont assez de jetons (FR-010).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> est <c>null</c>.</exception>
    public TokenBucket(ByteRate rate, TimeProvider clock, TokenBucket? parent = null)
    {
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;
        _parent = parent;
        _rate = rate;
        _capacityBytes = ComputeCapacity(rate);

        // Le seau demarre plein : une application qui vient d'etre limitee ne doit pas subir
        // une seconde d'arret complet avant que le premier jeton n'arrive.
        _availableTokens = _capacityBytes;
        _lastRefillTimestamp = clock.GetTimestamp();
    }

    /// <summary>Capacité du seau, en octets. Borne la rafale autorisée.</summary>
    public long CapacityBytes => _capacityBytes;

    /// <summary>Jetons actuellement disponibles.</summary>
    public double AvailableTokens => _availableTokens;

    /// <summary>Seau parent, ou <c>null</c>.</summary>
    public TokenBucket? Parent => _parent;

    /// <summary>
    /// Débit cible. Modifiable à chaud.
    /// </summary>
    /// <remarks>
    /// Changer le débit ne remet pas les jetons à zéro : l'utilisateur qui ajuste un plafond
    /// ne doit pas voir sa connexion se figer à chaque modification. Les jetons sont seulement
    /// ramenés sous la nouvelle capacité si celle-ci diminue.
    /// </remarks>
    public ByteRate Rate
    {
        get => _rate;
        set
        {
            Refill();

            _rate = value;
            _capacityBytes = ComputeCapacity(value);
            _availableTokens = Math.Min(_availableTokens, _capacityBytes);
        }
    }

    /// <summary>Recharge le seau au prorata du temps écoulé.</summary>
    public void Refill()
    {
        long now = _clock.GetTimestamp();
        TimeSpan elapsed = _clock.GetElapsedTime(_lastRefillTimestamp, now);

        // Une horloge qui recule — ajustement d'heure, synchronisation, sortie de veille —
        // ne doit ni creer de jetons, ni figer l'horodatage dans le futur. Le second cas
        // empecherait tout rechargement ulterieur et couperait l'application.
        if (elapsed <= TimeSpan.Zero)
        {
            _lastRefillTimestamp = now;
            return;
        }

        _availableTokens = Math.Min(
            _capacityBytes,
            _availableTokens + (elapsed.TotalSeconds * _rate.BytesPerSecond));

        _lastRefillTimestamp = now;
    }

    /// <summary>
    /// Tente de consommer les jetons correspondant à un paquet.
    /// </summary>
    /// <returns><c>false</c> si le paquet doit attendre.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> n'est pas positif.</exception>
    public bool TryConsume(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bytes, 0);

        Refill();
        _parent?.Refill();

        if (!CanAfford(bytes) || _parent?.CanAfford(bytes) == false)
        {
            return false;
        }

        // Les deux seaux sont debites ENSEMBLE, jamais l'un sans l'autre : debiter le parent
        // puis echouer sur l'enfant ferait payer au plafond global un paquet jamais emis.
        Withdraw(bytes);
        _parent?.Withdraw(bytes);

        return true;
    }

    private bool CanAfford(int bytes)
    {
        // Un paquet plus grand que la capacite ne tiendrait jamais dans le seau : a 10 Ko/s
        // la capacite vaut 3 000 octets, alors qu'un segment issu du deport de segmentation
        // atteint 65 535. Sans ce cas particulier, ce flux se bloquerait DEFINITIVEMENT et
        // l'utilisateur verrait l'application cesser de fonctionner. On exige alors un seau
        // plein : le paquet paie tout ce qu'il peut, et le debit moyen reste borne.
        return bytes > _capacityBytes
            ? _availableTokens >= _capacityBytes
            : _availableTokens >= bytes;
    }

    private void Withdraw(int bytes) =>
        _availableTokens = Math.Max(0, _availableTokens - bytes);

    private static long ComputeCapacity(ByteRate rate)
    {
        long burst = (long)(rate.BytesPerSecond * BurstWindow.TotalSeconds);

        // Plancher de deux MTU : en dessous, un seul paquet de taille courante saturerait le
        // seau et la mise en forme degenererait en arret-redemarrage permanent.
        return Math.Max(2L * MaximumTransmissionUnit, burst);
    }
}
