using System.Net;
using Microsoft.Extensions.Time.Testing;
using NetworkLimiter.Service.FlowTable;

namespace NetworkLimiter.Service.Tests.FlowTable;

/// <summary>
/// Table de flux — research.md R-003.
/// </summary>
/// <remarks>
/// <para>
/// La couche <c>NETWORK</c> de WinDivert ne porte <b>aucun</b> identifiant de processus : sa
/// structure ne contient que des indices d'interface. Attribuer un paquet à une application
/// passe donc obligatoirement par cette table, alimentée par la couche <c>FLOW</c>.
/// </para>
/// <para>
/// Deux propriétés comptent autant que la correspondance elle-même. D'abord la table doit
/// rester <b>bornée</b> : elle est alimentée par le trafic, donc par l'extérieur. Ensuite un
/// flux inconnu doit se traduire par « je ne sais pas » et non par une supposition — le
/// pipeline réinjecte alors le paquet sans limitation, conformément au principe IV.
/// </para>
/// </remarks>
public sealed class FlowTableTests
{
    private const byte Tcp = 6;
    private const byte Udp = 17;

    private static FlowKey Key(
        string local = "192.168.1.10", ushort localPort = 50000,
        string remote = "93.184.216.34", ushort remotePort = 443,
        byte protocol = Tcp) =>
        new(protocol, IPAddress.Parse(local), localPort, IPAddress.Parse(remote), remotePort);

    private static (Service.FlowTable.FlowTable Table, FakeTimeProvider Clock) Create(int maxEntries = 4096)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return (new Service.FlowTable.FlowTable(clock, maxEntries), clock);
    }

    // -- Correspondance de base -----------------------------------------------

    [Fact]
    public void FluxEtabli_EstRetrouveParSonQuintuplet()
    {
        (Service.FlowTable.FlowTable table, _) = Create();
        FlowKey key = Key();

        table.OnFlowEstablished(key, endpointId: 42, processId: 1234);

        table.TryGetProcessId(key, out uint processId).Should().BeTrue();
        processId.Should().Be(1234u);
    }

    [Fact]
    public void FluxInconnu_RepondQuIlEstInconnu()
    {
        // Le pipeline reinjecte alors le paquet SANS limitation. Dans le doute, on laisse
        // passer : c'est le principe IV.
        (Service.FlowTable.FlowTable table, _) = Create();

        table.TryGetProcessId(Key(), out uint processId).Should().BeFalse();
        processId.Should().Be(0u);
    }

    [Fact]
    public void QuintupletsDifferents_NeSeConfondentPas()
    {
        (Service.FlowTable.FlowTable table, _) = Create();
        table.OnFlowEstablished(Key(localPort: 50000), 1, processId: 100);
        table.OnFlowEstablished(Key(localPort: 50001), 2, processId: 200);

        table.TryGetProcessId(Key(localPort: 50000), out uint first).Should().BeTrue();
        table.TryGetProcessId(Key(localPort: 50001), out uint second).Should().BeTrue();

        first.Should().Be(100u);
        second.Should().Be(200u);
    }

    [Fact]
    public void MemeQuintupletMaisProtocoleDifferent_EstUnFluxDistinct()
    {
        (Service.FlowTable.FlowTable table, _) = Create();
        table.OnFlowEstablished(Key(protocol: Tcp), 1, processId: 100);
        table.OnFlowEstablished(Key(protocol: Udp), 2, processId: 200);

        table.TryGetProcessId(Key(protocol: Tcp), out uint tcp).Should().BeTrue();
        table.TryGetProcessId(Key(protocol: Udp), out uint udp).Should().BeTrue();

        tcp.Should().Be(100u);
        udp.Should().Be(200u);
    }

    [Fact]
    public void FluxIPv6_EstTraiteCommeUnFluxIPv4()
    {
        // FR-008 : IPv6 est soumis aux memes plafonds. Un trou ici laisserait le trafic
        // IPv6 non attribue, donc non limite, sans que rien ne le signale.
        (Service.FlowTable.FlowTable table, _) = Create();
        FlowKey key = Key(local: "fd00::1", remote: "2606:4700:4700::1111");

        table.OnFlowEstablished(key, endpointId: 7, processId: 999);

        table.TryGetProcessId(key, out uint processId).Should().BeTrue();
        processId.Should().Be(999u);
    }

    // -- Suppression de flux --------------------------------------------------

    [Fact]
    public void FluxSupprime_NEstPlusRetrouve()
    {
        (Service.FlowTable.FlowTable table, _) = Create();
        FlowKey key = Key();
        table.OnFlowEstablished(key, 1, processId: 100);

        table.OnFlowDeleted(key);

        table.TryGetProcessId(key, out _).Should().BeFalse();
        table.Count.Should().Be(0);
    }

    [Fact]
    public void SuppressionDUnFluxInconnu_EstSansEffet()
    {
        (Service.FlowTable.FlowTable table, _) = Create();

        Action act = () => table.OnFlowDeleted(Key());

        act.Should().NotThrow();
        table.Count.Should().Be(0);
    }

    [Fact]
    public void ReetablissementDuMemeQuintuplet_MetAJourLeProcessus()
    {
        // Les ports sont reutilises : un quintuplet identique peut appartenir a un autre
        // processus quelques instants plus tard. Conserver l'ancien attribuerait le trafic
        // a la mauvaise application, donc lui appliquerait la mauvaise limite.
        (Service.FlowTable.FlowTable table, _) = Create();
        FlowKey key = Key();
        table.OnFlowEstablished(key, endpointId: 1, processId: 100);

        table.OnFlowEstablished(key, endpointId: 2, processId: 200);

        table.TryGetProcessId(key, out uint processId).Should().BeTrue();
        processId.Should().Be(200u);
        table.Count.Should().Be(1);
    }

    // -- Purge des orphelins --------------------------------------------------

    [Fact]
    public void EntreesSansEvenementDeSuppression_SontPurgeesApresExpiration()
    {
        // Tous les flux ne produisent pas de FLOW_DELETED : arret brutal d'un processus,
        // perte d'evenement sous charge. Sans balayage, la table croitrait indefiniment.
        (Service.FlowTable.FlowTable table, FakeTimeProvider clock) = Create();
        table.OnFlowEstablished(Key(localPort: 1), 1, processId: 100);
        table.OnFlowEstablished(Key(localPort: 2), 2, processId: 200);

        clock.Advance(TimeSpan.FromMinutes(10));
        int purged = table.PurgeStale(TimeSpan.FromMinutes(5));

        purged.Should().Be(2);
        table.Count.Should().Be(0);
    }

    [Fact]
    public void EntreesRecentes_NeSontPasPurgees()
    {
        (Service.FlowTable.FlowTable table, FakeTimeProvider clock) = Create();
        table.OnFlowEstablished(Key(), 1, processId: 100);

        clock.Advance(TimeSpan.FromMinutes(1));
        int purged = table.PurgeStale(TimeSpan.FromMinutes(5));

        purged.Should().Be(0);
        table.Count.Should().Be(1);
    }

    [Fact]
    public void ConsultationDUnFlux_RafraichitSaDateDeDerniereVue()
    {
        // Un flux long mais actif (telechargement, visioconference) ne doit pas etre purge
        // sous pretexte que son etablissement est ancien.
        (Service.FlowTable.FlowTable table, FakeTimeProvider clock) = Create();
        FlowKey key = Key();
        table.OnFlowEstablished(key, 1, processId: 100);

        for (int i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            table.TryGetProcessId(key, out _).Should().BeTrue();
        }

        table.PurgeStale(TimeSpan.FromMinutes(5)).Should().Be(0);
        table.Count.Should().Be(1);
    }

    // -- Table bornée ---------------------------------------------------------

    [Fact]
    public void TableAuMaximum_EvinceLesEntreesLesPlusAnciennes()
    {
        // La table est alimentee par le trafic, donc par l'exterieur. Sans borne, un flux
        // d'etablissements suffirait a epuiser la memoire du service.
        (Service.FlowTable.FlowTable table, FakeTimeProvider clock) = Create(maxEntries: 3);

        for (ushort port = 1; port <= 5; port++)
        {
            table.OnFlowEstablished(Key(localPort: port), port, processId: port);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        table.Count.Should().BeLessThanOrEqualTo(3);
        table.TryGetProcessId(Key(localPort: 5), out uint newest).Should().BeTrue();
        newest.Should().Be(5u);
        table.TryGetProcessId(Key(localPort: 1), out _).Should().BeFalse();
    }

    [Fact]
    public void CapaciteMaximale_EstStrictementPositive()
    {
        Action act = () => _ = new Service.FlowTable.FlowTable(new FakeTimeProvider(), maxEntries: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructeur_SansHorloge_Leve()
    {
        Action act = () => _ = new Service.FlowTable.FlowTable(null!, 100);

        act.Should().Throw<ArgumentNullException>();
    }

    // -- Vidage complet -------------------------------------------------------

    [Fact]
    public void Clear_VideLaTable()
    {
        // Appele a la fermeture des handles : les flux connus n'ont plus de sens une fois
        // l'interception arretee, et les conserver ferait repartir sur un etat perime.
        (Service.FlowTable.FlowTable table, _) = Create();
        table.OnFlowEstablished(Key(localPort: 1), 1, 100);
        table.OnFlowEstablished(Key(localPort: 2), 2, 200);

        table.Clear();

        table.Count.Should().Be(0);
    }
}
