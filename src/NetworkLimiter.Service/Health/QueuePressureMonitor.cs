namespace NetworkLimiter.Service.Health;

/// <summary>
/// Surveille la capacité de la boucle de drainage à suivre le débit entrant.
/// </summary>
/// <remarks>
/// <para>
/// WinDivert <b>rejette</b> les paquets quand sa file noyau déborde — 16 384 paquets, 32 Mo,
/// ou 2 secondes de séjour. Si la boucle de drainage décroche, la limitation se transforme en
/// perte de paquets non maîtrisée : un mode de défaillance opaque qui dégrade la latence de
/// toute la machine, pas seulement de l'application visée.
/// </para>
/// <para>
/// Ce moniteur détecte la situation pour que le service ferme ses handles et libère le trafic.
/// Mieux vaut ne plus limiter que dégrader la connexion sans le dire — c'est la lecture directe
/// du principe IV.
/// </para>
/// <para>
/// Le point de conception subtil : un lot plein isolé est <b>normal</b>. Un téléchargement qui
/// démarre remplit la file le temps que la boucle prenne son rythme. Seule une saturation
/// <b>continue</b> signifie qu'on décroche vraiment ; déclencher sur un pic libérerait le
/// trafic à chaque début de transfert.
/// </para>
/// </remarks>
public sealed class QueuePressureMonitor
{
    /// <summary>Facteur de lissage de la moyenne mobile exponentielle.</summary>
    /// <remarks>
    /// 0,2 donne une réponse en quelques dizaines de lots : assez rapide pour détecter un
    /// décrochage en moins d'une seconde, assez lente pour ignorer un lot isolé.
    /// </remarks>
    private const double SmoothingFactor = 0.2;

    private readonly TimeProvider _clock;
    private readonly double _threshold;
    private readonly TimeSpan _sustainedFor;

    private double _pressure;
    private long? _saturatedSinceTimestamp;

    /// <summary>Crée un moniteur.</summary>
    /// <param name="clock">Horloge injectée, pour tester sans attente réelle.</param>
    /// <param name="threshold">Pression au-delà de laquelle la file est réputée saturée, dans ]0, 1].</param>
    /// <param name="sustainedFor">Durée de saturation continue avant libération du trafic.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> est <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Le seuil est hors de ]0, 1].</exception>
    public QueuePressureMonitor(TimeProvider clock, double threshold, TimeSpan sustainedFor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(threshold, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(threshold, 1);

        _clock = clock;
        _threshold = threshold;
        _sustainedFor = sustainedFor;
    }

    /// <summary>Pression courante, entre 0 et 1.</summary>
    public double Pressure => _pressure;

    /// <summary>
    /// Indique que la saturation dure depuis assez longtemps pour libérer le trafic.
    /// </summary>
    public bool ShouldReleaseTraffic =>
        _saturatedSinceTimestamp is { } since &&
        _clock.GetElapsedTime(since) >= _sustainedFor;

    /// <summary>Enregistre le résultat d'une lecture par lot.</summary>
    /// <param name="packetsRead">Nombre de paquets effectivement lus.</param>
    /// <param name="batchCapacity">Taille maximale du lot demandé.</param>
    /// <exception cref="ArgumentOutOfRangeException">Les arguments sont incohérents.</exception>
    public void RecordBatch(int packetsRead, int batchCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(batchCapacity, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(packetsRead);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(packetsRead, batchCapacity);

        // Un lot plein signifie que la file avait au moins autant de paquets en attente :
        // c'est l'indicateur le plus direct dont on dispose, WinDivert n'exposant pas la
        // profondeur de sa file.
        double instantaneous = (double)packetsRead / batchCapacity;

        _pressure = _pressure == 0 && instantaneous == 0
            ? 0
            : (_pressure * (1 - SmoothingFactor)) + (instantaneous * SmoothingFactor);

        // Sans ce collage, la moyenne exponentielle n'atteint jamais exactement ses bornes
        // et une saturation totale n'afficherait « que » 0,97.
        if (instantaneous >= 1 && _pressure > 1 - SmoothingFactor)
        {
            _pressure = 1;
        }
        else if (instantaneous == 0 && _pressure < SmoothingFactor / 4)
        {
            _pressure = 0;
        }

        if (_pressure >= _threshold)
        {
            // Le debut de saturation n'est enregistre qu'une fois : la duree se mesure
            // depuis le premier depassement continu, pas depuis le dernier lot.
            _saturatedSinceTimestamp ??= _clock.GetTimestamp();
        }
        else
        {
            // Une accalmie prouve que la boucle a rattrape son retard. Repartir de zero
            // evite de liberer le trafic pour une saturation ancienne, deja resorbee.
            _saturatedSinceTimestamp = null;
        }
    }

    /// <summary>
    /// Remet la mesure à zéro.
    /// </summary>
    /// <remarks>
    /// Appelé à la réouverture des handles : repartir sur une mesure héritée de la session
    /// précédente ferait libérer le trafic immédiatement.
    /// </remarks>
    public void Reset()
    {
        _pressure = 0;
        _saturatedSinceTimestamp = null;
    }
}
