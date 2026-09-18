using System.Runtime.InteropServices;

namespace NetworkLimiter.Service.Interception;

/// <summary>Type d'événement remonté par WinDivert.</summary>
public enum WinDivertEvent : byte
{
    /// <summary>Paquet sur la couche réseau.</summary>
    NetworkPacket = 0,

    /// <summary>Flux établi.</summary>
    FlowEstablished = 1,

    /// <summary>Flux supprimé.</summary>
    FlowDeleted = 2,

    /// <summary>Liaison de socket.</summary>
    SocketBind = 3,

    /// <summary>Connexion de socket.</summary>
    SocketConnect = 4,

    /// <summary>Mise en écoute.</summary>
    SocketListen = 5,

    /// <summary>Acceptation de connexion.</summary>
    SocketAccept = 6,

    /// <summary>Fermeture de socket.</summary>
    SocketClose = 7,

    /// <summary>Ouverture d'un handle WinDivert.</summary>
    ReflectOpen = 8,

    /// <summary>Fermeture d'un handle WinDivert.</summary>
    ReflectClose = 9,
}

/// <summary>
/// Métadonnées accompagnant chaque paquet ou événement WinDivert.
/// </summary>
/// <remarks>
/// <para>
/// Reproduit <c>WINDIVERT_ADDRESS</c> de <c>windivert.h</c> : 8 octets d'horodatage, 4 octets
/// de champs de bits, 4 octets réservés, puis 64 octets d'union propre à la couche. Taille
/// totale <b>80 octets</b>, fixée explicitement.
/// </para>
/// <para>
/// Les champs de bits n'existent pas en C# : ils sont décodés à la main depuis un
/// <see cref="uint"/>. Une erreur de décalage d'un seul bit produirait ici une confusion
/// silencieuse — un paquet entrant pris pour un paquet sortant, ou de la boucle locale traitée
/// comme du trafic internet. C'est pourquoi la disposition et chaque bit font l'objet de tests.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = 80)]
public struct WinDivertAddress
{
    /// <summary>Taille de la structure, en octets, telle que définie par <c>windivert.h</c>.</summary>
    public const int SizeInBytes = 80;

    /// <summary>Décalage de la zone d'union propre à la couche.</summary>
    internal const int UnionOffset = 16;

    private long _timestamp;
    private uint _bits;
    // Champs de remplissage : jamais ecrits par du code manage, uniquement par le
    // marshaling natif. Declares readonly pour rendre cette intention explicite.
    private readonly uint _reserved2;

    // 64 octets d'union. Declares en champs explicites plutot qu'en tampon fixe pour eviter
    // le code non verifiable ; la couche Flow n'utilise que les 44 premiers.
    private readonly ulong _union0;
    private readonly ulong _union1;
    private readonly ulong _union2;
    private readonly ulong _union3;
    private readonly ulong _union4;
    private readonly ulong _union5;
    private readonly ulong _union6;
    private readonly ulong _union7;

    /// <summary>Horodatage du paquet, en ticks de compteur de performance.</summary>
    public readonly long Timestamp => _timestamp;

    /// <summary>Couche d'origine.</summary>
    public readonly WinDivertLayer Layer => (WinDivertLayer)(_bits & 0xFF);

    /// <summary>Type d'événement.</summary>
    public readonly WinDivertEvent Event => (WinDivertEvent)((_bits >> 8) & 0xFF);

    /// <summary>Le paquet a été copié et non détourné.</summary>
    public readonly bool Sniffed => (_bits & (1u << 16)) != 0;

    /// <summary>Le paquet est sortant.</summary>
    public readonly bool Outbound => (_bits & (1u << 17)) != 0;

    /// <summary>
    /// Le paquet appartient à la boucle locale.
    /// </summary>
    /// <remarks>
    /// Fourni directement par WinDivert, ce qui évite de traiter la boucle locale par plages
    /// d'adresses (FR-040a).
    /// </remarks>
    public readonly bool Loopback => (_bits & (1u << 18)) != 0;

    /// <summary>
    /// Le paquet a été réinjecté par un autre handle WinDivert.
    /// </summary>
    /// <remarks>
    /// Important pour ne pas mettre en forme deux fois le même paquet si un autre outil
    /// utilisant WinDivert tourne sur la machine.
    /// </remarks>
    public readonly bool Impostor => (_bits & (1u << 19)) != 0;

    /// <summary>Le paquet est en IPv6.</summary>
    public readonly bool IPv6 => (_bits & (1u << 20)) != 0;

    /// <summary>La somme de contrôle IPv4 est valide.</summary>
    public readonly bool IPChecksumValid => (_bits & (1u << 21)) != 0;

    /// <summary>La somme de contrôle TCP est valide.</summary>
    public readonly bool TcpChecksumValid => (_bits & (1u << 22)) != 0;

    /// <summary>La somme de contrôle UDP est valide.</summary>
    public readonly bool UdpChecksumValid => (_bits & (1u << 23)) != 0;

    /// <summary>Compose la valeur brute des champs de bits. Réservé aux tests et à la construction.</summary>
    internal static uint PackBits(
        WinDivertLayer layer,
        WinDivertEvent @event,
        bool sniffed = false,
        bool outbound = false,
        bool loopback = false,
        bool impostor = false,
        bool ipv6 = false)
    {
        uint bits = (uint)layer & 0xFF;
        bits |= ((uint)@event & 0xFF) << 8;

        if (sniffed) { bits |= 1u << 16; }
        if (outbound) { bits |= 1u << 17; }
        if (loopback) { bits |= 1u << 18; }
        if (impostor) { bits |= 1u << 19; }
        if (ipv6) { bits |= 1u << 20; }

        return bits;
    }

    /// <summary>Construit une adresse à partir de champs bruts. Réservé aux tests.</summary>
    internal static WinDivertAddress FromRaw(long timestamp, uint bits) =>
        new() { _timestamp = timestamp, _bits = bits };
}

/// <summary>
/// Données de la couche <c>FLOW</c>, superposées à la zone d'union de
/// <see cref="WinDivertAddress"/>.
/// </summary>
/// <remarks>
/// C'est la seule structure qui porte un identifiant de processus avec le cycle de vie complet
/// du flux — la couche <c>NETWORK</c> n'en porte aucun. Les adresses sont stockées sur quatre
/// entiers 32 bits, en IPv4 comme en IPv6 ; en IPv4, seul le premier est significatif.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct WinDivertFlowData
{
    /// <summary>Taille de la structure, en octets.</summary>
    public const int SizeInBytes = 44;

    /// <summary>Identifiant du point de terminaison.</summary>
    public ulong EndpointId;

    /// <summary>Identifiant du point de terminaison parent.</summary>
    public ulong ParentEndpointId;

    /// <summary>Identifiant du processus propriétaire du flux.</summary>
    public uint ProcessId;

    /// <summary>Adresse locale, quatre mots de 32 bits.</summary>
    public uint LocalAddr0;

    /// <summary>Adresse locale, deuxième mot.</summary>
    public uint LocalAddr1;

    /// <summary>Adresse locale, troisième mot.</summary>
    public uint LocalAddr2;

    /// <summary>Adresse locale, quatrième mot.</summary>
    public uint LocalAddr3;

    /// <summary>Adresse distante, premier mot.</summary>
    public uint RemoteAddr0;

    /// <summary>Adresse distante, deuxième mot.</summary>
    public uint RemoteAddr1;

    /// <summary>Adresse distante, troisième mot.</summary>
    public uint RemoteAddr2;

    /// <summary>Adresse distante, quatrième mot.</summary>
    public uint RemoteAddr3;

    /// <summary>Port local.</summary>
    public ushort LocalPort;

    /// <summary>Port distant.</summary>
    public ushort RemotePort;

    /// <summary>Numéro de protocole IP.</summary>
    public byte Protocol;
}
