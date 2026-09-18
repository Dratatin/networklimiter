using NetworkLimiter.Core.Classification;

namespace NetworkLimiter.Core.Tests.Classification;

/// <summary>
/// Normalisation de chemin d'exécutable — FR-039, research.md R-007.
/// </summary>
/// <remarks>
/// Sans normalisation, deux écritures du même chemin réel produisent deux règles distinctes,
/// et l'utilisateur voit une application limitée deux fois avec des plafonds différents sans
/// comprendre pourquoi.
///
/// La résolution <c>\Device\HarddiskVolumeN</c> vers une lettre de lecteur est confiée à un
/// <see cref="IDeviceVolumeResolver"/> injecté : l'API Windows correspondante n'a pas sa place
/// dans <c>NetworkLimiter.Core</c>, qui ne doit dépendre d'aucun assembly Windows (principe III).
/// </remarks>
public sealed class PathNormalizerTests
{
    private sealed class FakeVolumeResolver : IDeviceVolumeResolver
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase)
        {
            ["\\Device\\HarddiskVolume3"] = "C:",
            ["\\Device\\HarddiskVolume7"] = "E:",
        };

        public bool TryResolve(string deviceName, out string driveLetter) =>
            _map.TryGetValue(deviceName, out driveLetter!);
    }

    private static PathNormalizer CreateNormalizer() => new(new FakeVolumeResolver());

    // -- Idempotence ----------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Program Files\Steam\steam.exe")]
    [InlineData(@"c:\program files\steam\steam.exe")]
    [InlineData(@"C:/Program Files/Steam/steam.exe")]
    [InlineData(@"\??\C:\Program Files\Steam\steam.exe")]
    [InlineData(@"\Device\HarddiskVolume3\Program Files\Steam\steam.exe")]
    [InlineData(@"C:\Program Files\\Steam\\steam.exe")]
    public void Normalize_EstIdempotent(string path)
    {
        // Invariant central : normalize(normalize(p)) == normalize(p). S'il tombe, la
        // comparaison de chemins devient dependante du nombre de passages, donc instable.
        PathNormalizer normalizer = CreateNormalizer();

        string once = normalizer.Normalize(path);
        string twice = normalizer.Normalize(once);

        twice.Should().Be(once);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Steam\steam.exe")]
    [InlineData(@"c:\PROGRAM FILES\steam\STEAM.EXE")]
    [InlineData(@"C:/Program Files/Steam/steam.exe")]
    [InlineData(@"\??\C:\Program Files\Steam\steam.exe")]
    [InlineData(@"\Device\HarddiskVolume3\Program Files\Steam\steam.exe")]
    [InlineData("  C:\\Program Files\\Steam\\steam.exe  ")]
    public void Normalize_FormesEquivalentes_ProduisentLaMemeChaine(string path)
    {
        PathNormalizer normalizer = CreateNormalizer();

        normalizer.Normalize(path).Should().Be(@"c:\program files\steam\steam.exe");
    }

    // -- Résolution de volume -------------------------------------------------

    [Fact]
    public void Normalize_CheminDePeripherique_EstResoluEnLettreDeLecteur()
    {
        PathNormalizer normalizer = CreateNormalizer();

        normalizer.Normalize(@"\Device\HarddiskVolume7\Jeux\jeu.exe")
                  .Should().Be(@"e:\jeux\jeu.exe");
    }

    [Fact]
    public void Normalize_VolumeInconnu_ConserveLeCheminDePeripherique()
    {
        // Perdre l'information serait pire que la garder sous une forme inhabituelle :
        // le chemin reste au moins comparable a lui-meme, donc la regle continue de
        // s'appliquer de facon stable.
        PathNormalizer normalizer = CreateNormalizer();

        normalizer.Normalize(@"\Device\HarddiskVolume99\App\app.exe")
                  .Should().Be(@"\device\harddiskvolume99\app\app.exe");
    }

    // -- Nom de fichier, critère de repli (FR-039) ----------------------------

    [Theory]
    [InlineData(@"C:\Program Files\Steam\steam.exe", "steam.exe")]
    [InlineData(@"\Device\HarddiskVolume3\Jeux\Jeu.EXE", "jeu.exe")]
    [InlineData(@"C:\app.exe", "app.exe")]
    public void GetExecutableName_RendLeNomEnCasseInvariante(string path, string expected)
    {
        PathNormalizer normalizer = CreateNormalizer();

        normalizer.GetExecutableName(path).Should().Be(expected);
    }

    // -- Cas limite de la spec : dossiers versionnés (FR-039a) ----------------

    [Fact]
    public void Normalize_DossiersVersionnes_RestentDesCheminsDistincts()
    {
        // Cas « Discord / app-1.2.3 » de spec.md : deux versions donnent bien deux chemins
        // differents. C'est voulu — c'est le repli par nom d'executable qui fera le lien,
        // pas la normalisation, et l'utilisateur doit en etre informe (FR-039a).
        PathNormalizer normalizer = CreateNormalizer();

        string v1 = normalizer.Normalize(@"C:\Users\m\AppData\Local\App\app-1.2.3\app.exe");
        string v2 = normalizer.Normalize(@"C:\Users\m\AppData\Local\App\app-1.2.4\app.exe");

        v1.Should().NotBe(v2);
        normalizer.GetExecutableName(v1).Should().Be(normalizer.GetExecutableName(v2));
    }

    // -- Robustesse -----------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_CheminVide_Leve(string path)
    {
        PathNormalizer normalizer = CreateNormalizer();

        Action act = () => normalizer.Normalize(path);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Normalize_CheminNul_Leve()
    {
        PathNormalizer normalizer = CreateNormalizer();

        Action act = () => normalizer.Normalize(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructeur_ResolveurNul_Leve()
    {
        Action act = () => _ = new PathNormalizer(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Normalize_SeparateurFinal_EstRetire()
    {
        PathNormalizer normalizer = CreateNormalizer();

        normalizer.Normalize(@"C:\Program Files\Steam\").Should().Be(@"c:\program files\steam");
    }
}
