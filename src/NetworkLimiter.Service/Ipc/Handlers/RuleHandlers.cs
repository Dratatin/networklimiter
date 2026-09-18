using System.Diagnostics.CodeAnalysis;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Contracts.Serialization;
using NetworkLimiter.Service.Persistence;
using ILogger = Serilog.ILogger;

namespace NetworkLimiter.Service.Ipc.Handlers;

/// <summary>
/// Traite les commandes d'écriture portant sur les règles.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transactionnels du point de vue de l'appelant</b> : validation complète, puis persistance
/// atomique, puis publication en mémoire. Un échec à n'importe quelle étape laisse l'état
/// exactement tel qu'avant. C'est ce qui rend sûr le cas « élévation refusée en milieu de
/// modification » de la spécification — la garantie vit dans <see cref="RuleStore"/>, ces
/// gestionnaires ne font que s'y conformer.
/// </para>
/// <para>
/// La journalisation ne porte <b>jamais</b> d'adresse distante, de nom d'hôte ni d'URL (FR-033).
/// Le chemin de l'exécutable visé est en revanche consigné : c'est ce que l'utilisateur a
/// lui-même saisi, et sans lui un journal de règles ne sert à rien.
/// </para>
/// </remarks>
public sealed class RuleHandlers
{
    private readonly RuleStore _rules;
    private readonly ILogger _log;

    /// <summary>Crée les gestionnaires.</summary>
    /// <exception cref="ArgumentNullException">Un argument est <c>null</c>.</exception>
    public RuleHandlers(RuleStore rules, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(log);

        _rules = rules;
        _log = log;
    }

    /// <summary>Crée ou modifie une règle.</summary>
    public MessageEnvelope Upsert(MessageEnvelope request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryRead(request, out UpsertRulePayload? payload, out MessageEnvelope? malformed))
        {
            return malformed;
        }

        bool isCreation = payload.RuleId is null &&
            !_rules.ActiveRules.Any(rule => rule.Id == payload.Rule.Id);

        WriteResult result = _rules.UpsertRule(payload.RuleId, payload.Rule);

        if (!result.Ok)
        {
            return Failed(request, result);
        }

        _log.Information(
            "Règle {Action} : {Chemin}, descente {Descente}, montée {Montee}.",
            isCreation ? "créée" : "modifiée",
            payload.Rule.Target.ExecutablePath,
            Describe(payload.Rule.DownloadBytesPerSecond),
            Describe(payload.Rule.UploadBytesPerSecond));

        return Ok(request);
    }

    /// <summary>Supprime une règle.</summary>
    public MessageEnvelope Delete(MessageEnvelope request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryRead(request, out DeleteRulePayload? payload, out MessageEnvelope? malformed))
        {
            return malformed;
        }

        // Le chemin est lu AVANT la suppression : apres, la regle n'existe plus et le journal
        // ne pourrait plus dire ce qui a ete retire.
        string? target = _rules.ActiveRules
            .FirstOrDefault(rule => rule.Id == payload.RuleId)?.Target.ExecutablePath;

        WriteResult result = _rules.DeleteRule(payload.RuleId);

        if (!result.Ok)
        {
            return Failed(request, result);
        }

        _log.Information("Règle retirée : {Chemin}.", target ?? "cible inconnue");

        return Ok(request);
    }

    /// <summary>Active ou désactive une règle sans la supprimer.</summary>
    public MessageEnvelope SetEnabled(MessageEnvelope request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryRead(request, out SetRuleEnabledPayload? payload, out MessageEnvelope? malformed))
        {
            return malformed;
        }

        string? target = _rules.ActiveRules
            .FirstOrDefault(rule => rule.Id == payload.RuleId)?.Target.ExecutablePath;

        WriteResult result = _rules.SetRuleEnabled(payload.RuleId, payload.Enabled);

        if (!result.Ok)
        {
            return Failed(request, result);
        }

        _log.Information(
            "Règle {Etat} : {Chemin}.",
            payload.Enabled ? "réactivée" : "désactivée",
            target ?? "cible inconnue");

        return Ok(request);
    }

    private static bool TryRead<TPayload>(
        MessageEnvelope request,
        [NotNullWhen(true)] out TPayload? payload,
        [NotNullWhen(false)] out MessageEnvelope? malformed)
        where TPayload : class
    {
        try
        {
            payload = MessageSerializer.ReadPayload<TPayload>(request);
            malformed = null;
            return true;
        }
        catch (Exception exception) when (
            exception is System.Text.Json.JsonException or InvalidOperationException)
        {
            // Champ inconnu, champ manquant, type incorrect : le contrat impose le rejet du
            // message, jamais l'ignorance silencieuse d'un champ.
            payload = null;
            malformed = MessageEnvelope.CreateError(
                request.Id,
                ErrorCode.ValidationFailed,
                $"Message « {request.Type} » illisible : {exception.Message}");

            return false;
        }
    }

    private static MessageEnvelope Ok(MessageEnvelope request) =>
        new()
        {
            Type = request.Type + MessageTypes.ResultSuffix,
            Id = request.Id,
            Ok = true,
        };

    private static MessageEnvelope Failed(MessageEnvelope request, WriteResult result) =>
        MessageEnvelope.CreateError(
            request.Id,
            result.Code ?? ErrorCode.InternalError,
            result.Message ?? "Écriture refusée.");

    private static string Describe(long? bytesPerSecond) =>
        bytesPerSecond is { } value
            ? Core.Units.ByteRate.FromBytesPerSecond(value).ToString()
            : "illimité";
}
