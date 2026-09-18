using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace NetworkLimiter.Integration.Tests.Security;

/// <summary>
/// Exécute du code sous un jeton privé des privilèges d'administrateur.
/// </summary>
/// <remarks>
/// <para>
/// SC-013 exige de prouver qu'un utilisateur standard ne peut pas modifier la configuration.
/// La façon évidente — créer un second compte Windows — rendrait le test impossible à exécuter
/// en intégration continue et le reléguerait à une vérification manuelle occasionnelle,
/// c'est-à-dire, en pratique, à rien.
/// </para>
/// <para>
/// À la place, on dérive du jeton courant un jeton <b>restreint</b> dont le SID Administrateurs
/// est désactivé et dont tous les privilèges sont retirés. C'est le mécanisme que Windows
/// emploie lui-même pour le jeton filtré d'UAC : le résultat est fidèle à ce que voit un
/// utilisateur standard, et le test tourne partout, à chaque exécution.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class RestrictedToken
{
    private const uint DisableMaxPrivilege = 0x1;

    /// <summary>Exécute une action sous un jeton sans privilèges d'administrateur.</summary>
    /// <returns><c>false</c> si le jeton restreint n'a pas pu être créé.</returns>
    public static bool TryRunWithoutAdministrator(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        using WindowsIdentity current = WindowsIdentity.GetCurrent();

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        byte[] sidBytes = new byte[administrators.BinaryLength];
        administrators.GetBinaryForm(sidBytes, 0);

        nint sidMemory = Marshal.AllocHGlobal(sidBytes.Length);

        try
        {
            Marshal.Copy(sidBytes, 0, sidMemory, sidBytes.Length);

            SidAndAttributes[] toDisable = [new SidAndAttributes { Sid = sidMemory, Attributes = 0 }];

            if (!CreateRestrictedToken(
                    current.Token,
                    DisableMaxPrivilege,
                    (uint)toDisable.Length,
                    toDisable,
                    0, null,
                    0, null,
                    out SafeAccessTokenHandle restricted))
            {
                return false;
            }

            using (restricted)
            {
                WindowsIdentity.RunImpersonated(restricted, action);
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(sidMemory);
        }
    }

    /// <summary>Vérifie que le jeton courant n'est pas administrateur.</summary>
    public static bool CurrentIsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public long Luid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CreateRestrictedToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateRestrictedToken(
        nint existingTokenHandle,
        uint flags,
        uint disableSidCount,
        SidAndAttributes[]? sidsToDisable,
        uint deletePrivilegeCount,
        LuidAndAttributes[]? privilegesToDelete,
        uint restrictedSidCount,
        SidAndAttributes[]? sidsToRestrict,
        out SafeAccessTokenHandle newTokenHandle);
}
