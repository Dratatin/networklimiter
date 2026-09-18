using FluentAssertions;
using NetworkLimiter.Service.Interception;
using Xunit;

namespace NetworkLimiter.Service.Tests.Interception;

/// <summary>
/// Vérifie que les filtres de production <b>correspondent</b> à ce qu'ils doivent capter.
/// </summary>
/// <remarks>
/// <para>
/// Ces tests existent à cause d'une panne qui a coûté deux sessions de diagnostic. Le filtre de
/// la couche flux était <c>(ip or ipv6) and (tcp or udp)</c> : syntaxiquement irréprochable,
/// accepté par le compilateur de WinDivert, handle ouvert sans erreur — et <b>aucun événement
/// pendant quatre-vingt-dix secondes</b>. À la couche flux il n'y a pas de paquet, donc les
/// prédicats d'en-tête <c>ip</c> et <c>ipv6</c> y sont toujours faux, et le filtre exigeait une
/// condition impossible.
/// </para>
/// <para>
/// La leçon tient en une phrase : <b>valider un filtre ne prouve pas qu'il correspond</b>.
/// Le test de validation existait déjà et passait. Celui-ci évalue le filtre contre un
/// événement, ce que <c>WinDivertHelperEvalFilter</c> fait en mode utilisateur — sans pilote,
/// sans privilèges, donc dans n'importe quelle exécution de la suite.
/// </para>
/// </remarks>
public sealed class FilterMatchingTests
{
    private const byte Tcp = 6;
    private const byte Udp = 17;

    [Theory]
    [InlineData(Tcp, false)]
    [InlineData(Tcp, true)]
    [InlineData(Udp, false)]
    [InlineData(Udp, true)]
    public void LeFiltreDeFlux_CorrespondAuxFluxTcpEtUdp_DesDeuxFamilles(byte protocol, bool ipv6)
    {
        WinDivertAddress address = BuildFlowEvent(protocol, ipv6);

        FilterBuilder.Matches(FilterBuilder.FlowFilter, address).Should().BeTrue(
            "sans correspondance, la couche flux reste muette : aucun paquet n'est attribué " +
            "à un processus et aucune règle ne s'applique, sans qu'aucune erreur ne le signale");
    }

    [Fact]
    public void LeFiltreDeFlux_NExigeAucunPredicatDEnTete()
    {
        // Le piege exact, fige en test : ces predicats portent sur un en-tete de paquet, et il
        // n'y a pas de paquet a cette couche. Le filtre compile, s'ouvre, et ne capte rien.
        WinDivertAddress address = BuildFlowEvent(Tcp, ipv6: false);

        FilterBuilder.Matches("ip or ipv6", address).Should().BeFalse(
            "c'est ce qui rendait l'ancien filtre de flux impossible à satisfaire");

        FilterBuilder.Matches("(ip or ipv6) and (tcp or udp)", address).Should().BeFalse(
            "l'ancien filtre de production ne correspondait à aucun événement de flux");
    }

    [Fact]
    public void LeFiltreDeFlux_NeDependPasDuSensNiDeLaBoucleLocale()
    {
        // La table de flux doit connaitre les deux sens : une recherche orientee « local /
        // distant » echouerait dans un sens si la couche n'en voyait qu'un.
        foreach (bool outbound in new[] { true, false })
        {
            foreach (bool loopback in new[] { true, false })
            {
                WinDivertAddress address = BuildFlowEvent(Tcp, ipv6: false, outbound, loopback);

                FilterBuilder.Matches(FilterBuilder.FlowFilter, address).Should().BeTrue(
                    $"sortant={outbound}, boucle={loopback}");
            }
        }
    }

    [Fact]
    public void LesDeuxFiltresDeProduction_SontAcceptesParWinDivert()
    {
        // Garde la validation syntaxique a cote de la verification de correspondance : les
        // deux sont necessaires, et aucune ne remplace l'autre.
        FilterBuilder.Validate(FilterBuilder.FlowFilter, WinDivertLayer.Flow);
        FilterBuilder.Validate(FilterBuilder.NetworkFilter, WinDivertLayer.Network);
    }

    private static WinDivertAddress BuildFlowEvent(
        byte protocol,
        bool ipv6,
        bool outbound = true,
        bool loopback = false)
    {
        uint bits = WinDivertAddress.PackBits(
            WinDivertLayer.Flow,
            WinDivertEvent.FlowEstablished,
            sniffed: true,
            outbound: outbound,
            loopback: loopback,
            impostor: false,
            ipv6: ipv6);

        var flow = new WinDivertFlowData
        {
            EndpointId = 42,
            ProcessId = 1234,
            LocalPort = 55845,
            RemotePort = 443,
            Protocol = protocol,
        };

        if (ipv6)
        {
            flow.LocalAddr3 = 0x20010DB8;
            flow.RemoteAddr3 = 0x20010DB8;
            flow.RemoteAddr0 = 1;
        }
        else
        {
            // 192.168.1.10 et 104.16.0.1, en ordre hote comme WinDivert les rend.
            flow.LocalAddr0 = 0xC0A8010A;
            flow.RemoteAddr0 = 0x68100001;
        }

        return WinDivertAddress.FromFlowData(bits, flow);
    }
}
