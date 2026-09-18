using NetworkLimiter.Core.Classification;
using NetworkLimiter.Service.ProcessIdentity;

namespace NetworkLimiter.Service.Tests.ProcessIdentity;

/// <summary>
/// Résolution de l'application propriétaire d'un processus — FR-039, research.md R-007.
/// </summary>
/// <remarks>
/// <para>
/// Le point critique est la <b>réutilisation des identifiants de processus</b>. Windows les
/// recycle, souvent rapidement. Une entrée de cache périmée attribuerait le trafic d'une
/// application à une autre : la limite serait appliquée à la mauvaise application, sans aucun
/// symptôme visible. L'utilisateur constaterait simplement qu'un programme qu'il n'a pas
/// limité rame, et que celui qu'il a limité ne l'est pas.
/// </para>
/// <para>
/// D'où la clé de cache composite (identifiant, heure de démarrage), et des tests qui
/// reproduisent explicitement le recyclage.
/// </para>
/// </remarks>
public sealed class ProcessIdentityResolverTests
{
    private sealed class FakeProcessInfoProvider : IProcessInfoProvider
    {
        private readonly Dictionary<uint, ProcessInfo> _processes = [];

        public int CallCount { get; private set; }

        public void Set(uint processId, string path, long startTime, bool packaged = false) =>
            _processes[processId] = new ProcessInfo(path, startTime, packaged);

        public void Remove(uint processId) => _processes.Remove(processId);

        public bool TryGetProcessInfo(uint processId, out ProcessInfo? info)
        {
            CallCount++;
            return _processes.TryGetValue(processId, out info);
        }
    }

    private sealed class FakeVolumeResolver : IDeviceVolumeResolver
    {
        public bool TryResolve(string deviceName, out string driveLetter)
        {
            driveLetter = "c:";
            return string.Equals(deviceName, @"\device\harddiskvolume3", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static (ProcessIdentityResolver Resolver, FakeProcessInfoProvider Provider) Create(
        int maxEntries = 1024)
    {
        var provider = new FakeProcessInfoProvider();
        var resolver = new ProcessIdentityResolver(
            provider, new PathNormalizer(new FakeVolumeResolver()), maxEntries);

        return (resolver, provider);
    }

    // -- Résolution nominale --------------------------------------------------

    [Fact]
    public void ProcessusConnu_EstResoluAvecSonCheminNormalise()
    {
        (ProcessIdentityResolver resolver, FakeProcessInfoProvider provider) = Create();
        provider.Set(1234, @"C:\Program Files\Steam\Steam.exe", startTime: 100);

        ResolvedProcess? resolved = resolver.Resolve(1234);

        resolved.Should().NotBeNull();
        resolved!.NormalizedPath.Should().Be(@"c:\program files\steam\steam.exe");
        resolved.ExecutableName.Should().Be("steam.exe");
        resolved.ProcessId.Should().Be(1234u);
    }

    [Fact]
    public void ProcessusInconnu_NEstPasResolu()
    {
        // Son trafic ne sera JAMAIS limite : dans le doute, on laisse passer.
        (ProcessIdentityResolver resolver, _) = Create();

        resolver.Resolve(9999).Should().BeNull();
    }

    [Fact]
    public void ApplicationPackagee_EstSignaleeCommeTelle()
    {
        // FR-038 : les applications du Store sont supportees, mais leur chemin sous
        // WindowsApps merite d'etre signale a l'interface.
        (ProcessIdentityResolver resolver, FakeProcessInfoProvider provider) = Create();
        provider.Set(50, @"C:\Program Files\WindowsApps\App_1.0\app.exe", 100, packaged: true);

        resolver.Resolve(50)!.IsPackaged.Should().BeTrue();
    }

    // -- Cache ----------------------------------------------------------------

    [Fact]
    public void ResolutionsRepetees_NInterrogentLeSystemeQuUneFoisParChangement()
    {
        // La resolution est appelee pour chaque flux etabli : sans cache, le service
        // interrogerait le systeme des milliers de fois par seconde.
        (ProcessIdentityResolver resolver, FakeProcessInfoProvider provider) = Create();
        provider.Set(1234, @"C:\App\app.exe", startTime: 100);

        for (int i = 0; i < 10; i++)
        {
            resolver.Resolve(1234).Should().NotBeNull();
        }

        // Le fournisseur est toujours interroge pour connaitre l'heure de demarrage — c'est
        // ce qui permet de detecter la reutilisation — mais la normalisation, elle, n'est
        // faite qu'une fois. Le cache renvoie la meme instance.
        resolver.Resolve(1234).Should().BeSameAs(resolver.Resolve(1234));
        resolver.CacheCount.Should().Be(1);
    }

    // -- Garde anti-réutilisation d'identifiant -------------------------------

    [Fact]
    public void IdentifiantReutiliseParUnAutreProcessus_EstResoluANeuf()
    {
        // LE test qui compte. Sans la garde, le trafic du nouveau processus serait attribue
        // a l'ancienne application, donc limite selon sa regle.
        (ProcessIdentityResolver resolver, FakeProcessInfoProvider provider) = Create();
        provider.Set(1234, @"C:\Ancien\ancien.exe", startTime: 100);

        ResolvedProcess? first = resolver.Resolve(1234);
        first!.ExecutableName.Should().Be("ancien.exe");

        // Meme identifiant, heure de demarrage differente : c'est un autre processus.
        provider.Set(1234, @"C:\Nouveau\nouveau.exe", startTime: 200);

        ResolvedProcess? second = resolver.Resolve(1234);

        second!.ExecutableName.Should().Be("nouveau.exe");
        second.NormalizedPath.Should().Be(@"c:\nouveau\nouveau.exe");
        second.StartTime.Should().Be(200);
    }

    [Fact]
    public void MemeIdentifiantEtMemeHeureDeDemarrage_ResteLeMemeProcessus()
    {
        (ProcessIdentityResolver resolver, FakeProcessInfoProvider provider) = Create();
        provider.Set(1234, @"C:\App\app.exe", startTime: 100);

        ResolvedProcess? first = resolver.Resolve(1234);
        ResolvedProcess? second = resolver.Resolve(1234);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void ProcessusDisparu_PurgeSonEntreeDeCache()
    {
        // Conserver l'entree reviendrait a attribuer du trafic a un processus qui n'existe
        // plus, et a ressusciter cette attribution si l'identifiant etait recycle.
        (ProcessIdentityResolver resolver, FakeProcessInfoProvider provider) = Create();
        provider.Set(1234, @"C:\App\app.exe", startTime: 100);
        resolver.Resolve(1234).Should().NotBeNull();

        provider.Remove(1234);

        resolver.Resolve(1234).Should().BeNull();
        resolver.CacheCount.Should().Be(0);
    }

    // -- Chemins illisibles ---------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CheminVide_NeProduitPasDIdentiteInventee(string path)
    {
        (ProcessIdentityResolver resolver, FakeProcessInfoProvider provider) = Create();
        provider.Set(1234, path, startTime: 100);

        resolver.Resolve(1234).Should().BeNull();
        resolver.CacheCount.Should().Be(0);
    }

    // -- Cache borné ----------------------------------------------------------

    [Fact]
    public void CacheAuMaximum_ResteBorne()
    {
        (ProcessIdentityResolver resolver, FakeProcessInfoProvider provider) = Create(maxEntries: 10);

        for (uint pid = 1; pid <= 50; pid++)
        {
            provider.Set(pid, $@"C:\App{pid}\app.exe", startTime: pid);
            resolver.Resolve(pid).Should().NotBeNull();
        }

        resolver.CacheCount.Should().BeLessThanOrEqualTo(10);
    }

    [Fact]
    public void EntreeEvincee_EstSimplementResolueANouveau()
    {
        // L'eviction est une precaution memoire, pas une perte d'exactitude.
        (ProcessIdentityResolver resolver, FakeProcessInfoProvider provider) = Create(maxEntries: 2);
        provider.Set(1, @"C:\A\a.exe", 1);
        provider.Set(2, @"C:\B\b.exe", 2);
        provider.Set(3, @"C:\C\c.exe", 3);

        resolver.Resolve(1);
        resolver.Resolve(2);
        resolver.Resolve(3);

        resolver.Resolve(1)!.ExecutableName.Should().Be("a.exe");
    }

    // -- Oubli et vidage ------------------------------------------------------

    [Fact]
    public void Forget_RetireLEntree()
    {
        (ProcessIdentityResolver resolver, FakeProcessInfoProvider provider) = Create();
        provider.Set(1234, @"C:\App\app.exe", 100);
        resolver.Resolve(1234);

        resolver.Forget(1234);

        resolver.CacheCount.Should().Be(0);
    }

    [Fact]
    public void Clear_VideLeCache()
    {
        (ProcessIdentityResolver resolver, FakeProcessInfoProvider provider) = Create();
        provider.Set(1, @"C:\A\a.exe", 1);
        provider.Set(2, @"C:\B\b.exe", 2);
        resolver.Resolve(1);
        resolver.Resolve(2);

        resolver.Clear();

        resolver.CacheCount.Should().Be(0);
    }

    // -- Robustesse -----------------------------------------------------------

    [Fact]
    public void Constructeur_SansFournisseur_Leve()
    {
        Action act = () => _ = new ProcessIdentityResolver(
            null!, new PathNormalizer(new FakeVolumeResolver()));

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructeur_SansNormaliseur_Leve()
    {
        Action act = () => _ = new ProcessIdentityResolver(new FakeProcessInfoProvider(), null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
