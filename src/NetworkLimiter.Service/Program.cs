using Microsoft.Extensions.Hosting;

namespace NetworkLimiter.Service;

/// <summary>
/// Point d'entrée du service NetworkLimiter.
/// </summary>
/// <remarks>
/// Squelette de Phase 1 : le service ne fait encore rien. L'hébergement en service
/// Windows, la journalisation Serilog, la vérification de compatibilité, l'ouverture
/// des handles WinDivert et la machine à états fail-open sont ajoutés en Phase 2
/// (tâches T033 à T037).
/// </remarks>
internal static class Program
{
    private static void Main(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        using IHost host = builder.Build();
        host.Run();
    }
}
