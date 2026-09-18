using System.Net.NetworkInformation;
using FluentAssertions;
using NetworkLimiter.Service.Health;
using Xunit;

namespace NetworkLimiter.Service.Tests.Health;

/// <summary>
/// Détection de VPN (FR-037, FR-040c).
/// </summary>
/// <remarks>
/// <para>
/// Le cas le plus important n'est pas la détection : c'est le <b>faux positif</b>. Windows
/// installe des WAN Miniport en permanence, et beaucoup de machines portent des adaptateurs
/// virtuels de virtualisation. Les signaler ferait afficher un avertissement de VPN à peu près
/// à tout le monde, tout le temps — et plus personne ne le lirait le jour où il serait vrai.
/// </para>
/// <para>
/// C'est pourquoi ces tests insistent autant sur ce qui ne doit <b>pas</b> déclencher
/// d'avertissement que sur ce qui doit le déclencher.
/// </para>
/// </remarks>
public sealed class VpnDetectorTests
{
    [Fact]
    public void UneMachineOrdinaire_NeDeclencheAucunAvertissement()
    {
        VpnStatus status = VpnDetector.Detect(
        [
            Adapter("Ethernet", "Realtek PCIe GbE Family Controller", NetworkInterfaceType.Ethernet),
            Adapter("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", NetworkInterfaceType.Wireless80211),
            Adapter("Loopback", "Software Loopback Interface 1", NetworkInterfaceType.Loopback),
        ]);

        status.Detected.Should().BeFalse();
        status.Warning.Should().BeNull();
    }

    [Fact]
    public void LesWanMiniportInactifs_NeDeclenchentRien()
    {
        // Windows les installe TOUS, en permanence, sur toutes les machines. C'est le faux
        // positif le plus probable, et celui qui rendrait l'avertissement inaudible.
        VpnStatus status = VpnDetector.Detect(
        [
            Adapter("WAN Miniport (IKEv2)", "WAN Miniport (IKEv2)", NetworkInterfaceType.Ppp, operational: false),
            Adapter("WAN Miniport (L2TP)", "WAN Miniport (L2TP)", NetworkInterfaceType.Ppp, operational: false),
            Adapter("WAN Miniport (SSTP)", "WAN Miniport (SSTP)", NetworkInterfaceType.Ppp, operational: false),
            Adapter("Ethernet", "Realtek PCIe GbE", NetworkInterfaceType.Ethernet),
        ]);

        status.Detected.Should().BeFalse("ils sont installés partout et inactifs la plupart du temps");
    }

    [Theory]
    [InlineData("TAP-Windows Adapter V9")]
    [InlineData("WireGuard Tunnel")]
    [InlineData("OpenVPN Wintun")]
    [InlineData("Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter")]
    [InlineData("Tailscale Tunnel")]
    [InlineData("Mullvad Tunnel")]
    public void UnAdaptateurDeTunnelActif_DeclencheUnAvertissement(string description)
    {
        VpnStatus status = VpnDetector.Detect(
        [
            Adapter("Ethernet", "Realtek PCIe GbE", NetworkInterfaceType.Ethernet),
            Adapter("VPN", description, NetworkInterfaceType.Ethernet),
        ]);

        status.Detected.Should().BeTrue();
        status.AdapterNames.Should().Contain("VPN");
    }

    [Fact]
    public void UnTypeTunnelDeclare_SuffitSansMotifDeNom()
    {
        // Un adaptateur qui s'annonce Tunnel en est un, quel que soit son nom : c'est le
        // signal le plus sur, et il n'a pas besoin de figurer dans une liste qui vieillit.
        VpnStatus status = VpnDetector.Detect(
        [
            Adapter("Connexion 42", "Pilote maison sans nom reconnaissable", NetworkInterfaceType.Tunnel),
        ]);

        status.Detected.Should().BeTrue();
    }

    [Fact]
    public void UnTunnelInactif_NeDeclenchePas()
    {
        VpnStatus status = VpnDetector.Detect(
        [
            Adapter("VPN", "WireGuard Tunnel", NetworkInterfaceType.Tunnel, operational: false),
        ]);

        status.Detected.Should().BeFalse("un tunnel installé mais éteint ne change rien au trafic");
    }

    [Fact]
    public void LAvertissement_DecritCeQuiEstIncertain_JamaisCeQuiEstFaux()
    {
        VpnStatus status = VpnDetector.Detect(
        [
            Adapter("Mon VPN", "WireGuard Tunnel", NetworkInterfaceType.Tunnel),
        ]);

        // La detection est heuristique. Affirmer « vos limites ne fonctionnent pas » serait
        // souvent inexact ; affirmer le contraire le serait tout autant, et bien plus grave.
        status.Warning.Should().Contain("peut");
        status.Warning.Should().Contain("Mon VPN", "l'utilisateur doit reconnaître de quoi on parle");
        status.Warning.Should().NotContain("ne fonctionnent pas");
    }

    [Fact]
    public void PlusieursTunnels_SontTousNommes_DansUnOrdreStable()
    {
        VpnStatus status = VpnDetector.Detect(
        [
            Adapter("Zeta", "WireGuard Tunnel", NetworkInterfaceType.Tunnel),
            Adapter("Alpha", "TAP-Windows Adapter V9", NetworkInterfaceType.Ethernet),
        ]);

        // Ordre stable : un avertissement dont le texte change a chaque rafraichissement
        // donne l'impression que la situation bouge alors que rien n'a change.
        status.AdapterNames.Should().Equal("Alpha", "Zeta");
    }

    [Fact]
    public void AucunAdaptateur_NeDeclencheRien()
    {
        // L'enumeration echoue parfois pendant un changement d'adaptateur, et la source rend
        // alors une liste vide. Cela ne doit surtout pas passer pour une detection.
        VpnDetector.Detect([]).Detected.Should().BeFalse();
    }

    private static NetworkAdapter Adapter(
        string name,
        string description,
        NetworkInterfaceType type,
        bool operational = true) => new(name, description, type, operational);
}
