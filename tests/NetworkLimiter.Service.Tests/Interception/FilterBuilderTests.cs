using NetworkLimiter.Service.Interception;

namespace NetworkLimiter.Service.Tests.Interception;

/// <summary>
/// Filtres d'interception — FR-008, FR-040, research.md R-003.
/// </summary>
/// <remarks>
/// <para>
/// Ces tests ne se contentent pas d'inspecter du texte : ils soumettent les filtres au
/// <b>vrai compilateur de filtres de WinDivert</b>, via <c>WinDivertHelperCompileFilter</c>.
/// Cette fonction s'exécute entièrement en mode utilisateur — ni privilèges administrateur,
/// ni pilote chargé — ce qui permet de prouver en test unitaire qu'une chaîne de filtre est
/// réellement acceptée, et pas seulement qu'elle nous paraît correcte.
/// </para>
/// <para>
/// Sans cela, une faute de syntaxe ne se manifesterait qu'au démarrage du service sur une
/// machine réelle, sous la forme d'un échec d'ouverture de handle sans cause lisible.
/// </para>
/// </remarks>
public sealed class FilterBuilderTests
{
    // -- Validation par le compilateur réel -----------------------------------

    [Fact]
    public void FiltreReseau_EstAccepteParWinDivert()
    {
        Action act = () => FilterBuilder.Validate(FilterBuilder.NetworkFilter, WinDivertLayer.Network);

        act.Should().NotThrow();
    }

    [Fact]
    public void FiltreDeFlux_EstAccepteParWinDivert()
    {
        Action act = () => FilterBuilder.Validate(FilterBuilder.FlowFilter, WinDivertLayer.Flow);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("ceci n'est pas un filtre")]
    [InlineData("ip and")]
    [InlineData("(ip or ipv6")]
    [InlineData("tcp.DstPort ==")]
    public void FiltreInvalide_EstRefuseAvecUneCauseEtUnePosition(string filter)
    {
        // Verifie que la validation a du mordant : si elle acceptait tout, les deux tests
        // precedents ne prouveraient rien.
        Action act = () => FilterBuilder.Validate(filter, WinDivertLayer.Network);

        act.Should().Throw<InvalidFilterException>()
           .Which.Filter.Should().Be(filter);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FiltreVide_Leve(string filter)
    {
        Action act = () => FilterBuilder.Validate(filter, WinDivertLayer.Network);

        act.Should().Throw<ArgumentException>();
    }

    // -- Couverture IPv4 et IPv6 (FR-008) -------------------------------------

    [Fact]
    public void FiltreReseau_CouvreLesDeuxFamillesDAdresses()
    {
        // Un plafond contournable en IPv6 est une defaillance silencieuse : la limitation
        // semblerait fonctionner, mais Windows prefere IPv6 des qu'il est disponible.
        FilterBuilder.NetworkFilter.Should().Contain("ip ");
        FilterBuilder.NetworkFilter.Should().Contain("ipv6");
    }

    [Fact]
    public void FiltreDeFlux_CouvreLesDeuxFamillesDAdresses()
    {
        FilterBuilder.FlowFilter.Should().Contain("ip ");
        FilterBuilder.FlowFilter.Should().Contain("ipv6");
    }

    [Fact]
    public void FiltreReseauRestreintAIPv4Seulement_SeraitAccepteParWinDivert()
    {
        // Demonstration du risque : un filtre qui oublie IPv6 compile parfaitement. Rien
        // dans l'outillage ne le signalerait — d'ou les deux tests ci-dessus, qui sont la
        // seule barriere contre cet oubli.
        Action act = () => FilterBuilder.Validate("ip and (tcp or udp)", WinDivertLayer.Network);

        act.Should().NotThrow();
    }

    // -- Protocoles et boucle locale ------------------------------------------

    [Fact]
    public void FiltreReseau_CouvreTcpEtUdp()
    {
        FilterBuilder.NetworkFilter.Should().Contain("tcp");
        FilterBuilder.NetworkFilter.Should().Contain("udp");
    }

    [Fact]
    public void FiltreReseau_ExclutLaBoucleLocale()
    {
        // FR-040a : la boucle locale n'est jamais soumise aux plafonds. L'exclure des le
        // noyau evite de faire transiter en mode utilisateur du trafic qu'on rejettera de
        // toute facon.
        FilterBuilder.NetworkFilter.Should().Contain("not loopback");
    }

    [Fact]
    public void FiltreDeFlux_NExclutPasLaBoucleLocale()
    {
        // Connaitre ces flux ne coute rien et evite un trou dans la table. C'est le filtre
        // reseau qui decide de ce qui est mis en forme.
        FilterBuilder.FlowFilter.Should().NotContain("loopback");
    }

    // -- La distinction internet / local n'est pas dans le filtre --------------

    [Fact]
    public void LesFiltres_NeDupliquentPasLaDefinitionDuReseauLocal()
    {
        // La classification vit dans NetworkScopeClassifier, teste aux bornes exactes de
        // chaque plage. La dupliquer dans le langage de filtre creerait deux definitions du
        // « reseau local » qui divergeraient tot ou tard.
        FilterBuilder.NetworkFilter.Should().NotContain("192.168");
        FilterBuilder.NetworkFilter.Should().NotContain("10.0.0.0");
        FilterBuilder.NetworkFilter.Should().NotContain("fe80");
    }

    // -- Disponibilité de la bibliothèque -------------------------------------

    [Fact]
    public void BibliothequeWinDivert_EstPresenteDansLaSortieDeBuild()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "WinDivert.dll");

        File.Exists(path).Should().BeTrue(
            "WinDivert.dll doit être restaurée avant de compiler. " +
            "Exécutez ./tools/restore-windivert.ps1 depuis la racine du dépôt.");
    }
}
