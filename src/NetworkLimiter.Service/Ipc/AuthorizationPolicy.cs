using NetworkLimiter.Contracts.Messages;

namespace NetworkLimiter.Service.Ipc;

/// <summary>
/// Décide si un message est autorisé, au vu de son type et de l'élévation de l'appelant.
/// </summary>
/// <remarks>
/// <para>
/// Deuxième couche de contrôle d'accès du contrat IPC, et traduction directe de FR-034 :
/// consultation libre, modification soumise à élévation.
/// </para>
/// <para>
/// La classe est <b>statique et sans état</b>, délibérément. Le contrat impose que le service
/// n'enregistre jamais qu'un appelant a été autorisé une fois : chaque message est évalué
/// seul. Une politique sans champ rend cette règle structurelle plutôt que disciplinaire —
/// on ne peut pas mémoriser une décision dans un objet qui n'a rien où la ranger.
/// </para>
/// <para>
/// La politique est séparée du mécanisme qui constate l'élévation
/// (<see cref="ICallerIdentity"/>) pour que la décision soit testable exhaustivement, sur
/// tous les messages de la liste blanche, sans dépendre d'un vrai jeton Windows.
/// </para>
/// </remarks>
public static class AuthorizationPolicy
{
    /// <summary>Indique si un message peut être traité.</summary>
    /// <param name="messageType">Type du message reçu.</param>
    /// <param name="callerIsElevated">Élévation constatée sur le jeton réel de l'appelant.</param>
    public static bool IsAuthorized(string? messageType, bool callerIsElevated)
    {
        if (!MessageTypes.IsKnownRequest(messageType))
        {
            // Refuse meme a un appelant eleve : une faute de frappe ou un message d'une
            // version future ne doit pas tomber dans un chemin par defaut.
            return false;
        }

        return !MessageTypes.RequiresElevation(messageType) || callerIsElevated;
    }

    /// <summary>
    /// Rend le code d'erreur à renvoyer, ou <c>null</c> si le message est autorisé.
    /// </summary>
    /// <remarks>
    /// Le code doit être précis : l'interface s'appuie sur
    /// <see cref="ErrorCode.ElevationRequired"/> pour proposer la relance élevée (FR-034a).
    /// Un code générique la laisserait sans action à proposer à l'utilisateur.
    /// </remarks>
    public static ErrorCode? GetDenialCode(string? messageType, bool callerIsElevated)
    {
        if (!MessageTypes.IsKnownRequest(messageType))
        {
            return ErrorCode.ValidationFailed;
        }

        if (MessageTypes.RequiresElevation(messageType) && !callerIsElevated)
        {
            return ErrorCode.ElevationRequired;
        }

        return null;
    }
}
