using System.Net;
using NetworkLimiter.Core.Classification;

namespace NetworkLimiter.Core.Tests.Classification;

/// <summary>
/// Classification internet / local / boucle locale — FR-040, FR-040a, research.md R-006.
/// </summary>
/// <remarks>
/// C'est la fonction la plus dangereuse du projet à se tromper, dans les deux sens.
/// Trop large, elle bride les sauvegardes vers un NAS et l'utilisateur conclut que l'outil
/// dégrade son réseau au hasard. Trop étroite, elle laisse échapper du trafic internet et
/// le plafond annoncé est faux sans que personne ne le voie.
///
/// D'où des tests aux <b>bornes exactes</b> de chaque plage, avec l'adresse publique
/// immédiatement adjacente : c'est là que les erreurs de masque se manifestent.
/// </remarks>
public sealed class NetworkScopeTests
{
    private static NetworkScope Classify(string address) =>
        NetworkScopeClassifier.Classify(IPAddress.Parse(address));

    // -- Boucle locale --------------------------------------------------------

    [Theory]
    [InlineData("127.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.255")]
    [InlineData("::1")]
    public void BoucleLocale_EstClasseeLoopback(string address) =>
        Classify(address).Should().Be(NetworkScope.Loopback);

    [Theory]
    [InlineData("126.255.255.255")]
    [InlineData("128.0.0.0")]
    public void AdressesAdjacentesA127_RestentInternet(string address) =>
        Classify(address).Should().Be(NetworkScope.Internet);

    // -- Plages privées IPv4 (RFC 1918) ---------------------------------------

    [Theory]
    [InlineData("10.0.0.0")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.0")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.0")]
    [InlineData("192.168.255.255")]
    public void PlagesPrivees_SontClasseesLocal(string address) =>
        Classify(address).Should().Be(NetworkScope.Local);

    [Theory]
    [InlineData("9.255.255.255")]    // juste avant 10.0.0.0/8
    [InlineData("11.0.0.0")]         // juste apres
    [InlineData("172.15.255.255")]   // juste avant 172.16.0.0/12
    [InlineData("172.32.0.0")]       // juste apres — l'erreur classique est de prendre /16
    [InlineData("192.167.255.255")]  // juste avant 192.168.0.0/16
    [InlineData("192.169.0.0")]      // juste apres
    public void AdressesAdjacentesAuxPlagesPrivees_RestentInternet(string address) =>
        Classify(address).Should().Be(NetworkScope.Internet);

    // -- Lien-local, multicast, diffusion -------------------------------------

    [Theory]
    [InlineData("169.254.0.0")]
    [InlineData("169.254.255.255")]
    [InlineData("224.0.0.0")]
    [InlineData("239.255.255.255")]
    [InlineData("255.255.255.255")]
    public void LienLocalMulticastEtDiffusion_SontClassesLocal(string address) =>
        Classify(address).Should().Be(NetworkScope.Local);

    [Theory]
    [InlineData("169.253.255.255")]
    [InlineData("169.255.0.0")]
    [InlineData("223.255.255.255")]
    public void AdressesAdjacentesAuLienLocalEtAuMulticast_RestentInternet(string address) =>
        Classify(address).Should().Be(NetworkScope.Internet);

    // -- IPv6 -----------------------------------------------------------------

    [Theory]
    [InlineData("fe80::")]
    [InlineData("febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]  // fin de fe80::/10
    [InlineData("fc00::")]
    [InlineData("fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]  // fin de fc00::/7
    [InlineData("ff00::")]
    [InlineData("ff02::1")]
    public void PlagesIPv6Locales_SontClasseesLocal(string address) =>
        Classify(address).Should().Be(NetworkScope.Local);

    [Theory]
    [InlineData("2001:4860:4860::8888")]  // DNS public Google
    [InlineData("2606:4700:4700::1111")]  // DNS public Cloudflare
    [InlineData("fec0::")]                // site-local, deprecie : pas dans nos plages locales
    [InlineData("fbff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]  // juste avant fc00::/7
    public void AdressesIPv6Publiques_SontClasseesInternet(string address) =>
        Classify(address).Should().Be(NetworkScope.Internet);

    // -- IPv4 encapsulee dans IPv6 --------------------------------------------

    [Theory]
    [InlineData("::ffff:10.0.0.1", NetworkScope.Local)]
    [InlineData("::ffff:192.168.1.1", NetworkScope.Local)]
    [InlineData("::ffff:127.0.0.1", NetworkScope.Loopback)]
    [InlineData("::ffff:8.8.8.8", NetworkScope.Internet)]
    public void IPv4MappeeEnIPv6_SuitLesReglesIPv4(string address, NetworkScope expected)
    {
        // Sans ce repli, une pile double-couche presenterait du trafic LAN comme
        // « internet » et le briderait a tort — ou l'inverse.
        Classify(address).Should().Be(expected);
    }

    // -- Trafic internet nominal ----------------------------------------------

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("100.64.0.1")]   // CGNAT : c'est du transport d'operateur, donc internet
    public void AdressesPubliques_SontClasseesInternet(string address) =>
        Classify(address).Should().Be(NetworkScope.Internet);

    // -- Cas limite de la spec : passerelle internet en plage privée -----------

    [Fact]
    public void PasserelleEnPlagePrivee_NExcluPasLeTraficInternet()
    {
        // Cas limite « Reseau d'entreprise en plage privee » de spec.md : la
        // classification porte sur l'adresse de DESTINATION, jamais sur la route
        // empruntee. Une requete vers un serveur public reste internet meme si le
        // premier saut est un routeur en 192.168.x.x.
        Classify("93.184.216.34").Should().Be(NetworkScope.Internet);
    }

    // -- Robustesse -----------------------------------------------------------

    [Fact]
    public void Classify_AdresseNulle_Leve()
    {
        Action act = () => NetworkScopeClassifier.Classify(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Classify_EstSeulementSoumisAuxPlafondsQuandInternet()
    {
        // Verrouille l'invariant de FR-040 exploite par le pipeline.
        NetworkScopeClassifier.IsSubjectToLimits(NetworkScope.Internet).Should().BeTrue();
        NetworkScopeClassifier.IsSubjectToLimits(NetworkScope.Local).Should().BeFalse();
        NetworkScopeClassifier.IsSubjectToLimits(NetworkScope.Loopback).Should().BeFalse();
    }
}
