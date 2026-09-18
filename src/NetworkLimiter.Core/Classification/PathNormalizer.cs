namespace NetworkLimiter.Core.Classification;

/// <summary>
/// Met les chemins d'exécutable sous une forme canonique comparable.
/// </summary>
/// <remarks>
/// <para>
/// Windows désigne le même fichier de plusieurs façons : casse libre, séparateurs mixtes,
/// préfixe <c>\??\</c>, chemin de périphérique <c>\Device\HarddiskVolumeN</c>. Sans forme
/// canonique, deux écritures du même exécutable produiraient deux règles distinctes, et
/// l'utilisateur verrait la même application limitée deux fois avec des plafonds différents.
/// </para>
/// <para>
/// La normalisation est <b>idempotente</b> par construction : appliquée à son propre
/// résultat, elle ne change rien. C'est l'invariant qui rend la comparaison de chemins
/// indépendante du nombre de passages.
/// </para>
/// </remarks>
public sealed class PathNormalizer
{
    private const string DevicePrefix = @"\device\";
    private const string NtObjectPrefix = @"\??\";

    private readonly IDeviceVolumeResolver _volumeResolver;

    /// <summary>Crée un normaliseur.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="volumeResolver"/> est <c>null</c>.</exception>
    public PathNormalizer(IDeviceVolumeResolver volumeResolver)
    {
        ArgumentNullException.ThrowIfNull(volumeResolver);
        _volumeResolver = volumeResolver;
    }

    /// <summary>Rend la forme canonique d'un chemin d'exécutable.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> est <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> est vide ou blanc.</exception>
    public string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string working = path.Trim();
        if (working.Length == 0)
        {
            throw new ArgumentException("Le chemin ne peut pas être vide.", nameof(path));
        }

        working = working.Replace('/', '\\');
        working = working.ToLowerInvariant();

        if (working.StartsWith(NtObjectPrefix, StringComparison.Ordinal))
        {
            working = working[NtObjectPrefix.Length..];
        }

        working = CollapseSeparators(working);
        working = ResolveDevicePath(working);

        // Un separateur final ferait differer "c:\app" de "c:\app\" pour le meme repertoire.
        // La racine d'un lecteur ("c:\") est preservee : la tronquer produirait "c:", qui
        // designe le repertoire courant du lecteur et non sa racine.
        if (working.Length > 3 && working.EndsWith('\\'))
        {
            working = working.TrimEnd('\\');
        }

        return working;
    }

    /// <summary>
    /// Rend le nom de fichier de l'exécutable, en casse invariante.
    /// </summary>
    /// <remarks>
    /// C'est le critère de <b>repli</b> de FR-039 : lorsque le chemin enregistré n'existe
    /// plus — cas typique d'une application mise à jour vers un dossier versionné — la règle
    /// s'applique via ce nom, et l'interface doit le signaler (FR-039a).
    /// </remarks>
    public string GetExecutableName(string path)
    {
        string normalized = Normalize(path);
        int lastSeparator = normalized.LastIndexOf('\\');

        return lastSeparator >= 0 && lastSeparator < normalized.Length - 1
            ? normalized[(lastSeparator + 1)..]
            : normalized;
    }

    private static string CollapseSeparators(string path)
    {
        if (!path.Contains(@"\\", StringComparison.Ordinal))
        {
            return path;
        }

        // Le premier caractere est preserve tel quel : un chemin UNC commence par "\\" et
        // doit le rester.
        Span<char> buffer = path.Length <= 512 ? stackalloc char[path.Length] : new char[path.Length];
        int length = 0;

        for (int i = 0; i < path.Length; i++)
        {
            bool isDuplicateSeparator =
                i > 1 && path[i] == '\\' && buffer[length - 1] == '\\';

            if (!isDuplicateSeparator)
            {
                buffer[length++] = path[i];
            }
        }

        return new string(buffer[..length]);
    }

    private string ResolveDevicePath(string path)
    {
        if (!path.StartsWith(DevicePrefix, StringComparison.Ordinal))
        {
            return path;
        }

        // Le nom de peripherique s'arrete au separateur suivant :
        //   \device\harddiskvolume3\program files\... -> \device\harddiskvolume3
        int deviceEnd = path.IndexOf('\\', DevicePrefix.Length);
        string deviceName = deviceEnd < 0 ? path : path[..deviceEnd];

        if (!_volumeResolver.TryResolve(deviceName, out string driveLetter))
        {
            // Volume inconnu : on conserve le chemin de peripherique. Perdre l'information
            // serait pire — le chemin reste au moins comparable a lui-meme, donc la regle
            // continue de s'appliquer de facon stable.
            return path;
        }

        string remainder = deviceEnd < 0 ? string.Empty : path[deviceEnd..];

        return driveLetter.ToLowerInvariant() + remainder;
    }
}
