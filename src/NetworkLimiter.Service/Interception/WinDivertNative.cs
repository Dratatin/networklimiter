using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NetworkLimiter.Service.Interception;

/// <summary>Couche d'interception WinDivert.</summary>
public enum WinDivertLayer
{
    /// <summary>Paquets de la machine locale. Ne porte pas d'identifiant de processus.</summary>
    Network = 0,

    /// <summary>Paquets en transit.</summary>
    NetworkForward = 1,

    /// <summary>Événements de flux. Porte l'identifiant de processus.</summary>
    Flow = 2,

    /// <summary>Opérations de socket. Porte l'identifiant de processus.</summary>
    Socket = 3,

    /// <summary>Événements WinDivert.</summary>
    Reflect = 4,
}

/// <summary>Paramètres réglables d'un handle WinDivert.</summary>
public enum WinDivertParam
{
    /// <summary>Longueur de la file du noyau, en paquets.</summary>
    QueueLength = 0,

    /// <summary>Temps maximal de séjour d'un paquet dans la file du noyau, en millisecondes.</summary>
    QueueTime = 1,

    /// <summary>Taille maximale de la file du noyau, en octets.</summary>
    QueueSize = 2,
}

/// <summary>Options d'ouverture d'un handle WinDivert (drapeaux combinables).</summary>
[Flags]
public enum WinDivertOpenOptions : ulong
{
    /// <summary>Aucun drapeau.</summary>
    None = 0,

    /// <summary>Copier les paquets au lieu de les détourner. Obligatoire pour la couche Flow.</summary>
    Sniff = 0x0001,

    /// <summary>Rejeter silencieusement les paquets correspondants.</summary>
    Drop = 0x0002,

    /// <summary>Handle en lecture seule. Obligatoire pour la couche Flow.</summary>
    RecvOnly = 0x0004,

    /// <summary>Handle en écriture seule.</summary>
    SendOnly = 0x0008,

    /// <summary>Ne pas installer le pilote : il doit déjà être enregistré.</summary>
    NoInstall = 0x0010,

    /// <summary>Recevoir aussi les fragments IP.</summary>
    Fragments = 0x0020,
}

/// <summary>Mode d'arrêt d'un handle.</summary>
public enum WinDivertShutdown
{
    /// <summary>Arrêter la réception.</summary>
    Recv = 1,

    /// <summary>Arrêter l'émission.</summary>
    Send = 2,

    /// <summary>Arrêter les deux sens.</summary>
    Both = 3,
}

/// <summary>
/// Handle WinDivert, libéré de façon déterministe.
/// </summary>
/// <remarks>
/// Le <see cref="SafeHandle"/> n'est pas un détail d'hygiène ici : tant qu'un handle reste
/// ouvert, le pilote continue de détourner le trafic. Sa fermeture est ce qui rend le réseau
/// à l'utilisateur, et elle doit donc survivre à toutes les voies de sortie du processus,
/// exception non gérée comprise (principe IV).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WinDivertHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Crée un handle invalide.</summary>
    public WinDivertHandle() : base(ownsHandle: true)
    {
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle() => WinDivertNative.WinDivertClose(handle);
}

/// <summary>
/// Liaisons P/Invoke vers <c>WinDivert.dll</c>.
/// </summary>
/// <remarks>
/// <para>
/// Surface volontairement minimale : seules les fonctions réellement utilisées sont liées.
/// Chaque entrée supplémentaire est du code natif de plus à auditer dans un service qui
/// s'exécute en <c>LocalSystem</c>.
/// </para>
/// <para>
/// La bibliothèque est restaurée par <c>tools/restore-windivert.ps1</c>, qui vérifie son
/// empreinte SHA-256 et, pour le pilote, sa signature Authenticode. Elle n'est jamais
/// téléchargée au moment de l'exécution.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class WinDivertNative
{
    private const string Library = "WinDivert.dll";

    /// <summary>Nombre maximal de paquets lus ou écrits en un appel.</summary>
    public const int BatchMax = 0xFF;

    /// <summary>Taille maximale d'un paquet, en octets.</summary>
    public const int MtuMax = 40 + 0xFFFF;

    // SetLastError est INDISPENSABLE ici. Sans lui, le runtime ne capture pas l'erreur
    // Windows et Marshal.GetLastWin32Error() rend une valeur residuelle arbitraire : un
    // echec d'ouverture remonte alors un code faux — ou zero — et devient indiagnosticable.
    // Un test de garde verifie que TOUTE liaison de ce fichier porte cet attribut.
    [LibraryImport(Library, EntryPoint = "WinDivertOpen",
                   StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    internal static partial WinDivertHandle Open(
        string filter,
        WinDivertLayer layer,
        short priority,
        WinDivertOpenOptions flags);

    [LibraryImport(Library, EntryPoint = "WinDivertClose", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinDivertClose(nint handle);

    [LibraryImport(Library, EntryPoint = "WinDivertShutdown", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Shutdown(WinDivertHandle handle, WinDivertShutdown how);

    [LibraryImport(Library, EntryPoint = "WinDivertSetParam", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetParam(WinDivertHandle handle, WinDivertParam param, ulong value);

    /// <summary>
    /// Reçoit un lot de paquets ou d'événements.
    /// </summary>
    /// <remarks>
    /// <paramref name="addressLength"/> est exprimé en <b>octets</b>, pas en nombre d'entrées :
    /// en entrée la taille du tampon, en sortie le nombre d'octets écrits. Le nombre
    /// d'événements s'en déduit par division par la taille d'une adresse. Confondre les deux
    /// conventions ferait lire un nombre d'événements erroné à chaque lot.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "WinDivertRecvEx", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool RecvEx(
        WinDivertHandle handle,
        byte* packet,
        uint packetLength,
        uint* receivedLength,
        ulong flags,
        WinDivertAddress* address,
        uint* addressLength,
        nint overlapped);

    /// <summary>Réinjecte un lot de paquets.</summary>
    [LibraryImport(Library, EntryPoint = "WinDivertSendEx", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool SendEx(
        WinDivertHandle handle,
        byte* packet,
        uint packetLength,
        uint* sentLength,
        ulong flags,
        WinDivertAddress* address,
        uint addressLength,
        nint overlapped);

    /// <summary>
    /// Compile un filtre sans ouvrir de handle.
    /// </summary>
    /// <remarks>
    /// Purement en mode utilisateur : n'exige ni privilèges administrateur, ni pilote chargé.
    /// C'est ce qui permet de valider nos chaînes de filtre contre le <b>vrai</b> compilateur
    /// de WinDivert dans les tests, au lieu de se contenter d'inspecter du texte.
    /// </remarks>
    // Cette fonction rapporte ses erreurs par parametres de sortie, pas par GetLastError.
    // L'attribut est neanmoins pose : son cout est negligeable, et une regle sans exception
    // se verifie mecaniquement, alors qu'une exception « justifiee » doit etre retenue par
    // chaque relecteur — c'est ainsi qu'on finit par en oublier une vraie.
    [LibraryImport(Library, EntryPoint = "WinDivertHelperCompileFilter",
                   StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool HelperCompileFilter(
        string filter,
        WinDivertLayer layer,
        byte* @object,
        uint objectLength,
        byte** errorMessage,
        uint* errorPosition);

    /// <summary>
    /// Évalue un filtre contre un événement donné, sans ouvrir de handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Comme <see cref="HelperCompileFilter"/>, purement en mode utilisateur. La différence est
    /// décisive : compiler dit qu'un filtre est <b>syntaxiquement valide</b>, évaluer dit qu'il
    /// <b>correspond</b>. Un filtre parfaitement valide qui ne correspond jamais à rien est
    /// accepté sans un mot par WinDivert, et se traduit par une couche silencieuse.
    /// </para>
    /// <para>
    /// Pour un événement de flux, <paramref name="packet"/> vaut <c>null</c> et
    /// <paramref name="packetLength"/> zéro : il n'y a pas de paquet à cette couche — ce qui
    /// est précisément la raison pour laquelle les prédicats d'en-tête y sont toujours faux.
    /// </para>
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "WinDivertHelperEvalFilter",
                   StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool HelperEvalFilter(
        string filter,
        byte* packet,
        uint packetLength,
        WinDivertAddress* address);
}
