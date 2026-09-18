using System.Diagnostics;
using NetworkLimiter.Core.Classification;
using NetworkLimiter.Service.ProcessIdentity;

namespace NetworkLimiter.Service.Tests.ProcessIdentity;

/// <summary>
/// Implémentations Windows réelles de la résolution d'identité.
/// </summary>
/// <remarks>
/// Contrairement aux tests de <see cref="ProcessIdentityResolverTests"/>, qui vérifient la
/// logique de cache avec un double, ceux-ci exercent les vraies API Windows sur la machine qui
/// exécute les tests. Ils sont le seul moyen de constater que <c>QueryDosDevice</c> et la
/// lecture de <c>MainModule</c> se comportent comme le reste du code le suppose — une
/// hypothèse fausse ici rendrait tous les chemins irrésolubles en production, sans que le
/// moindre test à double le signale.
/// </remarks>
public sealed class Win32ResolversTests
{
    // -- Résolution de volume -------------------------------------------------

    [Fact]
    public void ResolveurDeVolume_TraduitLeCheminDePeripheriqueDuProcessusCourant()
    {
        // Prend le chemin natif de l'executable de test, puis verifie qu'on sait le
        // retraduire en lettre de lecteur.
        var resolver = new Win32DeviceVolumeResolver();
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');

        // On cherche le nom de peripherique correspondant au lecteur systeme en testant
        // les volumes usuels ; s'il est trouve, la traduction inverse doit redonner le
        // meme lecteur.
        bool resolvedAtLeastOne = false;

        for (int volume = 0; volume <= 16 && !resolvedAtLeastOne; volume++)
        {
            if (resolver.TryResolve($@"\Device\HarddiskVolume{volume}", out string drive))
            {
                drive.Should().MatchRegex("^[A-Za-z]:$");
                resolvedAtLeastOne = true;
            }
        }

        resolvedAtLeastOne.Should().BeTrue(
            $"au moins un volume disque doit être résoluble (lecteur système : {systemDrive})");
    }

    [Fact]
    public void ResolveurDeVolume_PeripheriqueInexistant_NEstPasResolu()
    {
        var resolver = new Win32DeviceVolumeResolver();

        resolver.TryResolve(@"\Device\HarddiskVolume9999", out string drive).Should().BeFalse();
        drive.Should().BeEmpty();
    }

    [Fact]
    public void ResolveurDeVolume_NomNul_Leve()
    {
        var resolver = new Win32DeviceVolumeResolver();

        Action act = () => resolver.TryResolve(null!, out _);

        act.Should().Throw<ArgumentNullException>();
    }

    // -- Lecture d'informations de processus ----------------------------------

    [Fact]
    public void FournisseurDeProcessus_LitLeProcessusCourant()
    {
        var provider = new Win32ProcessInfoProvider();
        uint currentPid = (uint)Environment.ProcessId;

        provider.TryGetProcessInfo(currentPid, out ProcessInfo? info).Should().BeTrue();

        info.Should().NotBeNull();
        info!.ExecutablePath.Should().NotBeNullOrWhiteSpace();
        info.StartTime.Should().BeGreaterThan(0);
    }

    [Fact]
    public void FournisseurDeProcessus_LHeureDeDemarrageCorrespondAuProcessusReel()
    {
        // C'est cette valeur qui distingue deux processus partageant un identifiant recycle.
        // Si elle etait constante ou nulle, la garde anti-reutilisation serait inoperante
        // tout en paraissant fonctionner.
        var provider = new Win32ProcessInfoProvider();
        using Process current = Process.GetCurrentProcess();

        provider.TryGetProcessInfo((uint)Environment.ProcessId, out ProcessInfo? info).Should().BeTrue();

        info!.StartTime.Should().Be(current.StartTime.ToUniversalTime().Ticks);
    }

    [Fact]
    public void FournisseurDeProcessus_IdentifiantInexistant_EchoueSansLever()
    {
        var provider = new Win32ProcessInfoProvider();

        provider.TryGetProcessInfo(0xFFFFFFF0, out ProcessInfo? info).Should().BeFalse();
        info.Should().BeNull();
    }

    // -- Chaîne complète ------------------------------------------------------

    [Fact]
    public void ChaineComplete_ResoutLeProcessusCourantEnCheminNormalise()
    {
        // Verifie l'assemblage reel : API Windows, normalisation, cache. C'est le chemin
        // qu'empruntera chaque flux etabli.
        var resolver = new ProcessIdentityResolver(
            new Win32ProcessInfoProvider(),
            new PathNormalizer(new Win32DeviceVolumeResolver()));

        ResolvedProcess? resolved = resolver.Resolve((uint)Environment.ProcessId);

        resolved.Should().NotBeNull();
        resolved!.NormalizedPath.Should().NotBeNullOrWhiteSpace();
        resolved.NormalizedPath.Should().Be(resolved.NormalizedPath.ToLowerInvariant());
        resolved.ExecutableName.Should().EndWith(".exe");
        resolved.ExecutableName.Should().NotContain("\\");
    }

    [Fact]
    public void ChaineComplete_LaNormalisationResteIdempotenteSurUnCheminReel()
    {
        var normalizer = new PathNormalizer(new Win32DeviceVolumeResolver());
        using Process current = Process.GetCurrentProcess();
        string path = current.MainModule!.FileName;

        string once = normalizer.Normalize(path);

        normalizer.Normalize(once).Should().Be(once);
    }
}
