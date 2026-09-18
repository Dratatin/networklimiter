namespace NetworkLimiter.Service;

/// <summary>
/// Emplacements de données du service.
/// </summary>
/// <remarks>
/// Tout vit sous <c>%ProgramData%\NetworkLimiter</c>, dont les ACL restreignent l'écriture aux
/// seuls administrateurs. Ce n'est pas une commodité de rangement : c'est le verrou réel de
/// FR-034. Si un utilisateur standard pouvait écrire ici, il lèverait ses propres limites avec
/// un éditeur de texte, et l'autorisation IPC ne serait qu'un verrou d'interface.
/// </remarks>
public static class ServicePaths
{
    /// <summary>Répertoire racine des données du service.</summary>
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "NetworkLimiter");

    /// <summary>Fichier de configuration.</summary>
    public static string ConfigFile => Path.Combine(RootDirectory, "config.json");

    /// <summary>Répertoire des journaux.</summary>
    public static string LogDirectory => Path.Combine(RootDirectory, "logs");

    /// <summary>Gabarit de nom des fichiers de journal.</summary>
    public static string LogFileTemplate => Path.Combine(LogDirectory, "service-.log");
}
