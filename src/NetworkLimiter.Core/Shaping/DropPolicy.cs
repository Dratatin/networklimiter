namespace NetworkLimiter.Core.Shaping;

/// <summary>Sens d'un paquet.</summary>
public enum PacketDirection
{
    /// <summary>Émis par la machine.</summary>
    Outbound,

    /// <summary>Reçu par la machine.</summary>
    Inbound,
}

/// <summary>Protocole de transport.</summary>
public enum TransportProtocol
{
    /// <summary>TCP : dispose d'un contrôle de congestion.</summary>
    Tcp,

    /// <summary>UDP : aucun contrôle de congestion.</summary>
    Udp,

    /// <summary>Autre protocole.</summary>
    Other,
}

/// <summary>Traitement retenu pour un paquet en excès.</summary>
public enum ShapingAction
{
    /// <summary>Temporiser : le paquet part plus tard, rien n'est perdu.</summary>
    Delay,

    /// <summary>Rejeter : seule façon de tenir le plafond, au prix d'une perte.</summary>
    Drop,
}

/// <summary>
/// Décide si un paquet en excès doit être temporisé ou rejeté.
/// </summary>
/// <remarks>
/// <para>
/// Traduction directe de FR-003a et FR-003c. La règle tient en une phrase : <b>on ne rejette
/// jamais quand la temporisation suffit</b>.
/// </para>
/// <para>
/// En <b>montant</b>, retarder un paquet contrôle le débit directement : la machine décide
/// quand elle émet. En <b>descendant</b>, le paquet est déjà arrivé — le retenir ne libère pas
/// la ligne. Cela fonctionne quand même pour TCP, parce que retarder le flux fait refluer
/// l'émetteur par le contrôle de congestion. Mais UDP n'a aucune boucle de rétroaction : le
/// seul moyen d'y tenir un plafond est de jeter des données.
/// </para>
/// <para>
/// C'est pourquoi FR-003b impose de <b>signaler</b> le rejet et sa proportion dans l'interface.
/// Un utilisateur dont la visioconférence se dégrade doit pouvoir faire le lien avec le plafond
/// qu'il a lui-même posé, plutôt que de conclure que l'outil casse son réseau au hasard.
/// </para>
/// </remarks>
public static class DropPolicy
{
    /// <summary>Indique si un paquet peut être tenu par simple temporisation.</summary>
    public static bool IsDelayable(PacketDirection direction, TransportProtocol protocol) =>
        direction == PacketDirection.Outbound || protocol == TransportProtocol.Tcp;

    /// <summary>
    /// Rend le traitement d'un paquet qui ne tient pas dans le plafond.
    /// </summary>
    /// <param name="direction">Sens du paquet.</param>
    /// <param name="protocol">Protocole de transport.</param>
    /// <param name="queueIsFull">La file de retard est pleine.</param>
    /// <remarks>
    /// Un paquet temporisable n'est rejeté que si la file déborde — et ce rejet-là est un
    /// dernier recours mesuré, pas une politique.
    /// </remarks>
    public static ShapingAction Decide(
        PacketDirection direction,
        TransportProtocol protocol,
        bool queueIsFull)
    {
        if (!IsDelayable(direction, protocol))
        {
            return ShapingAction.Drop;
        }

        return queueIsFull ? ShapingAction.Drop : ShapingAction.Delay;
    }
}
