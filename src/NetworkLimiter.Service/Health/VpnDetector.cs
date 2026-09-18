using System.Net.NetworkInformation;
using System.Runtime.Versioning;

namespace NetworkLimiter.Service.Health;

/// <summary>Un adaptateur réseau, réduit à ce dont la détection a besoin.</summary>
/// <param name="Name">Nom de la connexion, tel que Windows l'affiche.</param>
/// <param name="Description">Description du pilote, souvent plus révélatrice que le nom.</param>
/// <param name="Type">Type déclaré par Windows.</param>
/// <param name="IsOperational">L'adaptateur est actif.</param>
public sealed record NetworkAdapter(
    string Name,
    string Description,
    NetworkInterfaceType Type,
    bool IsOperational);

/// <summary>Énumère les adaptateurs de la machine.</summary>
/// <remarks>
/// Abstraction nécessaire : la détection repose sur des motifs de noms, et la vérifier sur la
/// machine de développement prouverait seulement qu'elle marche là. Les configurations qui
/// comptent — TAP, WireGuard, WAN Miniport — ne s'installent pas dans un test.
/// </remarks>
public interface INetworkAdapterSource
{
    /// <summary>Rend les adaptateurs présents.</summary>
    IReadOnlyList<NetworkAdapter> GetAdapters();
}

/// <summary>Source réelle, adossée à <see cref="NetworkInterface"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class SystemNetworkAdapterSource : INetworkAdapterSource
{
    /// <inheritdoc />
    public IReadOnlyList<NetworkAdapter> GetAdapters()
    {
        try
        {
            return
            [
                .. NetworkInterface.GetAllNetworkInterfaces().Select(adapter => new NetworkAdapter(
                    adapter.Name,
                    adapter.Description,
                    adapter.NetworkInterfaceType,
                    adapter.OperationalStatus == OperationalStatus.Up)),
            ];
        }
        catch (NetworkInformationException)
        {
            // L'enumeration echoue parfois pendant un changement d'adaptateur. Rendre une
            // liste vide vaut mieux que propager : l'absence d'avertissement est moins grave
            // qu'un service qui tombe sur un incident transitoire (principe IV).
            return [];
        }
    }
}

/// <summary>Résultat de la détection.</summary>
/// <param name="Detected">Un adaptateur ressemblant à un VPN est actif.</param>
/// <param name="AdapterNames">Noms des adaptateurs suspects, pour que l'utilisateur les reconnaisse.</param>
public sealed record VpnStatus(bool Detected, IReadOnlyList<string> AdapterNames)
{
    /// <summary>Aucun VPN détecté.</summary>
    public static VpnStatus None { get; } = new(false, []);

    /// <summary>
    /// Avertissement affichable, ou <c>null</c> si rien n'a été détecté.
    /// </summary>
    /// <remarks>
    /// Le texte dit ce qui est <b>incertain</b>, pas ce qui est faux. La détection est
    /// heuristique : affirmer « vos limites ne fonctionnent pas » serait souvent inexact, et
    /// affirmer le contraire le serait tout autant. FR-037 et FR-040c demandent d'informer,
    /// pas de garantir.
    /// </remarks>
    public string? Warning => Detected
        ? $"Un VPN semble actif ({string.Join(", ", AdapterNames)}). Selon sa configuration, " +
          "le trafic tunnelisé peut échapper aux limites, et la distinction entre trafic " +
          "internet et trafic local peut devenir inexacte."
        : null;
}

/// <summary>
/// Détecte la présence probable d'un VPN (FR-037, FR-040c).
/// </summary>
/// <remarks>
/// <para>
/// <b>Détecter et avertir, jamais garantir.</b> C'est une heuristique par construction : rien,
/// dans Windows, ne distingue de façon fiable un tunnel VPN d'un adaptateur virtuel de machine
/// virtuelle ou d'un pilote de capture. Prétendre trancher produirait des affirmations fausses
/// dans les deux sens — « vos limites ne marchent pas » alors qu'elles marchent, ou l'inverse,
/// bien pire.
/// </para>
/// <para>
/// La conséquence réelle est double. Le trafic tunnelisé peut ne jamais traverser la couche où
/// nous intervenons, et l'adresse distante observée devient celle du tunnel — donc souvent une
/// adresse privée, que le classificateur rangerait à tort dans le trafic local et exclurait des
/// plafonds.
/// </para>
/// </remarks>
public static class VpnDetector
{
    /// <summary>
    /// Motifs de description trahissant un tunnel.
    /// </summary>
    /// <remarks>
    /// Sur la description du pilote plutôt que sur le nom de connexion : ce dernier est
    /// renommable par l'utilisateur, et le premier vient du pilote installé. Une liste de
    /// motifs vieillit — c'est assumé, et c'est pourquoi le message avertit au lieu d'affirmer.
    /// </remarks>
    private static readonly string[] Signatures =
    [
        "tap-windows",
        "tap-win32",
        "wireguard",
        "openvpn",
        "anyconnect",
        "globalprotect",
        "fortinet",
        "pulse secure",
        "sonicwall",
        "checkpoint",
        "zerotier",
        "tailscale",
        "nordlynx",
        "proton",
        "mullvad",
        "expressvpn",
        "wan miniport (ikev2)",
        "wan miniport (l2tp)",
        "wan miniport (pptp)",
        "wan miniport (sstp)",
    ];

    /// <summary>Examine un ensemble d'adaptateurs.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="adapters"/> est <c>null</c>.</exception>
    public static VpnStatus Detect(IReadOnlyList<NetworkAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        // Seuls les adaptateurs ACTIFS comptent. Windows conserve des WAN Miniport installes
        // en permanence, tunnels inactifs compris : les signaler ferait afficher un
        // avertissement de VPN a peu pres a tout le monde, en permanence, et plus personne ne
        // le lirait le jour ou il serait vrai.
        string[] suspects =
        [
            .. adapters
                .Where(adapter => adapter.IsOperational && LooksLikeTunnel(adapter))
                .Select(adapter => adapter.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
        ];

        return suspects.Length == 0 ? VpnStatus.None : new VpnStatus(true, suspects);
    }

    /// <summary>Examine la machine courante.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> est <c>null</c>.</exception>
    public static VpnStatus Detect(INetworkAdapterSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Detect(source.GetAdapters());
    }

    private static bool LooksLikeTunnel(NetworkAdapter adapter)
    {
        // Le type declare par Windows est le signal le plus sur quand il est present : un
        // adaptateur qui s'annonce Tunnel ou Ppp en est un, quel que soit son nom.
        if (adapter.Type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
        {
            return true;
        }

        return Array.Exists(
            Signatures,
            signature => adapter.Description.Contains(signature, StringComparison.OrdinalIgnoreCase));
    }
}
