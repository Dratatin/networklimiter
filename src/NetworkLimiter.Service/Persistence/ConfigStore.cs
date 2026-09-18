using System.Globalization;
using System.Text.Json;

namespace NetworkLimiter.Service.Persistence;

/// <summary>Issue d'une lecture de configuration.</summary>
public enum ConfigReadStatus
{
    /// <summary>Configuration lue et structurellement valide.</summary>
    Ok,

    /// <summary>Aucune configuration : premier démarrage.</summary>
    Missing,

    /// <summary>Configuration illisible ou de version inconnue. Mise en quarantaine.</summary>
    Corrupt,
}

/// <summary>Résultat d'une lecture de configuration.</summary>
/// <param name="Status">Issue.</param>
/// <param name="Content">Contenu brut, présent seulement si <see cref="ConfigReadStatus.Ok"/>.</param>
/// <param name="QuarantinePath">Chemin du fichier mis de côté, si corrompu.</param>
/// <param name="Diagnostic">Message destiné à l'état de santé.</param>
public sealed record ConfigReadResult(
    ConfigReadStatus Status,
    byte[]? Content,
    string? QuarantinePath,
    string? Diagnostic);

/// <summary>
/// Étapes de l'écriture atomique.
/// </summary>
/// <remarks>
/// Publiques parce qu'elles documentent le protocole d'écriture, et parce que les tests
/// s'en servent pour simuler une coupure à chaque étape — le cas « perte de courant pendant
/// la sauvegarde », qui ne se reproduit pas autrement.
/// </remarks>
public enum ConfigWriteStage
{
    /// <summary>Le fichier temporaire vient d'être écrit et vidé sur le disque.</summary>
    TemporaryWritten,

    /// <summary>Le remplacement atomique va avoir lieu.</summary>
    BeforeReplace,

    /// <summary>Le remplacement a eu lieu, la sauvegarde va être supprimée.</summary>
    AfterReplace,
}

/// <summary>
/// Lit et écrit le fichier de configuration, de façon atomique.
/// </summary>
/// <remarks>
/// <para>
/// Ce composant ne connaît pas la structure de la configuration : il manipule des octets et
/// vérifie seulement la présence d'une version de schéma reconnue. La sérialisation typée
/// appartient aux couches supérieures. Cette séparation permet de tester la mécanique de
/// fichier — la partie qui peut détruire les réglages de l'utilisateur — indépendamment du
/// modèle de données, qui évoluera.
/// </para>
/// <para>
/// L'écriture suit strictement : fichier temporaire, vidage sur disque, remplacement atomique
/// avec sauvegarde, suppression de la sauvegarde. Après une interruption à n'importe quelle
/// étape, la relecture donne soit l'ancien contenu intégral, soit le nouveau — jamais un
/// fichier tronqué. C'est le cas « coupure de courant pendant la sauvegarde », qui sinon
/// produit une configuration corrompue au pire moment.
/// </para>
/// </remarks>
public sealed class ConfigStore
{
    /// <summary>Version de schéma reconnue.</summary>
    public const int SupportedSchemaVersion = 1;

    private readonly string _filePath;
    private readonly string _temporaryPath;
    private readonly string _backupPath;
    private readonly TimeProvider _clock;

    /// <summary>Point d'observation réservé aux tests, pour simuler une interruption.</summary>
    internal Action<ConfigWriteStage>? StageHook { get; set; }

    /// <summary>Crée un dépôt de configuration.</summary>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> est vide.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> est <c>null</c>.</exception>
    public ConfigStore(string filePath, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(clock);

        _filePath = filePath;
        _temporaryPath = filePath + ".tmp";
        _backupPath = filePath + ".bak";
        _clock = clock;
    }

    /// <summary>Chemin du fichier de configuration.</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// Lit la configuration.
    /// </summary>
    /// <remarks>
    /// Une configuration corrompue est <b>conservée</b> sous un nom horodaté, jamais
    /// supprimée : c'est la seule chance de récupérer les réglages de l'utilisateur. Et
    /// aucune limite n'est appliquée dans ce cas — un jeu de règles à moitié appliqué serait
    /// plus trompeur qu'aucune règle (FR-022).
    /// </remarks>
    public ConfigReadResult Read()
    {
        if (!File.Exists(_filePath))
        {
            return new ConfigReadResult(ConfigReadStatus.Missing, null, null, null);
        }

        byte[] content;
        try
        {
            content = File.ReadAllBytes(_filePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ConfigReadResult(
                ConfigReadStatus.Corrupt, null, null,
                $"Configuration illisible : {exception.Message}");
        }

        string? schemaProblem = ValidateSchema(content);
        if (schemaProblem is null)
        {
            return new ConfigReadResult(ConfigReadStatus.Ok, content, null, null);
        }

        string quarantinePath = Quarantine();

        return new ConfigReadResult(
            ConfigReadStatus.Corrupt, null, quarantinePath,
            $"{schemaProblem} Le fichier a été conservé sous « {Path.GetFileName(quarantinePath)} » " +
            "et aucune limite n'est appliquée.");
    }

    /// <summary>Écrit la configuration de façon atomique.</summary>
    public void Write(ReadOnlySpan<byte> content)
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 1. Ecriture dans un temporaire du MEME repertoire, donc du meme volume : File.Replace
        //    exige que source et destination soient sur le meme volume.
        using (var stream = new FileStream(
            _temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);   // 2. vidage jusqu'au disque, pas seulement au cache
        }

        StageHook?.Invoke(ConfigWriteStage.TemporaryWritten);

        if (!File.Exists(_filePath))
        {
            // Premiere ecriture : rien a remplacer.
            StageHook?.Invoke(ConfigWriteStage.BeforeReplace);
            File.Move(_temporaryPath, _filePath);
            StageHook?.Invoke(ConfigWriteStage.AfterReplace);
            return;
        }

        StageHook?.Invoke(ConfigWriteStage.BeforeReplace);

        // 3. Remplacement atomique avec sauvegarde de l'ancien contenu.
        File.Replace(_temporaryPath, _filePath, _backupPath, ignoreMetadataErrors: true);

        StageHook?.Invoke(ConfigWriteStage.AfterReplace);

        // 4. Suppression de la sauvegarde. Un echec ici est sans consequence : le nouveau
        //    contenu est deja en place.
        TryDelete(_backupPath);
    }

    /// <summary>
    /// Tente de récupérer une écriture interrompue.
    /// </summary>
    /// <remarks>
    /// Appelée au démarrage. Si une sauvegarde subsiste alors que le fichier principal est
    /// absent, l'interruption a eu lieu pendant le remplacement : la sauvegarde est le dernier
    /// contenu valide connu.
    /// </remarks>
    public void RecoverInterruptedWrite()
    {
        if (!File.Exists(_filePath) && File.Exists(_backupPath))
        {
            File.Move(_backupPath, _filePath);
        }

        // Un temporaire orphelin est un reliquat d'ecriture interrompue avant le
        // remplacement : son contenu n'a jamais ete valide.
        TryDelete(_temporaryPath);
    }

    private static string? ValidateSchema(byte[] content)
    {
        if (content.Length == 0)
        {
            return "Le fichier de configuration est vide.";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(content);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "La configuration n'est pas un objet JSON.";
            }

            // La verification du ValueKind n'est pas redondante : TryGetInt32 LEVE une
            // InvalidOperationException sur un element qui n'est pas un nombre, au lieu de
            // rendre false. Sans elle, une configuration portant "schemaVersion":"un" ferait
            // planter le service au demarrage au lieu d'etre mise en quarantaine.
            if (!document.RootElement.TryGetProperty("schemaVersion", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out int schemaVersion))
            {
                return "La configuration ne porte pas de version de schéma exploitable.";
            }

            if (schemaVersion != SupportedSchemaVersion)
            {
                return $"Version de schéma {schemaVersion.ToString(CultureInfo.InvariantCulture)} inconnue " +
                       $"(attendue : {SupportedSchemaVersion.ToString(CultureInfo.InvariantCulture)}).";
            }

            return null;
        }
        catch (JsonException exception)
        {
            return $"Configuration JSON invalide : {exception.Message}";
        }
    }

    private string Quarantine()
    {
        string timestamp = _clock.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string quarantinePath = $"{_filePath}.corrupt.{timestamp}.json";

        try
        {
            File.Move(_filePath, quarantinePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Si meme la mise de cote echoue, on n'ecrase surtout pas : le fichier reste en
            // place et la prochaine lecture le signalera a nouveau.
            return _filePath;
        }

        return quarantinePath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Sans consequence : un reliquat sera nettoye au prochain demarrage.
        }
    }
}
