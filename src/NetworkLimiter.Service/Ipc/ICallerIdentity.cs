namespace NetworkLimiter.Service.Ipc;

/// <summary>
/// Constate l'élévation réelle de l'appelant d'une connexion.
/// </summary>
/// <remarks>
/// Sépare le <b>mécanisme</b> — impersonation du client sur le tuyau et lecture de son jeton —
/// de la <b>politique</b> portée par <see cref="AuthorizationPolicy"/>. Cette séparation rend
/// la décision d'autorisation testable exhaustivement sans dépendre d'un vrai jeton Windows.
/// </remarks>
public interface ICallerIdentity
{
    /// <summary>
    /// Indique si l'appelant s'exécute avec un jeton administrateur élevé.
    /// </summary>
    /// <remarks>
    /// Sous UAC, le jeton filtré d'un administrateur <b>non élevé</b> ne porte pas le SID
    /// Administrateurs activé : la valeur est donc <c>false</c> pour une interface lancée
    /// normalement, et <c>true</c> pour une interface relancée avec élévation. Le
    /// comportement exigé par FR-034 découle du modèle de sécurité de Windows, sans logique
    /// d'autorisation maison.
    /// </remarks>
    bool IsElevatedAdministrator { get; }
}
