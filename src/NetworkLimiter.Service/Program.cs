using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        ConfigureLogging();

        try
        {
            Log.Information("Démarrage du service, build {OsBuild}, architecture {Architecture}.",
                CompatibilityGate.GetCurrentPlatform().OsBuild,
                CompatibilityGate.GetCurrentPlatform().ProcessArchitecture);

            HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

            builder.Services.AddSerilog();
            builder.Services.AddSingleton(TimeProvider.System);
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

    private static void ConfigureLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
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
