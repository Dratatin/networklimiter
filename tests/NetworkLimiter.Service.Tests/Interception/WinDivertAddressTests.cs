using System.Runtime.InteropServices;
using NetworkLimiter.Service.Interception;

namespace NetworkLimiter.Service.Tests.Interception;

/// <summary>
/// Disposition mémoire et décodage des champs de bits de <c>WINDIVERT_ADDRESS</c>.
/// </summary>
/// <remarks>
/// <para>
/// Les champs de bits du C n'existent pas en C# : ils sont décodés à la main. Une erreur de
/// décalage d'un seul bit ne provoquerait aucun plantage — elle produirait une <b>confusion
/// silencieuse</b> : un paquet entrant pris pour un paquet sortant, de la boucle locale
/// traitée comme du trafic internet, ou un paquet déjà réinjecté remis en forme une seconde
/// fois.
/// </para>
/// <para>
/// Ce sont exactement les défauts qu'aucun test de haut niveau ne rattrape, parce que tout
/// continue de fonctionner « à peu près ». D'où une vérification bit à bit.
/// </para>
/// </remarks>
public sealed class WinDivertAddressTests
{
    // -- Disposition mémoire --------------------------------------------------

    [Fact]
    public void TailleDeLaStructure_EstCelleDeWinDivert()
    {
        // windivert.h : INT64 + 2 UINT32 + union de 64 octets = 80.
        Marshal.SizeOf<WinDivertAddress>().Should().Be(80);
        WinDivertAddress.SizeInBytes.Should().Be(80);
    }

    [Fact]
    public void TailleDesDonneesDeFlux_EstCelleDeWinDivert()
    {
        // WINDIVERT_DATA_FLOW : 2 UINT64 + UINT32 + 8 UINT32 d'adresses + 2 UINT16 + UINT8.
        // La structure est alignee a 8 octets, d'ou un remplissage final.
        Marshal.SizeOf<WinDivertFlowData>().Should().BeGreaterThanOrEqualTo(WinDivertFlowData.SizeInBytes);
    }

    [Fact]
    public void ZoneDUnion_CommenceApresLEnTete()
    {
        // 8 octets d'horodatage + 4 de champs de bits + 4 reserves = 16.
        WinDivertAddress.UnionOffset.Should().Be(16);
    }

    // -- Décodage des champs -------------------------------------------------

    [Theory]
    [InlineData(WinDivertLayer.Network)]
    [InlineData(WinDivertLayer.Flow)]
    [InlineData(WinDivertLayer.Socket)]
    [InlineData(WinDivertLayer.Reflect)]
    public void Couche_EstLueSurLesHuitPremiersBits(WinDivertLayer layer)
    {
        uint bits = WinDivertAddress.PackBits(layer, WinDivertEvent.NetworkPacket);

        WinDivertAddress.FromRaw(0, bits).Layer.Should().Be(layer);
    }

    [Theory]
    [InlineData(WinDivertEvent.NetworkPacket)]
    [InlineData(WinDivertEvent.FlowEstablished)]
    [InlineData(WinDivertEvent.FlowDeleted)]
    [InlineData(WinDivertEvent.ReflectClose)]
    public void Evenement_EstLuSurLesHuitBitsSuivants(WinDivertEvent @event)
    {
        uint bits = WinDivertAddress.PackBits(WinDivertLayer.Flow, @event);

        WinDivertAddress.FromRaw(0, bits).Event.Should().Be(@event);
    }

    [Fact]
    public void CoucheEtEvenement_NeSeChevauchentPas()
    {
        // Le piege classique : lire l'evenement sans decalage, ou la couche sur 16 bits.
        uint bits = WinDivertAddress.PackBits(WinDivertLayer.Reflect, WinDivertEvent.ReflectClose);
        WinDivertAddress address = WinDivertAddress.FromRaw(0, bits);

        address.Layer.Should().Be(WinDivertLayer.Reflect);
        address.Event.Should().Be(WinDivertEvent.ReflectClose);
    }

    // -- Drapeaux, un par un --------------------------------------------------

    [Fact]
    public void Sniffed_EstLeBit16()
    {
        WinDivertAddress address = WinDivertAddress.FromRaw(0, 1u << 16);

        address.Sniffed.Should().BeTrue();
        address.Outbound.Should().BeFalse();
        address.Loopback.Should().BeFalse();
        address.Impostor.Should().BeFalse();
        address.IPv6.Should().BeFalse();
    }

    [Fact]
    public void Outbound_EstLeBit17()
    {
        WinDivertAddress address = WinDivertAddress.FromRaw(0, 1u << 17);

        address.Outbound.Should().BeTrue();
        address.Sniffed.Should().BeFalse();
        address.Loopback.Should().BeFalse();
    }

    [Fact]
    public void Loopback_EstLeBit18()
    {
        // Un decalage ici ferait traiter du trafic internet comme de la boucle locale,
        // donc l'exempterait de tout plafond sans que rien ne le signale.
        WinDivertAddress address = WinDivertAddress.FromRaw(0, 1u << 18);

        address.Loopback.Should().BeTrue();
        address.Outbound.Should().BeFalse();
        address.Impostor.Should().BeFalse();
    }

    [Fact]
    public void Impostor_EstLeBit19()
    {
        WinDivertAddress address = WinDivertAddress.FromRaw(0, 1u << 19);

        address.Impostor.Should().BeTrue();
        address.Loopback.Should().BeFalse();
        address.IPv6.Should().BeFalse();
    }

    [Fact]
    public void IPv6_EstLeBit20()
    {
        WinDivertAddress address = WinDivertAddress.FromRaw(0, 1u << 20);

        address.IPv6.Should().BeTrue();
        address.Impostor.Should().BeFalse();
        address.IPChecksumValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    public void SommesDeControle_SontLesBits21A23(int bit)
    {
        WinDivertAddress address = WinDivertAddress.FromRaw(0, 1u << bit);

        bool[] checksums = [address.IPChecksumValid, address.TcpChecksumValid, address.UdpChecksumValid];

        checksums.Count(set => set).Should().Be(1, "un seul indicateur de somme de contrôle doit être levé");
        checksums[bit - 21].Should().BeTrue();
    }

    // -- Combinaisons ---------------------------------------------------------

    [Fact]
    public void DrapeauxCombines_SontTousLusCorrectement()
    {
        uint bits = WinDivertAddress.PackBits(
            WinDivertLayer.Network, WinDivertEvent.NetworkPacket,
            outbound: true, ipv6: true);

        WinDivertAddress address = WinDivertAddress.FromRaw(1234, bits);

        address.Layer.Should().Be(WinDivertLayer.Network);
        address.Event.Should().Be(WinDivertEvent.NetworkPacket);
        address.Outbound.Should().BeTrue();
        address.IPv6.Should().BeTrue();
        address.Loopback.Should().BeFalse();
        address.Impostor.Should().BeFalse();
        address.Timestamp.Should().Be(1234);
    }

    [Fact]
    public void AucunDrapeau_SignifieEntrantIPv4NonBoucleLocale()
    {
        // Cas le plus frequent : un paquet entrant IPv4 ordinaire. Tous les indicateurs a
        // faux, ce qui doit se lire correctement et non produire de valeur par defaut fausse.
        WinDivertAddress address = WinDivertAddress.FromRaw(
            0, WinDivertAddress.PackBits(WinDivertLayer.Network, WinDivertEvent.NetworkPacket));

        address.Outbound.Should().BeFalse();
        address.IPv6.Should().BeFalse();
        address.Loopback.Should().BeFalse();
        address.Impostor.Should().BeFalse();
        address.Sniffed.Should().BeFalse();
    }

    // -- Lecture de l'union FLOW ----------------------------------------------

    [Fact]
    public void LesDonneesDeFlux_SontLuesDepuisLaZoneDUnion()
    {
        // L'union du C n'a pas d'equivalent en C# : la zone est reinterpretee a partir de sa
        // representation binaire. Une erreur de decalage produirait des identifiants de
        // processus inventes, donc du trafic attribue a des applications au hasard — sans
        // qu'aucun plantage ne le signale.
        var flow = new WinDivertFlowData
        {
            EndpointId = 0x1122334455667788,
            ParentEndpointId = 0x8877665544332211,
            ProcessId = 4242,
            LocalAddr0 = 0x0A000001,
            RemoteAddr0 = 0x5DB8D822,
            LocalPort = 50000,
            RemotePort = 443,
            Protocol = 6,
        };

        uint bits = WinDivertAddress.PackBits(WinDivertLayer.Flow, WinDivertEvent.FlowEstablished);
        WinDivertAddress address = WinDivertAddress.FromFlowData(bits, flow);

        WinDivertFlowData read = address.AsFlowData();

        read.EndpointId.Should().Be(flow.EndpointId);
        read.ParentEndpointId.Should().Be(flow.ParentEndpointId);
        read.ProcessId.Should().Be(4242u);
        read.LocalAddr0.Should().Be(flow.LocalAddr0);
        read.RemoteAddr0.Should().Be(flow.RemoteAddr0);
        read.LocalPort.Should().Be(50000);
        read.RemotePort.Should().Be(443);
        read.Protocol.Should().Be(6);
    }

    [Fact]
    public void LesChampsDEnTete_SurviventALEcritureDeLUnion()
    {
        // Verrouille le decalage : ecrire l'union ne doit pas ecraser l'horodatage ni les
        // champs de bits, qui la precedent.
        uint bits = WinDivertAddress.PackBits(
            WinDivertLayer.Flow, WinDivertEvent.FlowDeleted, sniffed: true);

        WinDivertAddress address = WinDivertAddress.FromFlowData(
            bits, new WinDivertFlowData { ProcessId = 7 });

        address.Layer.Should().Be(WinDivertLayer.Flow);
        address.Event.Should().Be(WinDivertEvent.FlowDeleted);
        address.Sniffed.Should().BeTrue();
        address.AsFlowData().ProcessId.Should().Be(7u);
    }

    [Fact]
    public void ToutesLesValeursDeCoucheEtEvenement_SontDistinctes()
    {
        // Verrouille l'absence de collision entre valeurs d'enumeration, qui produirait des
        // aiguillages silencieusement faux dans la boucle d'interception.
        Enum.GetValues<WinDivertLayer>().Select(l => (int)l).Should().OnlyHaveUniqueItems();
        Enum.GetValues<WinDivertEvent>().Select(e => (int)e).Should().OnlyHaveUniqueItems();
    }
}
