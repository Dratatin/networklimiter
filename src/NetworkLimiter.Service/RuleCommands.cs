using System.Globalization;
using System.Runtime.Versioning;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Core.Classification;
using NetworkLimiter.Core.Units;
using NetworkLimiter.Service.Persistence;
using NetworkLimiter.Service.ProcessIdentity;

namespace NetworkLimiter.Service;

/// <summary>
/// Commandes de règles en ligne de commande.
/// </summary>
/// <remarks>
/// <para>
/// <b>Aide au développement, destinée à disparaître.</b> L'interface graphique (US1, tâches
/// T072 et T073) est le chemin prévu pour créer une règle ; en attendant, il faut un moyen de
/// valider que la limitation fonctionne réellement, sans quoi toute la chaîne resterait
/// non démontrée.
/// </para>
/// <para>
/// Elle emprunte volontairement le <b>même chemin</b> que le futur gestionnaire IPC :
/// <see cref="RuleStore"/>, mêmes validations, même écriture transactionnelle. Une commande qui
/// écrirait directement dans le fichier contournerait les invariants et validerait autre chose
/// que le produit.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class RuleCommands
{
    /// <summary>
    /// Exécute une commande en traduisant un refus d'accès en message utilisable.
    /// </summary>
    /// <remarks>
    /// Sans élévation, la sécurisation du répertoire de données échoue. Laisser remonter
    /// l'exception afficherait une trace de pile là où un « ouvrez un terminal administrateur »
    /// suffit — et laisserait croire à un défaut du produit plutôt qu'à un manque de droits.
    /// </remarks>
    public static int Run(Func<int> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            return command();
        }
        catch (Exception exception) when (
            exception is ConfigNotSecurableException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "Cette commande modifie la configuration du service : ouvrez un terminal " +
                "administrateur.");
            return 77;
        }
    }

    /// <summary>Ajoute ou remplace une règle et rend un code de sortie.</summary>
    public static int AddRule(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine(
                "Usage : --add-rule <chemin de l'exécutable> <Ko/s en descente> [Ko/s en montée]");
            return 64;
        }

        string executablePath = args[1];

        if (!File.Exists(executablePath))
        {
            Console.Error.WriteLine($"Introuvable : « {executablePath} ».");
            return 66;
        }

        if (!TryParseRate(args[2], out long? download, out string? downloadProblem))
        {
            Console.Error.WriteLine(downloadProblem);
            return 64;
        }

        long? upload = null;
        if (args.Length > 3 && !TryParseRate(args[3], out upload, out string? uploadProblem))
        {
            Console.Error.WriteLine(uploadProblem);
            return 64;
        }

        var normalizer = new PathNormalizer(new Win32DeviceVolumeResolver());
        string normalized = normalizer.Normalize(executablePath);
        string name = normalizer.GetExecutableName(executablePath);

        RuleStore store = OpenStore();

        // Remplace la regle existante visant la meme application plutot que d'echouer sur un
        // conflit : en usage de test, « je veux limiter ceci a tant » doit etre idempotent.
        RuleDto? existing = store.ActiveRules.FirstOrDefault(rule =>
            string.Equals(rule.Target.ExecutablePath, normalized, StringComparison.Ordinal));

        var rule = new RuleDto
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            Target = new AppIdentityDto
            {
                ExecutablePath = normalized,
                ExecutableName = name,
                DisplayName = Path.GetFileNameWithoutExtension(executablePath),
            },
            DownloadBytesPerSecond = download,
            UploadBytesPerSecond = upload,
            Enabled = true,
            ExemptFromGlobal = false,
        };

        WriteResult result = store.UpsertRule(existing?.Id, rule);

        if (!result.Ok)
        {
            Console.Error.WriteLine($"Refusé ({result.Code}) : {result.Message}");
            return 65;
        }

        Console.WriteLine($"Règle enregistrée pour « {normalized} ».");
        Console.WriteLine($"  descente : {Describe(download)}");
        Console.WriteLine($"  montée   : {Describe(upload)}");
        Console.WriteLine();
        Console.WriteLine("Redémarrez le service pour qu'elle prenne effet.");

        return 0;
    }

    /// <summary>Affiche les règles du profil actif.</summary>
    public static int ListRules()
    {
        RuleStore store = OpenStore();

        Console.WriteLine($"Profil actif : {store.Config.ActiveProfile.Name}");
        Console.WriteLine();

        if (store.ActiveRules.Count == 0)
        {
            Console.WriteLine("Aucune règle.");
            return 0;
        }

        foreach (RuleDto rule in store.ActiveRules)
        {
            string state = rule.Enabled ? "active" : "désactivée";

            Console.WriteLine($"[{state}] {rule.Target.ExecutablePath}");
            Console.WriteLine($"          descente : {Describe(rule.DownloadBytesPerSecond)}");
            Console.WriteLine($"          montée   : {Describe(rule.UploadBytesPerSecond)}");
        }

        return 0;
    }

    /// <summary>Supprime toutes les règles du profil actif.</summary>
    public static int ClearRules()
    {
        RuleStore store = OpenStore();
        int removed = 0;

        foreach (Guid id in store.ActiveRules.Select(rule => rule.Id).ToList())
        {
            if (store.DeleteRule(id).Ok)
            {
                removed++;
            }
        }

        Console.WriteLine($"{removed} règle(s) supprimée(s). Redémarrez le service.");
        return 0;
    }

    private static RuleStore OpenStore()
    {
        ConfigAcl.EnsureSecured(ServicePaths.RootDirectory);

        var configStore = new ConfigStore(ServicePaths.ConfigFile, TimeProvider.System);
        configStore.RecoverInterruptedWrite();

        ConfigReadResult read = configStore.Read();

        PersistedConfig config = read.Status == ConfigReadStatus.Ok
            ? ConfigSerializer.TryDeserialize(read.Content!, out _) ?? PersistedConfig.CreateDefault()
            : PersistedConfig.CreateDefault();

        return new RuleStore(configStore, config);
    }

    private static bool TryParseRate(string text, out long? bytesPerSecond, out string? problem)
    {
        bytesPerSecond = null;
        problem = null;

        if (string.Equals(text, "illimite", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "illimité", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            problem = $"« {text} » n'est pas un nombre de Ko/s.";
            return false;
        }

        long candidate = (long)Math.Round(value * 1024);

        if (!ByteRate.IsValid(candidate))
        {
            problem = $"Le plafond doit être compris entre {ByteRate.MinBytesPerSecond / 1024} " +
                      $"et {ByteRate.MaxBytesPerSecond / 1024} Ko/s.";
            return false;
        }

        bytesPerSecond = candidate;
        return true;
    }

    private static string Describe(long? bytesPerSecond) =>
        bytesPerSecond is { } value
            ? ByteRate.FromBytesPerSecond(value).ToString()
            : "illimité";
}
