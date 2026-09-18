using System.Text;
using Microsoft.Extensions.Time.Testing;
using NetworkLimiter.Service.Persistence;

namespace NetworkLimiter.Service.Tests.Persistence;

/// <summary>
/// Mécanique du fichier de configuration — FR-022, contracts/config-schema.md.
/// </summary>
/// <remarks>
/// <para>
/// Ce composant peut détruire les réglages de l'utilisateur. L'exigence n'est donc pas
/// seulement « ça marche » mais « après une interruption à n'importe quelle étape, la relecture
/// donne soit l'ancien contenu intégral, soit le nouveau, jamais un fichier tronqué ».
/// </para>
/// <para>
/// L'interruption est simulée par un point d'observation interne, visible des seuls tests.
/// C'est une couture assumée : le cas « coupure de courant pendant la sauvegarde » ne se
/// reproduit pas autrement, et c'est précisément celui qui corrompt une configuration au pire
/// moment.
/// </para>
/// </remarks>
public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _configPath;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 18, 14, 30, 0, TimeSpan.Zero));

    public ConfigStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, "config.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Nettoyage opportuniste.
        }
    }

    private ConfigStore CreateStore() => new(_configPath, _clock);

    private static byte[] ValidConfig(string marker = "a") =>
        Encoding.UTF8.GetBytes($$"""{"schemaVersion":1,"marker":"{{marker}}"}""");

    private string ReadFile() => File.ReadAllText(_configPath);

    // -- Lecture --------------------------------------------------------------

    [Fact]
    public void FichierAbsent_EstSignaleCommeManquantEtNonCommeCorrompu()
    {
        // Premier demarrage : ce n'est pas une anomalie, et confondre les deux afficherait
        // un avertissement alarmant a un utilisateur qui vient d'installer l'outil.
        ConfigReadResult result = CreateStore().Read();

        result.Status.Should().Be(ConfigReadStatus.Missing);
        result.Content.Should().BeNull();
        result.Diagnostic.Should().BeNull();
    }

    [Fact]
    public void ConfigurationValide_EstRelueIdentique()
    {
        ConfigStore store = CreateStore();
        byte[] content = ValidConfig();
        store.Write(content);

        ConfigReadResult result = store.Read();

        result.Status.Should().Be(ConfigReadStatus.Ok);
        result.Content.Should().Equal(content);
    }

    // -- Corruption -----------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("pas du json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("\"chaine\"")]
    [InlineData("{\"pasDeVersion\":true}")]
    [InlineData("{\"schemaVersion\":\"un\"}")]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":0}")]
    public void ConfigurationCorrompueOuDeVersionInconnue_EstSignalee(string content)
    {
        File.WriteAllText(_configPath, content);

        ConfigReadResult result = CreateStore().Read();

        result.Status.Should().Be(ConfigReadStatus.Corrupt);
        result.Content.Should().BeNull();
        result.Diagnostic.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ConfigurationCorrompue_EstConserveeEtNonSupprimee()
    {
        // C'est la seule chance de recuperer les reglages de l'utilisateur.
        File.WriteAllText(_configPath, "contenu irrecuperable");

        ConfigReadResult result = CreateStore().Read();

        result.QuarantinePath.Should().NotBeNullOrWhiteSpace();
        File.Exists(result.QuarantinePath!).Should().BeTrue();
        File.ReadAllText(result.QuarantinePath!).Should().Be("contenu irrecuperable");
    }

    [Fact]
    public void NomDuFichierEnQuarantaine_EstHorodate()
    {
        // Plusieurs corruptions successives ne doivent pas s'ecraser l'une l'autre.
        File.WriteAllText(_configPath, "corrompu");

        ConfigReadResult result = CreateStore().Read();

        Path.GetFileName(result.QuarantinePath!).Should().Contain("20260918-143000");
    }

    [Fact]
    public void ConfigurationCorrompue_LeFichierPrincipalEstRetire()
    {
        // Sinon la lecture suivante signalerait a nouveau la meme corruption et creerait une
        // quarantaine de plus a chaque demarrage.
        File.WriteAllText(_configPath, "corrompu");

        CreateStore().Read();

        File.Exists(_configPath).Should().BeFalse();
    }

    // -- Écriture atomique ----------------------------------------------------

    [Fact]
    public void PremiereEcriture_CreeLeFichier()
    {
        CreateStore().Write(ValidConfig("premier"));

        ReadFile().Should().Contain("premier");
    }

    [Fact]
    public void EcritureSuivante_RemplaceLeContenu()
    {
        ConfigStore store = CreateStore();
        store.Write(ValidConfig("ancien"));

        store.Write(ValidConfig("nouveau"));

        ReadFile().Should().Contain("nouveau");
        ReadFile().Should().NotContain("ancien");
    }

    [Fact]
    public void ApresEcritureReussie_AucunFichierTemporaireNiSauvegardeNeSubsiste()
    {
        ConfigStore store = CreateStore();
        store.Write(ValidConfig("un"));
        store.Write(ValidConfig("deux"));

        File.Exists(_configPath + ".tmp").Should().BeFalse();
        File.Exists(_configPath + ".bak").Should().BeFalse();
    }

    [Theory]
    [InlineData(ConfigWriteStage.TemporaryWritten)]
    [InlineData(ConfigWriteStage.BeforeReplace)]
    public void InterruptionAvantLeRemplacement_LaisseLAncienContenuIntact(ConfigWriteStage stage)
    {
        ConfigStore store = CreateStore();
        store.Write(ValidConfig("ancien"));

        ConfigStore interrupted = CreateStore();
        interrupted.StageHook = reached =>
        {
            if (reached == stage)
            {
                throw new IOException("coupure simulée");
            }
        };

        Action act = () => interrupted.Write(ValidConfig("nouveau"));

        act.Should().Throw<IOException>();
        ReadFile().Should().Contain("ancien", "une interruption avant le remplacement ne doit rien changer");
    }

    [Fact]
    public void InterruptionApresLeRemplacement_LaisseLeNouveauContenuEnPlace()
    {
        // A cette etape, seule la suppression de la sauvegarde reste a faire : le nouveau
        // contenu est deja atomiquement en place.
        ConfigStore store = CreateStore();
        store.Write(ValidConfig("ancien"));

        ConfigStore interrupted = CreateStore();
        interrupted.StageHook = reached =>
        {
            if (reached == ConfigWriteStage.AfterReplace)
            {
                throw new IOException("coupure simulée");
            }
        };

        Action act = () => interrupted.Write(ValidConfig("nouveau"));

        act.Should().Throw<IOException>();
        ReadFile().Should().Contain("nouveau");
    }

    [Fact]
    public void ApresInterruption_LaRelectureNeDonneJamaisUnFichierTronque()
    {
        // La propriete essentielle, verifiee sur les trois etapes : le contenu relu est
        // toujours l'un des deux contenus complets, jamais un melange.
        foreach (ConfigWriteStage stage in Enum.GetValues<ConfigWriteStage>())
        {
            File.Delete(_configPath);
            ConfigStore store = CreateStore();
            store.Write(ValidConfig("ancien"));

            ConfigStore interrupted = CreateStore();
            interrupted.StageHook = reached =>
            {
                if (reached == stage)
                {
                    throw new IOException("coupure simulée");
                }
            };

            try
            {
                interrupted.Write(ValidConfig("nouveau"));
            }
            catch (IOException)
            {
                // Attendu.
            }

            ConfigReadResult result = CreateStore().Read();

            result.Status.Should().Be(ConfigReadStatus.Ok, $"étape interrompue : {stage}");
            Encoding.UTF8.GetString(result.Content!)
                    .Should().BeOneOf(
                        Encoding.UTF8.GetString(ValidConfig("ancien")),
                        Encoding.UTF8.GetString(ValidConfig("nouveau")));
        }
    }

    // -- Reprise d'une écriture interrompue -----------------------------------

    [Fact]
    public void SauvegardeOrphelineSansFichierPrincipal_EstRestauree()
    {
        // Cas d'une interruption pendant le remplacement : la sauvegarde est le dernier
        // contenu valide connu.
        File.WriteAllBytes(_configPath + ".bak", ValidConfig("sauvegarde"));

        CreateStore().RecoverInterruptedWrite();

        File.Exists(_configPath).Should().BeTrue();
        ReadFile().Should().Contain("sauvegarde");
    }

    [Fact]
    public void TemporaireOrphelin_EstSupprime()
    {
        // Son contenu n'a jamais ete valide : le garder ferait croire a une configuration
        // disponible.
        ConfigStore store = CreateStore();
        store.Write(ValidConfig("valide"));
        File.WriteAllText(_configPath + ".tmp", "contenu partiel");

        store.RecoverInterruptedWrite();

        File.Exists(_configPath + ".tmp").Should().BeFalse();
        ReadFile().Should().Contain("valide");
    }

    [Fact]
    public void SauvegardePresenteAvecFichierPrincipal_NEcrasePasLePrincipal()
    {
        ConfigStore store = CreateStore();
        store.Write(ValidConfig("courant"));
        File.WriteAllBytes(_configPath + ".bak", ValidConfig("ancien"));

        store.RecoverInterruptedWrite();

        ReadFile().Should().Contain("courant");
    }

    // -- Robustesse -----------------------------------------------------------

    [Fact]
    public void Constructeur_SansChemin_Leve()
    {
        Action act = () => _ = new ConfigStore("  ", _clock);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructeur_SansHorloge_Leve()
    {
        Action act = () => _ = new ConfigStore(_configPath, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void VersionDeSchemaSupportee_EstUn() =>
        ConfigStore.SupportedSchemaVersion.Should().Be(1);
}
