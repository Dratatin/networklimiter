using System.Text.Json;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Contracts.Serialization;

namespace NetworkLimiter.Service.Persistence;

/// <summary>Résultat d'une écriture.</summary>
/// <param name="Ok">L'écriture a été appliquée et persistée.</param>
/// <param name="Code">Code d'erreur destiné à l'appelant, ou <c>null</c> si réussi.</param>
/// <param name="Message">Message affichable, ou <c>null</c> si réussi.</param>
public sealed record WriteResult(bool Ok, ErrorCode? Code, string? Message)
{
    /// <summary>Écriture réussie.</summary>
    public static WriteResult Success { get; } = new(true, null, null);

    /// <summary>Écriture refusée.</summary>
    public static WriteResult Failed(ErrorCode code, string message) => new(false, code, message);
}

/// <summary>
/// Détient la configuration en mémoire et la persiste de façon transactionnelle.
/// </summary>
/// <remarks>
/// <para>
/// La garantie qui compte, et que le contrat exige : <b>un échec à n'importe quelle étape
/// laisse l'état exactement tel qu'avant</b>. C'est ce qui rend sûr le cas limite « élévation
/// refusée en milieu de modification » de la spécification.
/// </para>
/// <para>
/// L'ordre retenu est : calculer l'état résultant, le valider entièrement, le <b>persister</b>,
/// et seulement ensuite le publier en mémoire. Appliquer en mémoire avant de persister — comme
/// une lecture rapide du contrat pourrait le suggérer — laisserait le service et le disque
/// divergents si l'écriture échouait, et un redémarrage ferait alors « reculer » des réglages
/// que l'utilisateur a vus appliqués.
/// </para>
/// </remarks>
public sealed class RuleStore
{
    private readonly ConfigStore _configStore;
    private PersistedConfig _config;

    /// <summary>Crée un dépôt autour d'une configuration déjà chargée.</summary>
    /// <exception cref="ArgumentNullException">Un argument est <c>null</c>.</exception>
    public RuleStore(ConfigStore configStore, PersistedConfig initial)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(initial);

        _configStore = configStore;
        _config = initial;
    }

    /// <summary>Configuration courante.</summary>
    public PersistedConfig Config => _config;

    /// <summary>Règles du profil actif.</summary>
    public IReadOnlyList<RuleDto> ActiveRules => _config.ActiveProfile.Rules;

    /// <summary>Émis après chaque écriture réussie.</summary>
    /// <remarks>
    /// Diffusé à <b>tous</b> les clients connectés, y compris non élevés : une interface en
    /// lecture seule doit refléter les changements faits par une instance élevée, sans quoi
    /// l'utilisateur verrait deux fenêtres se contredire.
    /// </remarks>
    public event EventHandler<PersistedConfig>? Changed;

    /// <summary>Crée ou modifie une règle.</summary>
    public WriteResult UpsertRule(Guid? ruleId, RuleDto rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.Validate() is { } problem)
        {
            return WriteResult.Failed(ErrorCode.ValidationFailed, problem);
        }

        ProfileDto profile = _config.ActiveProfile;
        Guid effectiveId = ruleId ?? rule.Id;
        List<RuleDto> rules = [.. profile.Rules];

        int index = rules.FindIndex(existing => existing.Id == effectiveId);

        // Deux regles actives visant la meme application produiraient deux plafonds
        // concurrents sur le meme trafic, sans qu'aucun ne soit manifestement le bon.
        bool conflicts = rules.Any(existing =>
            existing.Id != effectiveId &&
            string.Equals(existing.Target.ExecutablePath, rule.Target.ExecutablePath, StringComparison.Ordinal));

        if (conflicts)
        {
            return WriteResult.Failed(
                ErrorCode.ConflictingRule,
                $"Une règle vise déjà « {rule.Target.DisplayName} ». Modifiez-la plutôt que d'en créer une seconde.");
        }

        RuleDto stored = rule with { Id = effectiveId };

        if (index >= 0)
        {
            rules[index] = stored;
        }
        else
        {
            if (rules.Count >= ProfileDto.MaxRules)
            {
                return WriteResult.Failed(
                    ErrorCode.ValidationFailed,
                    $"Un profil ne peut pas contenir plus de {ProfileDto.MaxRules} règles.");
            }

            rules.Add(stored);
        }

        return Commit(profile with { Rules = rules });
    }

    /// <summary>Supprime une règle.</summary>
    public WriteResult DeleteRule(Guid ruleId)
    {
        ProfileDto profile = _config.ActiveProfile;
        List<RuleDto> rules = [.. profile.Rules];

        if (rules.RemoveAll(rule => rule.Id == ruleId) == 0)
        {
            return WriteResult.Failed(ErrorCode.NotFound, "Cette règle n'existe plus.");
        }

        return Commit(profile with { Rules = rules });
    }

    /// <summary>Active ou désactive une règle sans la supprimer (FR-006).</summary>
    public WriteResult SetRuleEnabled(Guid ruleId, bool enabled)
    {
        ProfileDto profile = _config.ActiveProfile;
        List<RuleDto> rules = [.. profile.Rules];

        int index = rules.FindIndex(rule => rule.Id == ruleId);
        if (index < 0)
        {
            return WriteResult.Failed(ErrorCode.NotFound, "Cette règle n'existe plus.");
        }

        rules[index] = rules[index] with { Enabled = enabled };

        return Commit(profile with { Rules = rules });
    }

    private WriteResult Commit(ProfileDto updatedProfile)
    {
        List<ProfileDto> profiles = [.. _config.Profiles];
        int index = profiles.FindIndex(profile => profile.Id == updatedProfile.Id);

        if (index < 0)
        {
            return WriteResult.Failed(ErrorCode.InternalError, "Le profil actif a disparu.");
        }

        profiles[index] = updatedProfile;
        PersistedConfig candidate = _config with { Profiles = profiles };

        // Validation de l'etat RESULTANT, pas seulement de la modification : c'est la seule
        // facon d'attraper les invariants qui portent sur l'ensemble, comme l'unicite des
        // cibles ou le nombre maximal de regles.
        if (candidate.Validate() is { } problem)
        {
            return WriteResult.Failed(ErrorCode.ValidationFailed, problem);
        }

        try
        {
            _configStore.Write(ConfigSerializer.Serialize(candidate));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // L'etat en memoire n'a pas ete touche : l'appelant voit un echec propre, et le
            // service continue d'appliquer exactement ce qu'il appliquait avant.
            return WriteResult.Failed(
                ErrorCode.InternalError,
                $"La configuration n'a pas pu être enregistrée : {exception.Message}");
        }

        _config = candidate;
        Changed?.Invoke(this, candidate);

        return WriteResult.Success;
    }
}

/// <summary>Sérialise la configuration persistée.</summary>
public static class ConfigSerializer
{
    /// <summary>Sérialise une configuration en octets UTF-8.</summary>
    public static byte[] Serialize(PersistedConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return MessageSerializer.SerializeConfig(config);
    }

    /// <summary>
    /// Désérialise une configuration.
    /// </summary>
    /// <returns><c>null</c> si le contenu est illisible ou invalide.</returns>
    /// <remarks>
    /// Un contenu importé ou relu est <b>revalidé intégralement</b>, comme une saisie
    /// utilisateur : un fichier de configuration est un vecteur d'entrée au même titre qu'un
    /// message IPC.
    /// </remarks>
    public static PersistedConfig? TryDeserialize(ReadOnlySpan<byte> utf8Json, out string? problem)
    {
        try
        {
            PersistedConfig? config = MessageSerializer.DeserializeConfig(utf8Json);

            if (config is null)
            {
                problem = "La configuration est vide.";
                return null;
            }

            problem = config.Validate();
            return problem is null ? config : null;
        }
        catch (JsonException exception)
        {
            problem = $"Configuration illisible : {exception.Message}";
            return null;
        }
    }
}
