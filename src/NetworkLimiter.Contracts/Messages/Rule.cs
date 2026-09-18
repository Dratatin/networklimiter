using System.Text.Json.Serialization;
using NetworkLimiter.Core.Rules;
using NetworkLimiter.Core.Units;

namespace NetworkLimiter.Contracts.Messages;

/// <summary>Identité d'une application, telle qu'elle circule sur le canal de contrôle.</summary>
/// <remarks>
/// Reprend <see cref="AppIdentity"/> de <c>Core</c> plutôt que de la dupliquer : une seconde
/// définition finirait par diverger, et les deux moitiés du produit n'appliqueraient plus la
/// même notion d'identité.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppIdentityDto
{
    /// <summary>Chemin complet normalisé de l'exécutable. Critère primaire (FR-039).</summary>
    [JsonPropertyName("executablePath")]
    public required string ExecutablePath { get; init; }

    /// <summary>Nom de fichier de l'exécutable. Critère de repli (FR-039a).</summary>
    [JsonPropertyName("executableName")]
    public required string ExecutableName { get; init; }

    /// <summary>Nom lisible. Affichage seulement, jamais utilisé pour apparier.</summary>
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    /// <summary>Longueur maximale acceptée pour un chemin.</summary>
    public const int MaxPathLength = 32_767;

    /// <summary>Longueur maximale acceptée pour un nom affiché.</summary>
    public const int MaxDisplayNameLength = 256;

    /// <summary>Convertit vers le type du domaine.</summary>
    public AppIdentity ToDomain() => new(ExecutablePath, ExecutableName, DisplayName);

    /// <summary>
    /// Valide les bornes de longueur.
    /// </summary>
    /// <returns>Le motif du refus, ou <c>null</c> si valide.</returns>
    /// <remarks>
    /// Bornes vérifiées côté service parce que le message vient d'un processus moins
    /// privilégié : une chaîne de plusieurs mégaoctets serait acceptée par le cadrage — elle
    /// tient dans 64 Ko — mais gonflerait la configuration persistée sans limite.
    /// </remarks>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath) || ExecutablePath.Length > MaxPathLength)
        {
            return "Le chemin de l'exécutable est vide ou trop long.";
        }

        if (string.IsNullOrWhiteSpace(ExecutableName) || ExecutableName.Length > MaxPathLength)
        {
            return "Le nom de l'exécutable est vide ou trop long.";
        }

        if (DisplayName.Length > MaxDisplayNameLength)
        {
            return "Le nom affiché est trop long.";
        }

        return null;
    }
}

/// <summary>Une règle de limitation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuleDto
{
    /// <summary>Identifiant de la règle.</summary>
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    /// <summary>Application visée.</summary>
    [JsonPropertyName("target")]
    public required AppIdentityDto Target { get; init; }

    /// <summary>Plafond descendant en octets par seconde, ou <c>null</c> pour illimité.</summary>
    [JsonPropertyName("downloadBytesPerSecond")]
    public long? DownloadBytesPerSecond { get; init; }

    /// <summary>Plafond montant en octets par seconde, ou <c>null</c> pour illimité.</summary>
    [JsonPropertyName("uploadBytesPerSecond")]
    public long? UploadBytesPerSecond { get; init; }

    /// <summary>La règle est active (FR-006).</summary>
    [JsonPropertyName("enabled")]
    public required bool Enabled { get; init; }

    /// <summary>La règle échappe au plafond global (FR-011).</summary>
    [JsonPropertyName("exemptFromGlobal")]
    public required bool ExemptFromGlobal { get; init; }

    /// <summary>
    /// Valide la règle.
    /// </summary>
    /// <returns>Le motif du refus, ou <c>null</c> si valide.</returns>
    /// <remarks>
    /// <para>
    /// Une règle dont les deux plafonds sont absents est <b>valide mais sans effet</b>. Elle
    /// est affichée comme telle plutôt que silencieusement supprimée : l'utilisateur qui
    /// vient de retirer ses deux plafonds ne doit pas voir sa règle disparaître.
    /// </para>
    /// <para>
    /// Un plafond de zéro serait un blocage, que FR-028 exclut du périmètre : les bornes de
    /// <see cref="ByteRate"/> le refusent donc.
    /// </para>
    /// </remarks>
    public string? Validate()
    {
        string? targetProblem = Target?.Validate();
        if (Target is null)
        {
            return "La règle ne vise aucune application.";
        }

        if (targetProblem is not null)
        {
            return targetProblem;
        }

        if (DownloadBytesPerSecond is { } download && !ByteRate.IsValid(download))
        {
            return $"Le plafond descendant doit être compris entre {ByteRate.MinBytesPerSecond} " +
                   $"et {ByteRate.MaxBytesPerSecond} octets par seconde.";
        }

        if (UploadBytesPerSecond is { } upload && !ByteRate.IsValid(upload))
        {
            return $"Le plafond montant doit être compris entre {ByteRate.MinBytesPerSecond} " +
                   $"et {ByteRate.MaxBytesPerSecond} octets par seconde.";
        }

        return null;
    }
}

/// <summary>Charge utile de création ou de modification d'une règle.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpsertRulePayload
{
    /// <summary>Identifiant de la règle à modifier, ou <c>null</c> pour en créer une.</summary>
    [JsonPropertyName("ruleId")]
    public Guid? RuleId { get; init; }

    /// <summary>Contenu de la règle.</summary>
    [JsonPropertyName("rule")]
    public required RuleDto Rule { get; init; }
}

/// <summary>Charge utile de suppression d'une règle.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeleteRulePayload
{
    /// <summary>Identifiant de la règle à supprimer.</summary>
    [JsonPropertyName("ruleId")]
    public required Guid RuleId { get; init; }
}

/// <summary>Charge utile d'activation ou de désactivation d'une règle.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetRuleEnabledPayload
{
    /// <summary>Identifiant de la règle.</summary>
    [JsonPropertyName("ruleId")]
    public required Guid RuleId { get; init; }

    /// <summary>Nouvel état.</summary>
    [JsonPropertyName("enabled")]
    public required bool Enabled { get; init; }
}
