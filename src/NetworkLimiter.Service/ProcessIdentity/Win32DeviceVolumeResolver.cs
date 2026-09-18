using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NetworkLimiter.Core.Classification;

namespace NetworkLimiter.Service.ProcessIdentity;

/// <summary>
/// Traduit les noms de périphérique Windows en lettres de lecteur.
/// </summary>
/// <remarks>
/// Implémente l'abstraction déclarée dans <c>NetworkLimiter.Core</c>, qui ne peut pas appeler
/// <c>QueryDosDevice</c> sans dépendre de Windows. La table est construite une fois puis
/// rafraîchie à la demande : les volumes changent rarement, mais un disque externe branché en
/// cours de route ne doit pas rendre un chemin irrésoluble pour toujours.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class Win32DeviceVolumeResolver : IDeviceVolumeResolver
{
    private readonly Lock _gate = new();
    private Dictionary<string, string> _deviceToDrive = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    /// <inheritdoc />
    public bool TryResolve(string deviceName, out string driveLetter)
    {
        ArgumentNullException.ThrowIfNull(deviceName);

        lock (_gate)
        {
            if (!_loaded)
            {
                Refresh();
            }

            if (_deviceToDrive.TryGetValue(deviceName, out string? found))
            {
                driveLetter = found;
                return true;
            }

            // Volume inconnu : il a peut-etre ete monte apres le premier chargement.
            Refresh();

            if (_deviceToDrive.TryGetValue(deviceName, out found))
            {
                driveLetter = found;
                return true;
            }
        }

        driveLetter = string.Empty;
        return false;
    }

    private void Refresh()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Span<char> buffer = stackalloc char[1024];

        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            string drive = $"{letter}:";

            uint length = QueryDosDevice(drive, buffer, (uint)buffer.Length);
            if (length == 0)
            {
                continue;
            }

            // QueryDosDevice rend une liste de chaines terminees par un caractere nul ;
            // seule la premiere nous interesse.
            int end = buffer.IndexOf('\0');
            string device = new(buffer[..(end < 0 ? (int)length : end)]);

            if (!string.IsNullOrEmpty(device))
            {
                map[device] = drive;
            }
        }

        _deviceToDrive = map;
        _loaded = true;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint QueryDosDevice(string deviceName, Span<char> targetPath, uint max);
}
