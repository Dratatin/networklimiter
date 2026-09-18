using NetworkLimiter.Core.Classification;

namespace NetworkLimiter.Service.ProcessIdentity;

/// <summary>Informations brutes sur un processus vivant.</summary>
/// <param name="ExecutablePath">Chemin de l'exécutable, tel que rendu par le système.</param>
/// <param name="StartTime">Heure de démarrage, en ticks UTC.</param>
/// <param name="IsPackaged">Indique une application packagée (Microsoft Store).</param>
public sealed record ProcessInfo(string ExecutablePath, long StartTime, bool IsPackaged);

/// <summary>Fournit les informations brutes d'un processus.</summary>
/// <remarks>
/// Abstraction nécessaire : la lecture réelle passe par <c>QueryFullProcessImageName</c>, une
/// API Windows. La séparer permet de tester la garde anti-réutilisation de PID — la partie qui
/// peut être fausse — sans dépendre de processus réels, dont on ne contrôle ni les
/// identifiants ni la durée de vie.
/// </remarks>
public interface IProcessInfoProvider
{
    /// <summary>Tente de lire les informations d'un processus.</summary>
    /// <returns><c>false</c> si le processus a disparu ou est inaccessible.</returns>
    bool TryGetProcessInfo(uint processId, out ProcessInfo? info);
}

/// <summary>Identité résolue d'un processus.</summary>
/// <param name="ProcessId">Identifiant du processus.</param>
/// <param name="StartTime">Heure de démarrage, en ticks UTC.</param>
/// <param name="NormalizedPath">Chemin normalisé de l'exécutable.</param>
/// <param name="ExecutableName">Nom de fichier de l'exécutable, critère de repli de FR-039.</param>
/// <param name="IsPackaged">Indique une application packagée.</param>
public sealed record ResolvedProcess(
    uint ProcessId,
    long StartTime,
    string NormalizedPath,
    string ExecutableName,
    bool IsPackaged);

/// <summary>
/// Résout l'application propriétaire d'un processus, avec cache.
/// </summary>
/// <remarks>
/// <para>
/// La résolution est appelée pour chaque flux établi : sans cache, le service interrogerait le
/// système des milliers de fois par seconde.
/// </para>
/// <para>
/// La clé du cache est le couple <b>(identifiant de processus, heure de démarrage)</b>, jamais
/// l'identifiant seul. Windows réutilise les identifiants, souvent rapidement : une entrée
/// périmée attribuerait le trafic d'une application à une autre — donc appliquerait une limite
/// à la mauvaise application, sans qu'aucun symptôme ne l'indique. C'est exactement le genre
/// de défaillance silencieuse que le principe VI cherche à exclure.
/// </para>
/// </remarks>
public sealed class ProcessIdentityResolver
{
    private readonly IProcessInfoProvider _provider;
    private readonly PathNormalizer _normalizer;
    private readonly Dictionary<uint, ResolvedProcess> _cache = [];
    private readonly int _maxEntries;

    // Les deux boucles d'interception resolvent des identites en parallele : celle des flux a
    // l'ouverture, celle du reseau a chaque paquet. Sans verrou, ce dictionnaire se corrompt —
    // constate en execution reelle, la boucle des flux est morte sur « a concurrent update was
    // performed on this collection ».
    private readonly Lock _gate = new();

    /// <summary>Crée un résolveur.</summary>
    /// <exception cref="ArgumentNullException">Un argument est <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEntries"/> n'est pas positif.</exception>
    public ProcessIdentityResolver(
        IProcessInfoProvider provider,
        PathNormalizer normalizer,
        int maxEntries = 1024)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxEntries, 0);

        _provider = provider;
        _normalizer = normalizer;
        _maxEntries = maxEntries;
    }

    /// <summary>Nombre d'entrées en cache.</summary>
    public int CacheCount
    {
        get
        {
            lock (_gate)
            {
                return _cache.Count;
            }
        }
    }

    /// <summary>
    /// Résout l'identité d'un processus.
    /// </summary>
    /// <returns>
    /// <c>null</c> si le processus a disparu ou est inaccessible. Son trafic n'est alors
    /// <b>jamais</b> limité : dans le doute, on laisse passer (principe IV).
    /// </returns>
    public ResolvedProcess? Resolve(uint processId)
    {
        // Appel systeme hors verrou, deliberement : le verrou protege le cache, pas le noyau.
        // Le tenir pendant un appel systeme serialiserait les deux boucles d'interception sur
        // Windows, ce qui est exactement ce que la separation en deux threads evite.
        if (!_provider.TryGetProcessInfo(processId, out ProcessInfo? info) || info is null)
        {
            // Le processus a disparu entre l'evenement de flux et cette resolution, ou il
            // est protege. On oublie toute entree de cache : la conserver reviendrait a
            // attribuer du trafic a un processus qui n'existe plus.
            Forget(processId);
            return null;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(processId, out ResolvedProcess? cached) &&
                cached.StartTime == info.StartTime)
            {
                return cached;
            }
        }

        // Soit l'entree est absente, soit son heure de demarrage differe : l'identifiant a
        // ete reutilise par un autre processus. Dans les deux cas, on resout a neuf.
        string normalizedPath;
        string executableName;

        try
        {
            normalizedPath = _normalizer.Normalize(info.ExecutablePath);
            executableName = _normalizer.GetExecutableName(info.ExecutablePath);
        }
        catch (ArgumentException)
        {
            // Chemin vide ou illisible : on refuse d'inventer une identite. Le trafic reste
            // non limite.
            Forget(processId);
            return null;
        }

        var resolved = new ResolvedProcess(
            processId, info.StartTime, normalizedPath, executableName, info.IsPackaged);

        lock (_gate)
        {
            // Deux boucles peuvent resoudre le meme identifiant en meme temps et inscrire
            // toutes deux : sans consequence, elles produisent la meme valeur a partir des
            // memes informations. Verrouiller la resolution entiere pour l'eviter couterait
            // un appel systeme serialise afin de ne rien gagner.
            if (_cache.Count >= _maxEntries && !_cache.ContainsKey(processId))
            {
                EvictArbitrary();
            }

            _cache[processId] = resolved;
        }

        return resolved;
    }

    /// <summary>Retire une entrée du cache, par exemple à la disparition d'un processus.</summary>
    public void Forget(uint processId)
    {
        lock (_gate)
        {
            _cache.Remove(processId);
        }
    }

    /// <summary>Vide le cache.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _cache.Clear();
        }
    }

    private void EvictArbitrary()
    {
        // Le cache est borne par prudence memoire, pas par exactitude : une entree evincee
        // sera simplement resolue a nouveau au prochain flux. Un choix arbitraire suffit
        // donc, et evite de maintenir un ordre d'usage pour un gain nul.
        foreach (uint key in _cache.Keys)
        {
            _cache.Remove(key);
            return;
        }
    }
}
