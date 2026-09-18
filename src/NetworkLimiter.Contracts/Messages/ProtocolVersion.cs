namespace NetworkLimiter.Contracts.Messages;

/// <summary>
/// Version du contrat IPC.
/// </summary>
/// <remarks>
/// Toute modification du contrat impose d'incrémenter cette constante. Le service refuse
/// alors proprement une interface de version différente, avec un message actionnable, plutôt
/// que d'échouer silencieusement plus loin (contracts/ipc-protocol.md, « Compatibilité de
/// version »).
/// </remarks>
public static class ProtocolVersion
{
    /// <summary>Version courante du protocole.</summary>
    public const int Current = 1;
}
