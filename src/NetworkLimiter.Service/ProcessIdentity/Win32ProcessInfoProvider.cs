using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace NetworkLimiter.Service.ProcessIdentity;

/// <summary>
/// Lit les informations d'un processus via les API Windows.
/// </summary>
/// <remarks>
/// <para>
/// Le service s'exécutant en <c>LocalSystem</c>, il dispose de
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> sur les processus des autres sessions, y compris
/// élevés. Certains processus protégés restent inaccessibles : leur trafic n'est alors jamais
/// limité, ce qui est le comportement voulu — dans le doute, on laisse passer.
/// </para>
/// <para>
/// Aucune exception ne remonte : une disparition de processus entre l'événement de flux et
/// cette lecture est un cas courant, pas une défaillance.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class Win32ProcessInfoProvider : IProcessInfoProvider
{
    private static readonly string PackagedPathMarker =
        Path.DirectorySeparatorChar + "windowsapps" + Path.DirectorySeparatorChar;

    /// <inheritdoc />
    public bool TryGetProcessInfo(uint processId, out ProcessInfo? info)
    {
        info = null;

        try
        {
            using Process process = Process.GetProcessById((int)processId);

            string? path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            bool packaged = path.Contains(PackagedPathMarker, StringComparison.OrdinalIgnoreCase);

            info = new ProcessInfo(path, process.StartTime.ToUniversalTime().Ticks, packaged);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException           // le processus n'existe plus
                      or InvalidOperationException   // il s'est termine pendant la lecture
                      or Win32Exception              // acces refuse, processus protege
                      or NotSupportedException)
        {
            // Cas courants, pas des defaillances. Le trafic du processus reste non limite.
            return false;
        }
    }
}
