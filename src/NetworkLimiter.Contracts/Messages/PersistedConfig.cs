using System.Text.Json.Serialization;
using NetworkLimiter.Core.Units;

namespace NetworkLimiter.Contracts.Messages;

/// <summary>Plafond global appliqué à tout le trafic de la machine.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GlobalLimitDto
{
    /// <summary>Le plafond global est actif (FR-012).</summary>
    [JsonPropertyName("enabled")]
    public required bool Enabled { get; init; }

    /// <summary>Plafond descendant, ou <c>null</c> pour illimité.</summary>
    [JsonPropertyName("downloadBytesPerSecond")]
    public long? DownloadBytesPerSecond { get; init; }

    /// <summary>Plafond montant, ou <c>null</c> pour illimité.</summary>
    [JsonPropertyName("uploadBytesPerSecond")]
    public long? UploadBytesPerSecond { get; init; }

    /// <summary>Plafond global désactivé, sans valeur.</summary>
    public static GlobalLimitDto Disabled => new() { Enabled = false };

    /// <summary>Valide les bornes.</summary>
    /// <returns>Le motif du refus, ou <c>null</c> si valide.</returns>
    public string? Validate()
    {
        if (DownloadBytesPerSecond is { } download && !ByteRate.IsValid(download))
        {
            return "Le plafond global descendant est hors des bornes supportées.";
        }

        if (UploadBytesPerSecond is { } upload && !ByteRate.IsValid(upload))
        {
            return "Le plafond global montant est hors des bornes supportées.";
        }

        return null;
    }
}

/// <summary>Un profil : un jeu de règles et un plafond global.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileDto
{
    /// <summary>Longueur maximale d'un nom de profil.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Nombre maximal de règles dans un profil.</summary>
    public const int MaxRules = 200;

    /// <summary>Identifiant, immuable après création.</summary>
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    /// <summary>Nom du profil.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Plafond global du profil.</summary>
    [JsonPropertyName("globalLimit")]
    public required GlobalLimitDto GlobalLimit { get; init; }

    /// <summary>Règles du profil.</summary>
    [JsonPropertyName("rules")]
    public required IReadOnlyList<RuleDto> Rules { get; init; }

    /// <summary>Valide le profil et tout ce qu'il contient.</summary>
    /// <returns>Le motif du refus, ou <c>null</c> si valide.</returns>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > MaxNameLength)
        {
            return $"Le nom du profil doit faire entre 1 et {MaxNameLength} caractères.";
        }

        if (Name.Any(char.IsControl))
        {
            return "Le nom du profil ne peut pas contenir de caractère de contrôle.";
        }

        if (Rules.Count > MaxRules)
        {
            return $"Un profil ne peut pas contenir plus de {MaxRules} règles.";
        }

        if (GlobalLimit.Validate() is { } globalProblem)
        {
            return globalProblem;
        }

        foreach (RuleDto rule in Rules)
        {
            if (rule.Validate() is { } ruleProblem)
            {
                return ruleProblem;
            }
        }

        // Deux regles visant la meme application produiraient deux plafonds concurrents sur
        // le meme trafic, sans qu'aucun ne soit manifestement le bon.
        IEnumerable<string> duplicates = Rules
            .GroupBy(rule => rule.Target.ExecutablePath, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);

        return duplicates.FirstOrDefault() is { } duplicate
            ? $"Deux règles visent « {duplicate} »."
            : null;
    }
}

/// <summary>Racine du fichier de configuration.</summary>
/// <remarks>
/// Les profils figurent dès maintenant, alors que US1 n'en utilise qu'un seul. C'est
/// délibéré : les introduire plus tard imposerait une migration de schéma et un incrément de
/// <c>schemaVersion</c>, donc du code de compatibilité à écrire et à tester pour un produit
/// qui n'a pas encore d'utilisateurs.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersistedConfig
{
    /// <summary>Nombre maximal de profils.</summary>
    public const int MaxProfiles = 5;

    /// <summary>Nom du profil créé au premier démarrage.</summary>
    public const string DefaultProfileName = "Par défaut";

    /// <summary>Version du schéma.</summary>
    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    /// <summary>Profil actif.</summary>
    [JsonPropertyName("activeProfileId")]
    public required Guid ActiveProfileId { get; init; }

    /// <summary>Profils enregistrés.</summary>
    [JsonPropertyName("profiles")]
    public required IReadOnlyList<ProfileDto> Profiles { get; init; }

    /// <summary>Crée une configuration neuve, avec un profil vide.</summary>
    public static PersistedConfig CreateDefault()
    {
        Guid profileId = Guid.NewGuid();

        return new PersistedConfig
        {
            SchemaVersion = 1,
            ActiveProfileId = profileId,
            Profiles =
            [
                new ProfileDto
                {
                    Id = profileId,
                    Name = DefaultProfileName,
                    GlobalLimit = GlobalLimitDto.Disabled,
                    Rules = [],
                },
            ],
        };
    }

    /// <summary>Rend le profil actif.</summary>
    public ProfileDto ActiveProfile =>
        Profiles.FirstOrDefault(profile => profile.Id == ActiveProfileId) ?? Profiles[0];

    /// <summary>Valide la configuration entière.</summary>
    /// <returns>Le motif du refus, ou <c>null</c> si valide.</returns>
    public string? Validate()
    {
        if (SchemaVersion != 1)
        {
            return $"Version de schéma {SchemaVersion} inconnue.";
        }

        // Au moins un profil en permanence : sans profil actif, le service n'aurait aucun
        // jeu de regles a appliquer et l'interface n'aurait rien a afficher.
        if (Profiles.Count is 0 or > MaxProfiles)
        {
            return $"La configuration doit contenir entre 1 et {MaxProfiles} profils.";
        }

        if (Profiles.All(profile => profile.Id != ActiveProfileId))
        {
            return "Le profil actif ne figure pas dans la configuration.";
        }

        if (Profiles.Select(p => p.Id).Distinct().Count() != Profiles.Count)
        {
            return "Deux profils partagent le même identifiant.";
        }

        if (Profiles.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Profiles.Count)
        {
            return "Deux profils portent le même nom.";
        }

        foreach (ProfileDto profile in Profiles)
        {
            if (profile.Validate() is { } problem)
            {
                return problem;
            }
        }

        return null;
    }
}
