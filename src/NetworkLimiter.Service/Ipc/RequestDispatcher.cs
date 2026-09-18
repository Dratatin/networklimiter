using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Service.Ipc.Handlers;
using NetworkLimiter.Service.Safety;
using ILogger = Serilog.ILogger;

namespace NetworkLimiter.Service.Ipc;

/// <summary>
/// Achemine chaque requête vers son gestionnaire, après autorisation.
/// </summary>
/// <remarks>
/// <para>
/// L'autorisation est évaluée <b>à chaque message</b>, jamais mémorisée pour la connexion. Le
/// contrat l'exige, et la raison est concrète : une interface peut relancer une instance élevée
/// en cours de session, et rien ne garantit qu'un jeton reste ce qu'il était. Retenir « cet
/// appelant est autorisé » transformerait une élévation ponctuelle en droit permanent.
/// </para>
/// <para>
/// Un type inconnu, ou connu mais non implémenté par cette version, est <b>refusé</b> avec un
/// message qui le dit. Répondre par un succès vide laisserait l'interface croire que sa commande
/// a pris effet.
/// </para>
/// </remarks>
public sealed class RequestDispatcher
{
    private readonly RuleHandlers _ruleHandlers;
    private readonly IStateSource _state;
    private readonly IHealthSource _health;
    private readonly SuspensionController _suspension;
    private readonly ILogger _log;

    /// <summary>Crée le répartiteur.</summary>
    /// <exception cref="ArgumentNullException">Un argument est <c>null</c>.</exception>
    public RequestDispatcher(
        RuleHandlers ruleHandlers,
        IStateSource state,
        IHealthSource health,
        SuspensionController suspension,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(ruleHandlers);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(suspension);
        ArgumentNullException.ThrowIfNull(log);

        _ruleHandlers = ruleHandlers;
        _state = state;
        _health = health;
        _suspension = suspension;
        _log = log;
    }

    /// <summary>Émis après chaque écriture acceptée, pour déclencher la diffusion.</summary>
    public event EventHandler? StateWritten;

    /// <summary>
    /// Traite une requête et rend la réponse à renvoyer.
    /// </summary>
    /// <param name="request">Requête reçue.</param>
    /// <param name="callerIsElevated">Élévation réelle de l'appelant, calculée côté service.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> est <c>null</c>.</exception>
    public MessageEnvelope Dispatch(MessageEnvelope request, bool callerIsElevated)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (AuthorizationPolicy.GetDenialCode(request.Type, callerIsElevated) is { } denial)
        {
            // Journalise le REFUS, pas le contenu : un appelant non autorise ne doit pas
            // pouvoir faire ecrire ce qu'il veut dans le journal du service.
            _log.Information(
                "Commande {Type} refusée : {Raison}.", request.Type, denial);

            return MessageEnvelope.CreateError(request.Id, denial, DescribeDenial(denial));
        }

        try
        {
            return Route(request);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Un gestionnaire qui leve ne doit pas tuer la connexion ni le service : l'appelant
            // recoit une erreur exploitable, et le detail part au journal, pas sur le tuyau.
            _log.Error(exception, "Défaillance lors du traitement de {Type}.", request.Type);

            return MessageEnvelope.CreateError(
                request.Id,
                ErrorCode.InternalError,
                "Le service a rencontré une défaillance. Consultez le journal.");
        }
    }

    private MessageEnvelope Route(MessageEnvelope request)
    {
        switch (request.Type)
        {
            case MessageTypes.GetState:
                return MessageEnvelope.CreateResult(
                    request.Id,
                    MessageTypes.GetState + MessageTypes.ResultSuffix,
                    _state.GetState());

            case MessageTypes.GetHealth:
                return MessageEnvelope.CreateResult(
                    request.Id,
                    MessageTypes.GetHealth + MessageTypes.ResultSuffix,
                    _health.GetHealth());

            case MessageTypes.UpsertRule:
                return AfterWrite(_ruleHandlers.Upsert(request));

            case MessageTypes.DeleteRule:
                return AfterWrite(_ruleHandlers.Delete(request));

            case MessageTypes.SetRuleEnabled:
                return AfterWrite(_ruleHandlers.SetEnabled(request));

            case MessageTypes.SetSuspended:
                return AfterWrite(Suspend(request));

            default:
                return MessageEnvelope.CreateError(
                    request.Id,
                    ErrorCode.ValidationFailed,
                    $"La commande « {request.Type} » n'est pas prise en charge par cette version " +
                    "du service.");
        }
    }

    /// <summary>
    /// Suspend ou reprend la limitation.
    /// </summary>
    /// <remarks>
    /// Traité ici plutôt que dans un gestionnaire dédié : c'est une bascule d'état sans
    /// persistance ni validation de charge utile au-delà d'un booléen. Lui consacrer une classe
    /// ajouterait une indirection sans rien à y mettre.
    /// </remarks>
    private MessageEnvelope Suspend(MessageEnvelope request)
    {
        SetSuspendedPayload payload;

        try
        {
            payload = Contracts.Serialization.MessageSerializer
                .ReadPayload<SetSuspendedPayload>(request);
        }
        catch (System.Text.Json.JsonException exception)
        {
            return MessageEnvelope.CreateError(
                request.Id, ErrorCode.ValidationFailed, $"Message illisible : {exception.Message}");
        }

        _suspension.Set(payload.Suspended);

        // Journalise le geste : c'est une action de dernier recours, et savoir QUAND elle a eu
        // lieu est la premiere chose qu'on cherche en reconstituant un incident.
        _log.Warning(
            payload.Suspended
                ? "Limitation SUSPENDUE : toutes les limites sont levées, les règles sont conservées."
                : "Limitation reprise : les règles conservées sont réappliquées.");

        return new MessageEnvelope
        {
            Type = request.Type + MessageTypes.ResultSuffix,
            Id = request.Id,
            Ok = true,
        };
    }

    private MessageEnvelope AfterWrite(MessageEnvelope response)
    {
        // Ne diffuse que si l'ecriture a REUSSI. Notifier apres un refus ferait rafraichir
        // toutes les interfaces pour un etat qui n'a pas bouge.
        if (response.Ok == true)
        {
            StateWritten?.Invoke(this, EventArgs.Empty);
        }

        return response;
    }

    private static string DescribeDenial(ErrorCode code) => code switch
    {
        ErrorCode.ElevationRequired =>
            "Cette modification exige des droits administrateur. Relancez l'interface en tant " +
            "qu'administrateur.",

        ErrorCode.ValidationFailed =>
            "Commande inconnue.",

        _ => "Commande refusée.",
    };
}
