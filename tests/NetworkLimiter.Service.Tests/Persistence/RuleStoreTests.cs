using Microsoft.Extensions.Time.Testing;
using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Service.Persistence;

namespace NetworkLimiter.Service.Tests.Persistence;

/// <summary>
/// Écritures transactionnelles — contracts/ipc-protocol.md, « Invariants d'écriture ».
/// </summary>
/// <remarks>
/// <para>
/// La garantie qui compte : <b>un échec à n'importe quelle étape laisse l'état exactement tel
/// qu'avant</b>. C'est ce qui rend sûr le cas limite « élévation refusée en milieu de
/// modification » de la spécification — et, plus généralement, ce qui évite qu'un disque plein
/// ou une permission perdue ne laisse le service et le fichier de configuration divergents.
/// </para>
/// <para>
/// L'ordre est donc : valider l'état résultant, le persister, et seulement ensuite le publier
/// en mémoire. Appliquer en mémoire d'abord ferait « reculer » au redémarrage des réglages que
/// l'utilisateur a vus appliqués.
/// </para>
/// </remarks>
public sealed class RuleStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly ConfigStore _configStore;
    private readonly RuleStore _store;

    public RuleStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nl-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _configStore = new ConfigStore(
            Path.Combine(_directory, "config.json"),
            new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        _store = new RuleStore(_configStore, PersistedConfig.CreateDefault());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nettoyage opportuniste.
        }
    }

    private static RuleDto Rule(
        Guid? id = null,
        string path = @"c:\app\app.exe",
        long? download = 1_048_576,
        bool enabled = true) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            Target = new AppIdentityDto
            {
                ExecutablePath = path,
                ExecutableName = path[(path.LastIndexOf('\\') + 1)..],
                DisplayName = "App",
            },
            DownloadBytesPerSecond = download,
            UploadBytesPerSecond = null,
            Enabled = enabled,
            ExemptFromGlobal = false,
        };

    // -- Écriture nominale ----------------------------------------------------

    [Fact]
    public void CreerUneRegle_LaRendVisibleEtLaPersiste()
    {
        RuleDto rule = Rule();

        _store.UpsertRule(null, rule).Ok.Should().BeTrue();

        _store.ActiveRules.Should().ContainSingle().Which.Id.Should().Be(rule.Id);

        // Relu depuis le disque : la persistance a bien eu lieu, pas seulement la mise a jour
        // en memoire.
        ConfigReadResult read = _configStore.Read();
        read.Status.Should().Be(ConfigReadStatus.Ok);
        ConfigSerializer.TryDeserialize(read.Content!, out _)!
            .ActiveProfile.Rules.Should().ContainSingle();
    }

    [Fact]
    public void ModifierUneRegle_RemplaceSonContenuSansLaDupliquer()
    {
        RuleDto rule = Rule(download: 1_048_576);
        _store.UpsertRule(null, rule);

        _store.UpsertRule(rule.Id, rule with { DownloadBytesPerSecond = 102_400 }).Ok.Should().BeTrue();

        _store.ActiveRules.Should().ContainSingle()
              .Which.DownloadBytesPerSecond.Should().Be(102_400);
    }

    [Fact]
    public void SupprimerUneRegle_LaRetire()
    {
        RuleDto rule = Rule();
        _store.UpsertRule(null, rule);

        _store.DeleteRule(rule.Id).Ok.Should().BeTrue();

        _store.ActiveRules.Should().BeEmpty();
    }

    [Fact]
    public void DesactiverUneRegle_LaConserve()
    {
        // FR-006 : desactiver n'est pas supprimer. L'utilisateur qui coupe temporairement une
        // limite doit la retrouver telle quelle.
        RuleDto rule = Rule();
        _store.UpsertRule(null, rule);

        _store.SetRuleEnabled(rule.Id, enabled: false).Ok.Should().BeTrue();

        _store.ActiveRules.Should().ContainSingle().Which.Enabled.Should().BeFalse();
    }

    // -- Refus et état inchangé -----------------------------------------------

    [Fact]
    public void RegleInvalide_EstRefuseeSansRienChanger()
    {
        RuleDto valid = Rule();
        _store.UpsertRule(null, valid);
        PersistedConfig before = _store.Config;

        WriteResult result = _store.UpsertRule(null, Rule(path: @"c:\autre\autre.exe", download: 5));

        result.Ok.Should().BeFalse();
        result.Code.Should().Be(ErrorCode.ValidationFailed);
        _store.Config.Should().BeSameAs(before, "un refus ne doit rien changer");
        _store.ActiveRules.Should().ContainSingle();
    }

    [Fact]
    public void DeuxReglesVisantLaMemeApplication_SontRefusees()
    {
        // Deux plafonds concurrents sur le meme trafic, sans qu'aucun ne soit manifestement
        // le bon : l'utilisateur ne pourrait pas expliquer le debit qu'il observe.
        _store.UpsertRule(null, Rule(path: @"c:\app\app.exe"));

        WriteResult result = _store.UpsertRule(null, Rule(path: @"c:\app\app.exe"));

        result.Ok.Should().BeFalse();
        result.Code.Should().Be(ErrorCode.ConflictingRule);
        result.Message.Should().NotBeNullOrWhiteSpace();
        _store.ActiveRules.Should().ContainSingle();
    }

    [Fact]
    public void ModifierUneRegle_NEntreEnConflitAvecElleMeme()
    {
        RuleDto rule = Rule();
        _store.UpsertRule(null, rule);

        _store.UpsertRule(rule.Id, rule with { DownloadBytesPerSecond = 204_800 })
              .Ok.Should().BeTrue();
    }

    [Fact]
    public void SupprimerUneRegleInexistante_EstRefuseProprement()
    {
        WriteResult result = _store.DeleteRule(Guid.NewGuid());

        result.Ok.Should().BeFalse();
        result.Code.Should().Be(ErrorCode.NotFound);
    }

    [Fact]
    public void ActiverUneRegleInexistante_EstRefuseProprement()
    {
        WriteResult result = _store.SetRuleEnabled(Guid.NewGuid(), enabled: true);

        result.Ok.Should().BeFalse();
        result.Code.Should().Be(ErrorCode.NotFound);
    }

    [Fact]
    public void DepasserLeNombreMaximalDeRegles_EstRefuse()
    {
        for (int i = 0; i < ProfileDto.MaxRules; i++)
        {
            _store.UpsertRule(null, Rule(path: $@"c:\app{i}\app{i}.exe")).Ok.Should().BeTrue();
        }

        WriteResult result = _store.UpsertRule(null, Rule(path: @"c:\trop\trop.exe"));

        result.Ok.Should().BeFalse();
        _store.ActiveRules.Should().HaveCount(ProfileDto.MaxRules);
    }

    // -- Échec de persistance -------------------------------------------------

    [Fact]
    public void EchecDePersistance_LaisseLEtatEnMemoireIntact()
    {
        // LE test de la garantie transactionnelle. Si la memoire etait mise a jour avant le
        // disque, le service appliquerait une regle que le fichier ignore — et un
        // redemarrage la ferait disparaitre sans explication.
        RuleDto first = Rule();
        _store.UpsertRule(null, first).Ok.Should().BeTrue();
        PersistedConfig before = _store.Config;

        _configStore.StageHook = _ => throw new IOException("disque plein simulé");

        WriteResult result = _store.UpsertRule(null, Rule(path: @"c:\autre\autre.exe"));

        result.Ok.Should().BeFalse();
        result.Code.Should().Be(ErrorCode.InternalError);
        _store.Config.Should().BeSameAs(before);
        _store.ActiveRules.Should().ContainSingle().Which.Id.Should().Be(first.Id);
    }

    [Fact]
    public void EchecDePersistance_NeDeclenchePasDeNotification()
    {
        // Notifier une modification qui n'a pas eu lieu ferait afficher a l'interface un etat
        // que le service n'applique pas.
        _configStore.StageHook = _ => throw new IOException("disque plein simulé");
        bool notified = false;
        _store.Changed += (_, _) => notified = true;

        _store.UpsertRule(null, Rule());

        notified.Should().BeFalse();
    }

    // -- Notification ---------------------------------------------------------

    [Fact]
    public void EcritureReussie_DeclencheUneNotification()
    {
        // Diffusee a TOUS les clients, y compris non eleves : une interface en lecture seule
        // doit refleter les changements faits par une instance elevee, sans quoi l'utilisateur
        // verrait deux fenetres se contredire.
        PersistedConfig? notified = null;
        _store.Changed += (_, config) => notified = config;

        _store.UpsertRule(null, Rule()).Ok.Should().BeTrue();

        notified.Should().NotBeNull();
        notified!.ActiveProfile.Rules.Should().ContainSingle();
    }

    // -- Aller-retour de sérialisation ----------------------------------------

    [Fact]
    public void LaConfiguration_FaitUnAllerRetourFidele()
    {
        _store.UpsertRule(null, Rule(path: @"c:\a\a.exe", download: 102_400));
        _store.UpsertRule(null, Rule(path: @"c:\b\b.exe", download: null));

        byte[] bytes = ConfigSerializer.Serialize(_store.Config);
        PersistedConfig? restored = ConfigSerializer.TryDeserialize(bytes, out string? problem);

        problem.Should().BeNull();
        restored.Should().NotBeNull();
        restored!.ActiveProfile.Rules.Should().HaveCount(2);
        restored.ActiveProfileId.Should().Be(_store.Config.ActiveProfileId);
    }

    [Fact]
    public void UneConfigurationImportee_EstRevalideeIntegralement()
    {
        // Un fichier de configuration est un vecteur d'entree au meme titre qu'un message
        // IPC : il ne beneficie d'aucune confiance particuliere.
        const string tampered = """
            {"schemaVersion":1,"activeProfileId":"11111111-1111-1111-1111-111111111111",
             "profiles":[{"id":"11111111-1111-1111-1111-111111111111","name":"P",
             "globalLimit":{"enabled":false},
             "rules":[{"id":"22222222-2222-2222-2222-222222222222",
             "target":{"executablePath":"c:\\a\\a.exe","executableName":"a.exe","displayName":"A"},
             "downloadBytesPerSecond":5,"uploadBytesPerSecond":null,
             "enabled":true,"exemptFromGlobal":false}]}]}
            """;

        PersistedConfig? config = ConfigSerializer.TryDeserialize(
            System.Text.Encoding.UTF8.GetBytes(tampered), out string? problem);

        config.Should().BeNull();
        problem.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ConfigurationParDefaut_EstValideEtPorteUnProfil()
    {
        PersistedConfig config = PersistedConfig.CreateDefault();

        config.Validate().Should().BeNull();
        config.Profiles.Should().ContainSingle();
        config.ActiveProfile.Rules.Should().BeEmpty();
        config.ActiveProfile.GlobalLimit.Enabled.Should().BeFalse();
    }

    // -- Robustesse -----------------------------------------------------------

    [Fact]
    public void Constructeur_SansDepot_Leve()
    {
        Action act = () => _ = new RuleStore(null!, PersistedConfig.CreateDefault());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructeur_SansConfiguration_Leve()
    {
        Action act = () => _ = new RuleStore(_configStore, null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
