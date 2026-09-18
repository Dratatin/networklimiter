using System.Globalization;
using System.Runtime.Versioning;
using System.ServiceProcess;
using NetworkLimiter.Service.Interception;

namespace NetworkLimiter.Service;

/// <summary>
/// Diagnostic de l'environnement d'interception.
/// </summary>
/// <remarks>
/// <para>
/// Répond à la question qu'aucun test unitaire ne peut trancher : <b>Windows accepte-t-il de
/// charger le pilote sur cette machine ?</b> Secure Boot, l'intégrité du code protégée par
/// l'hyperviseur, ou un antivirus peuvent le refuser, et cela ne se découvre qu'au premier
/// appel à <c>WinDivertOpen</c>.
/// </para>
/// <para>
/// Volontairement intégré au service plutôt qu'écrit comme outil séparé : il exerce ainsi le
/// <b>code de production</b> — mêmes liaisons P/Invoke, mêmes filtres, mêmes drapeaux
/// d'ouverture. Un diagnostic qui passerait par un chemin parallèle pourrait réussir là où le
/// service échoue, ce qui serait pire que pas de diagnostic du tout.
/// </para>
/// <para>
/// Il referme immédiatement tout ce qu'il ouvre : lancer un diagnostic ne doit pas laisser du
/// trafic détourné derrière soi (principe IV).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class Diagnostics
{
    private const string DriverServiceName = "WinDivert";

    /// <summary>Exécute le diagnostic et rend un code de sortie.</summary>
    /// <returns><c>0</c> si l'interception est possible, sinon un code non nul.</returns>
    public static int Run()
    {
        Console.WriteLine("Diagnostic NetworkLimiter");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();

        bool ok = true;

        ok &= CheckPlatform();
        ok &= CheckElevation();
        ok &= CheckLibraryPresent();
        ok &= CheckFilters();
        ok &= CheckDriverService();

        if (!ok)
        {
            Console.WriteLine();
            Console.WriteLine("Diagnostic interrompu : corrigez les points ci-dessus avant d'aller plus loin.");
            return 1;
        }

        ok &= TryOpen("couche FLOW (attribution des flux aux processus)",
                      WinDivertInterceptor.CreateFlowInterceptor);

        ok &= TryOpen("couche NETWORK (mise en forme du trafic)",
                      WinDivertInterceptor.CreateNetworkInterceptor);

        Console.WriteLine();
        Console.WriteLine(new string('=', 60));

        if (ok)
        {
            Console.WriteLine("RESULTAT : l'interception est possible sur cette machine.");
            Console.WriteLine("Les handles ont ete refermes ; aucun trafic n'est detourne.");
            return 0;
        }

        Console.WriteLine("RESULTAT : l'interception N'EST PAS possible sur cette machine.");
        return 2;
    }

    private static bool CheckPlatform()
    {
        PlatformInfo platform = CompatibilityGate.GetCurrentPlatform();
        CompatibilityResult result = CompatibilityGate.Evaluate(platform);

        string build = platform.OsBuild.ToString(CultureInfo.InvariantCulture);
        Report("Matrice de compatibilite",
               CompatibilityGate.AllowsInterception(result.Verdict),
               $"Windows build {build}, {platform.ProcessArchitecture}",
               result.Diagnostic);

        return CompatibilityGate.AllowsInterception(result.Verdict);
    }

    private static bool CheckElevation()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        bool elevated = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

        Report("Privileges administrateur", elevated,
               elevated ? "session elevee" : "session NON elevee",
               "Relancez depuis une invite elevee : l'ouverture d'un handle WinDivert l'exige.");

        return elevated;
    }

    private static bool CheckLibraryPresent()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "WinDivert.dll");
        bool present = File.Exists(path);

        Report("Bibliotheque WinDivert.dll", present,
               present ? path : "absente",
               "Executez ./tools/restore-windivert.ps1 depuis la racine du depot.");

        return present;
    }

    private static bool CheckFilters()
    {
        try
        {
            FilterBuilder.Validate(FilterBuilder.FlowFilter, WinDivertLayer.Flow);
            FilterBuilder.Validate(FilterBuilder.NetworkFilter, WinDivertLayer.Network);

            Report("Filtres d'interception", true, "acceptes par WinDivert", null);
            return true;
        }
        catch (Exception exception) when (exception is InvalidFilterException or DllNotFoundException)
        {
            Report("Filtres d'interception", false, exception.Message, null);
            return false;
        }
    }

    private static bool CheckDriverService()
    {
        try
        {
            using var service = new ServiceController(DriverServiceName);
            ServiceControllerStatus status = service.Status;

            // « Arrete » est l'etat NORMAL : le pilote est enregistre a demarrage a la
            // demande et ne se charge qu'a l'ouverture d'un handle.
            Report("Service pilote WinDivert", true, $"enregistre, etat : {status}", null);
            return true;
        }
        catch (InvalidOperationException)
        {
            Report("Service pilote WinDivert", false, "non enregistre",
                   "Executez ./tools/register-windivert-dev.ps1 depuis une invite elevee.");
            return false;
        }
    }

    private static bool TryOpen(string label, Func<WinDivertInterceptor> factory)
    {
        Console.WriteLine();
        Console.WriteLine($"Ouverture de la {label}...");

        WinDivertInterceptor? interceptor = null;

        try
        {
            interceptor = factory();
            interceptor.Open();

            Report("  Ouverture", true, "reussie — le pilote est charge et accepte par Windows", null);
            return true;
        }
        catch (InterceptionUnavailableException exception)
        {
            Report("  Ouverture", false, exception.Message, Explain(exception.Win32ErrorCode));
            return false;
        }
        catch (DllNotFoundException exception)
        {
            Report("  Ouverture", false, exception.Message,
                   "WinDivert.dll est introuvable a cote de l'executable.");
            return false;
        }
        finally
        {
            // Referme systematiquement, y compris en cas d'echec partiel : un diagnostic ne
            // doit jamais laisser du trafic detourne derriere lui.
            interceptor?.Close();
            interceptor?.Dispose();
        }
    }

    /// <summary>Traduit un code d'erreur Windows en cause probable et en action.</summary>
    private static string? Explain(int win32Error) => win32Error switch
    {
        0 => null,

        2 => "Le fichier du pilote est introuvable. Le service pilote pointe peut-etre vers un " +
             "chemin qui n'existe plus : reenregistrez-le.",

        5 => "Acces refuse. La session doit etre elevee.",

        577 => "WINDOWS A REFUSE LA SIGNATURE DU PILOTE. C'est la cause la plus probable sur une " +
               "machine avec Secure Boot et l'integrite du code protegee par l'hyperviseur (HVCI). " +
               "Verifiez : Securite Windows > Securite des appareils > Isolation du noyau > " +
               "Integrite de la memoire. Si elle est activee, WinDivert ne pourra pas se charger.",

        1275 => "Une strategie de securite bloque le chargement de ce pilote.",

        1058 => "Le service du pilote est desactive.",

        1060 => "Le service du pilote n'existe pas. Executez ./tools/register-windivert-dev.ps1.",

        654 => "Une version differente du pilote a ete chargee puis dechargee. Redemarrez la machine.",

        1450 => "Ressources systeme insuffisantes pour charger le pilote.",

        87 => "Filtre, couche, priorite ou drapeaux d'ouverture invalides.",

        _ => "Code d'erreur Windows inattendu. Notez-le : il identifie la cause exacte.",
    };

    private static void Report(string label, bool ok, string detail, string? hint)
    {
        string mark = ok ? "[ OK ]" : "[ECHEC]";
        Console.WriteLine($"{mark} {label,-32} {detail}");

        if (!ok && hint is not null)
        {
            Console.WriteLine($"        -> {hint}");
        }
    }
}
