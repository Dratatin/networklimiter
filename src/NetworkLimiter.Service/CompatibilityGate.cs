using System.Globalization;
using System.Runtime.InteropServices;

namespace NetworkLimiter.Service;

/// <summary>Plateforme observée au démarrage.</summary>
/// <param name="OsBuild">Numéro de build du système.</param>
/// <param name="ProcessArchitecture">Architecture du processus.</param>
/// <param name="IsWindows">Indique si le système est Windows.</param>
public sealed record PlatformInfo(int OsBuild, Architecture ProcessArchitecture, bool IsWindows);

/// <summary>Verdict de compatibilité.</summary>
public enum CompatibilityVerdict
{
    /// <summary>La plateforme est dans la matrice déclarée.</summary>
    Supported,

    /// <summary>Le système n'est pas Windows.</summary>
    UnsupportedOperatingSystem,

    /// <summary>La version de Windows est antérieure au minimum supporté.</summary>
    UnsupportedOsVersion,

    /// <summary>L'architecture n'a pas de pilote WinDivert disponible.</summary>
    UnsupportedArchitecture,
}

/// <summary>Résultat de la vérification de compatibilité.</summary>
/// <param name="Verdict">Verdict.</param>
/// <param name="Diagnostic">Message nommant la cause, ou <c>null</c> si supporté.</param>
public sealed record CompatibilityResult(CompatibilityVerdict Verdict, string? Diagnostic);

/// <summary>
/// Vérifie que la machine appartient à la matrice de compatibilité déclarée.
/// </summary>
/// <remarks>
/// <para>
/// Le principe II impose de refuser explicitement de démarrer hors matrice plutôt que de
/// dégrader silencieusement. Un limiteur qui démarre sans pouvoir limiter est pire qu'un
/// limiteur absent : l'utilisateur croit sa connexion protégée alors qu'elle ne l'est pas.
/// </para>
/// <para>
/// La décision est une fonction pure de la plateforme observée, ce qui permet de la tester
/// sur toute la matrice — ARM64 compris, qu'aucune machine de développement x64 ne peut
/// exercer autrement.
/// </para>
/// </remarks>
public static class CompatibilityGate
{
    /// <summary>
    /// Build minimal supporté : 19045, soit Windows 10 22H2.
    /// </summary>
    /// <remarks>
    /// Les versions antérieures ne reçoivent plus de mises à jour de sécurité ; les tester
    /// reviendrait à valider l'outil sur un socle vulnérable.
    /// </remarks>
    public const int MinimumOsBuild = 19045;

    /// <summary>Indique si un verdict autorise la limitation.</summary>
    public static bool AllowsInterception(CompatibilityVerdict verdict) =>
        verdict == CompatibilityVerdict.Supported;

    /// <summary>Évalue une plateforme.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="platform"/> est <c>null</c>.</exception>
    public static CompatibilityResult Evaluate(PlatformInfo platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        if (!platform.IsWindows)
        {
            return new CompatibilityResult(
                CompatibilityVerdict.UnsupportedOperatingSystem,
                "NetworkLimiter ne fonctionne que sous Windows : la limitation repose sur un " +
                "pilote réseau Windows.");
        }

        // L'architecture est evaluee AVANT la version : elle est redhibitoire et definitive,
        // alors qu'une version trop ancienne se corrige par une mise a jour. Nommer d'abord
        // la cause insurmontable evite d'envoyer l'utilisateur mettre a jour Windows pour rien.
        if (platform.ProcessArchitecture != Architecture.X64)
        {
            return new CompatibilityResult(
                CompatibilityVerdict.UnsupportedArchitecture,
                DescribeArchitecture(platform.ProcessArchitecture));
        }

        if (platform.OsBuild < MinimumOsBuild)
        {
            string observed = platform.OsBuild.ToString(CultureInfo.InvariantCulture);
            string minimum = MinimumOsBuild.ToString(CultureInfo.InvariantCulture);

            return new CompatibilityResult(
                CompatibilityVerdict.UnsupportedOsVersion,
                $"Windows build {observed} n'est pas supporté. " +
                $"Le minimum est le build {minimum} (Windows 10 22H2). " +
                "Mettez Windows à jour, puis relancez l'installation.");
        }

        return new CompatibilityResult(CompatibilityVerdict.Supported, null);
    }

    /// <summary>Observe la plateforme courante.</summary>
    public static PlatformInfo GetCurrentPlatform() =>
        new(
            Environment.OSVersion.Version.Build,
            RuntimeInformation.ProcessArchitecture,
            OperatingSystem.IsWindows());

    private static string DescribeArchitecture(Architecture architecture) => architecture switch
    {
        // La cause doit etre nommee. « Architecture non supportee » laisserait croire a un
        // oubli reparable, alors que WinDivert ne publie aucun pilote ARM64 et qu'un pilote
        // noyau ne s'execute pas sous l'emulation x64 de Windows ARM64.
        Architecture.Arm64 or Architecture.Arm =>
            "L'architecture ARM64 n'est pas supportée : WinDivert, le pilote sur lequel repose " +
            "la limitation, n'existe pas pour ARM64, et un pilote noyau ne peut pas s'exécuter " +
            "sous émulation. Ce n'est pas un réglage à changer.",

        Architecture.X86 =>
            "Windows 32 bits n'est pas supporté. NetworkLimiter exige une version 64 bits (x64).",

        _ =>
            $"L'architecture {architecture} n'est pas supportée. NetworkLimiter exige x64.",
    };
}
