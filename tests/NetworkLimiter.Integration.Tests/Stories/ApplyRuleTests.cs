using System.Diagnostics;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Service.Persistence;

namespace NetworkLimiter.Integration.Tests.Stories;

/// <summary>
/// US1 — poser, modifier et retirer une limite sur une application (FR-002, SC-003).
/// </summary>
/// <remarks>
/// <para>
/// Éprouve l'<b>assemblage</b>, pas les pièces : la chaîne complète du paquet brut à la
/// réinjection, câblée comme le service la câble. C'est le niveau où les défauts de ce projet
/// se sont réellement produits — chaque composant était testé isolément bien avant que la
/// chaîne ne voie son premier paquet.
/// </para>
/// <para>
/// <b>Sur le budget de 5 secondes de FR-002.</b> La propagation est ici <i>synchrone</i> :
/// l'écriture d'une règle applique les nouveaux plafonds avant de rendre la main. L'assertion
/// retenue est donc plus forte que l'exigence — le <b>paquet suivant</b> est déjà mis en forme —
/// et elle est déterministe, là où mesurer cinq secondes au chronomètre donnerait un test lent
/// et instable. Le budget lui-même reste vérifié, par une borne large.
/// </para>
/// </remarks>
public sealed class ApplyRuleTests
{
    private const string Machine = "192.168.1.10";
    private const string Server = "104.16.0.1";
    private const string OtherServer = "104.16.0.2";

    private const string LimitedApp = @"c:\jeux\jeu.exe";
    private const string OtherApp = @"c:\bureau\travail.exe";

    private const uint LimitedPid = 4242;
    private const uint OtherPid = 4243;

    private const int PacketBytes = 1400;

    [Fact]
    public void UneRegleAjoutee_SAppliqueDesLePaquetSuivant()
    {
        using var harness = new ShapingHarness();

        Connect(harness, LimitedPid, LimitedApp, 50_001);

        // Sans regle, tout passe : c'est le comportement par defaut et il doit etre constate
        // AVANT, sinon le test ne prouverait pas que c'est la regle qui change quelque chose.
        harness.DeliverInbound(Server, 443, Machine, 50_001, PacketBytes)
            .Should().BeTrue("aucune limite n'est définie");

        var elapsed = Stopwatch.StartNew();
        AddRule(harness, LimitedApp, downloadBytesPerSecond: 10_240);
        elapsed.Stop();

        // Le seau demarre plein : le premier paquet passe sur le credit initial, le suivant
        // attend. C'est le comportement voulu — une limite ne doit pas hacher le tout premier
        // octet d'un transfert.
        harness.DeliverInbound(Server, 443, Machine, 50_001, PacketBytes);

        bool shaped = false;

        for (int attempt = 0; attempt < 20 && !shaped; attempt++)
        {
            shaped = !harness.DeliverInbound(Server, 443, Machine, 50_001, PacketBytes);
        }

        shaped.Should().BeTrue("le plafond doit retenir des paquets une fois le crédit épuisé");

        elapsed.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(5),
            "FR-002 borne la prise d'effet d'une règle à 5 s ; la propagation est ici synchrone");
    }

    [Fact]
    public void UnPaquetRetenu_EstReinjecte_JamaisPerdu()
    {
        using var harness = new ShapingHarness();

        Connect(harness, LimitedPid, LimitedApp, 50_002);
        AddRule(harness, LimitedApp, downloadBytesPerSecond: 10_240);

        int reinjectedBefore = DrainCredit(harness, 50_002);

        // Le budget revient avec le temps : les paquets retenus doivent ressortir. Les
        // abandonner romprait la connexion etablie, ce que FR-003c interdit pour TCP.
        harness.Clock.Advance(TimeSpan.FromSeconds(2));

        harness.DeliverInbound(Server, 443, Machine, 50_002, PacketBytes);

        harness.Reinjected.Count.Should().BeGreaterThan(
            reinjectedBefore,
            "un paquet temporisé doit être réinjecté quand son budget revient, jamais abandonné");
    }

    [Fact]
    public void AucunPaquetNEstRejete_SurUneConnexionTcpEtablie()
    {
        using var harness = new ShapingHarness();

        Connect(harness, LimitedPid, LimitedApp, 50_003);
        RuleDto rule = AddRule(harness, LimitedApp, downloadBytesPerSecond: 10_240);

        for (int index = 0; index < 40; index++)
        {
            harness.DeliverInbound(Server, 443, Machine, 50_003, PacketBytes);
        }

        // FR-003c : le trafic temporisable ne doit JAMAIS etre rejete. Un rejet sur TCP
        // provoque une retransmission et un effondrement de fenetre, c'est-a-dire un debit
        // pire que la limite demandee, pour un utilisateur qui n'a rien demande de tel.
        harness.Shaper.GetDroppedPackets(rule.Id).Should().Be(
            0, "TCP dispose d'un contrôle de congestion : la temporisation suffit");
    }

    [Fact]
    public void UneAutreApplication_NEstPasAffectee()
    {
        using var harness = new ShapingHarness();

        Connect(harness, LimitedPid, LimitedApp, 50_004);
        Connect(harness, OtherPid, OtherApp, 50_005);

        AddRule(harness, LimitedApp, downloadBytesPerSecond: 10_240);

        DrainCredit(harness, 50_004);

        // L'autre application doit passer integralement, meme pendant que la premiere est
        // etranglee. C'est la propriete qui distingue une limite par application d'une limite
        // sur toute la machine.
        for (int index = 0; index < 30; index++)
        {
            harness.DeliverInbound(OtherServer, 443, Machine, 50_005, PacketBytes)
                .Should().BeTrue($"le paquet {index} d'une application non visée doit passer");
        }
    }

    [Fact]
    public void UnPlafondEleve_PrendEffetSansRecreerLeSeau()
    {
        using var harness = new ShapingHarness();

        Connect(harness, LimitedPid, LimitedApp, 50_006);
        RuleDto rule = AddRule(harness, LimitedApp, downloadBytesPerSecond: 10_240);

        DrainCredit(harness, 50_006);

        // Releve le plafond : le trafic doit repartir sans attendre, mais sans repartir non
        // plus d'un seau plein, ce qui autoriserait une rafale hors plafond.
        harness.Rules.UpsertRule(rule.Id, rule with { DownloadBytesPerSecond = 10_485_760 })
            .Ok.Should().BeTrue();

        harness.Clock.Advance(TimeSpan.FromMilliseconds(200));

        harness.DeliverInbound(Server, 443, Machine, 50_006, PacketBytes)
            .Should().BeTrue("un plafond relevé doit laisser passer dès le paquet suivant");
    }

    [Fact]
    public void UneRegleRetiree_LibereLeTraficEtLesPaquetsEnAttente()
    {
        using var harness = new ShapingHarness();

        Connect(harness, LimitedPid, LimitedApp, 50_007);
        RuleDto rule = AddRule(harness, LimitedApp, downloadBytesPerSecond: 10_240);

        DrainCredit(harness, 50_007);

        var elapsed = Stopwatch.StartNew();
        harness.Rules.DeleteRule(rule.Id).Ok.Should().BeTrue();
        elapsed.Stop();

        // Retirer une limite doit rendre le reseau IMMEDIATEMENT. Un utilisateur qui supprime
        // une regle parce que quelque chose ne marche plus ne doit pas avoir a patienter.
        for (int index = 0; index < 10; index++)
        {
            harness.DeliverInbound(Server, 443, Machine, 50_007, PacketBytes)
                .Should().BeTrue($"le paquet {index} doit passer, la règle n'existe plus");
        }

        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "FR-002 borne aussi le retrait");
    }

    [Fact]
    public void UnFluxInconnu_PasseSansLimitation()
    {
        using var harness = new ShapingHarness();

        Connect(harness, LimitedPid, LimitedApp, 50_008);
        AddRule(harness, LimitedApp, downloadBytesPerSecond: 10_240);

        // Aucun evenement de flux pour ce port : le paquet n'est attribuable a personne. Le
        // retenir ou le rejeter etranglerait un trafic qu'on ne sait meme pas identifier
        // (principe IV).
        for (int index = 0; index < 10; index++)
        {
            harness.DeliverInbound(Server, 443, Machine, 60_999, PacketBytes)
                .Should().BeTrue("dans le doute, on rend le réseau à l'utilisateur");
        }
    }

    private static void Connect(ShapingHarness harness, uint processId, string executablePath, ushort localPort)
    {
        harness.Processes.Map(processId, executablePath);
        harness.EstablishFlow(processId, Machine, localPort, ServerFor(localPort), 443);
        harness.WaitForFlows();
    }

    private static string ServerFor(ushort localPort) => localPort == 50_005 ? OtherServer : Server;

    private static RuleDto AddRule(ShapingHarness harness, string executablePath, long downloadBytesPerSecond)
    {
        var rule = new RuleDto
        {
            Id = Guid.NewGuid(),
            Target = new AppIdentityDto
            {
                ExecutablePath = executablePath,
                ExecutableName = Path.GetFileName(executablePath),
                DisplayName = Path.GetFileNameWithoutExtension(executablePath),
            },
            DownloadBytesPerSecond = downloadBytesPerSecond,
            UploadBytesPerSecond = null,
            Enabled = true,
            ExemptFromGlobal = false,
        };

        WriteResult result = harness.Rules.UpsertRule(null, rule);
        result.Ok.Should().BeTrue(result.Message);

        return rule;
    }

    /// <summary>Consomme le crédit initial du seau jusqu'à ce qu'un paquet soit retenu.</summary>
    private static int DrainCredit(ShapingHarness harness, ushort localPort)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (!harness.DeliverInbound(Server, 443, Machine, localPort, PacketBytes))
            {
                return harness.Reinjected.Count;
            }
        }

        throw new InvalidOperationException(
            "Le plafond n'a retenu aucun paquet : la limite ne s'applique pas.");
    }
}
