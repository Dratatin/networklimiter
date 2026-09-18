using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using NetworkLimiter.Service.Safety;
using Serilog;
using Serilog.Events;

namespace NetworkLimiter.Service;

/// <summary>Point d'entrée du service NetworkLimiter.</summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private static int Main(string[] args)
    {
        // Le diagnostic precede tout, y compris la verification de compatibilite : il doit
        // pouvoir s'executer justement quand quelque chose ne va pas, et rendre compte de ce
        // qui bloque plutot que de sortir en silence.
        if (Array.Exists(args, arg => string.Equals(arg, "--diagnose", StringComparison.Ordinal)))
        {
            return Diagnostics.Run();
        }

        // Commandes de regles : aide au developpement en attendant l'interface (T072, T073).
        // Elles precedent la creation de l'hote, sinon le service demarrerait aussi.
        switch (args.Length > 0 ? args[0] : null)
        {
            case "--add-rule":
                return RuleCommands.Run(() => RuleCommands.AddRule(args));
            case "--list-rules":
                return RuleCommands.Run(RuleCommands.ListRules);
            case "--clear-rules":
                return RuleCommands.Run(RuleCommands.ClearRules);
            case "--probe-flow":
                return FlowProbe.Run(args);
            default:
                break;
        }

        // La verification de compatibilite precede TOUT le reste, journalisation comprise :
        // inutile de creer des repertoires et d'ouvrir des fichiers sur une machine ou le
        // service ne pourra de toute facon rien faire (FR-035, principe II).
        CompatibilityResult compatibility = CompatibilityGate.Evaluate(
            CompatibilityGate.GetCurrentPlatform());

        if (!CompatibilityGate.AllowsInterception(compatibility.Verdict))
        {
            Console.Error.WriteLine(compatibility.Diagnostic);
            WriteStartupFailureToEventLog(compatibility.Diagnostic);
            return 2;
        }

        Directory.CreateDirectory(ServicePaths.LogDirectory);
        ConfigureLogging(Array.Exists(args, arg => string.Equals(arg, "--verbose", StringComparison.Ordinal)));

        try
        {
            Log.Information("Démarrage du service, build {OsBuild}, architecture {Architecture}.",
                CompatibilityGate.GetCurrentPlatform().OsBuild,
                CompatibilityGate.GetCurrentPlatform().ProcessArchitecture);

            HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

            builder.Services.AddSerilog();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(Log.Logger);
            builder.Services.AddHostedService<InterceptionWorker>();
            builder.Services.AddWindowsService(options => options.ServiceName = "NetworkLimiter");

            using IHost host = builder.Build();
            host.Run();

            return 0;
        }
        catch (Exception exception)
        {
            // Dernier filet : une exception qui remonte jusqu'ici est consignee avant que le
            // processus ne meure. Les handles d'interception se ferment avec le processus,
            // donc le trafic redevient libre (principe IV).
            Log.Fatal(exception, "Arrêt du service sur une défaillance non gérée.");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void ConfigureLogging(bool verbose)
    {
        LoggerConfiguration configuration = new LoggerConfiguration()
            .Enrich.FromLogContext();

        // « Debug » publie les compteurs d'etape de la boucle, seconde par seconde. Hors
        // diagnostic, c'est une ligne par seconde des qu'il y a du trafic : trop pour un
        // service permanent, indispensable quand on cherche pourquoi une limite ne prend pas.
        configuration = verbose
            ? configuration.MinimumLevel.Debug()
            : configuration.MinimumLevel.Information();

        // Lance a la main depuis un terminal, le service doit dire ce qu'il fait a l'ecran :
        // sans cela, la seule facon d'observer un demarrage est d'aller lire un fichier, ce
        // qui rend toute verification penible au moment ou elle compte le plus.
        if (!WindowsServiceHelpers.IsWindowsService())
        {
            configuration = configuration.WriteTo.Sink(new ConsoleSink());
        }

        Log.Logger = configuration
            .WriteTo.File(
                ServicePaths.LogFileTemplate,
                rollingInterval: RollingInterval.Day,
                // Culture invariante : un journal doit rester analysable quelle que soit la
                // locale de la machine qui l'a produit.
                formatProvider: CultureInfo.InvariantCulture,
                // Bornes obligatoires (principe VI) : un journal non borne sur un service
                // qui tourne en permanence finit par remplir le disque systeme.
                fileSizeLimitBytes: 16 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 7,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.EventLog(
                source: "NetworkLimiter",
                logName: "Application",
                manageEventSource: false,
                formatProvider: CultureInfo.InvariantCulture,
                restrictedToMinimumLevel: LogEventLevel.Error)
            .CreateLogger();
    }

    /// <summary>Écrit les événements sur la sortie standard.</summary>
    /// <remarks>
    /// Écrit à la main plutôt que tiré d'un paquet : <c>Serilog.Sinks.Console</c> ne servirait
    /// qu'à l'exécution en console, et chaque dépendance supplémentaire est une surface de
    /// chaîne d'approvisionnement à surveiller pour un service qui tourne en SYSTEM.
    /// </remarks>
    private sealed class ConsoleSink : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);

            TextWriter output = logEvent.Level >= LogEventLevel.Error ? Console.Error : Console.Out;

            output.WriteLine(string.Create(
                CultureInfo.CurrentCulture,
                $"{logEvent.Timestamp:HH:mm:ss} [{logEvent.Level.ToString()[..3].ToUpperInvariant()}] " +
                $"{logEvent.RenderMessage(CultureInfo.CurrentCulture)}"));

            if (logEvent.Exception is not null)
            {
                output.WriteLine(logEvent.Exception);
            }
        }
    }

    private static void WriteStartupFailureToEventLog(string? diagnostic)
    {
        try
        {
            Log.Logger = new LoggerConfiguration()
                .WriteTo.EventLog(
                    "NetworkLimiter",
                    "Application",
                    manageEventSource: false,
                    formatProvider: CultureInfo.InvariantCulture)
                .CreateLogger();

            Log.Error("Refus de démarrage : {Diagnostic}", diagnostic);
            Log.CloseAndFlush();
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            // La source d'evenements n'est pas enregistree (installation incomplete) ou
            // l'acces est refuse. Le message est deja sur stderr : ne pas transformer un
            // refus de demarrage explicite en plantage opaque.
        }
    }
}
