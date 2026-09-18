using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NetworkLimiter.Service.Interception;

/// <summary>
/// Intercepteur réel, adossé au pilote WinDivert.
/// </summary>
/// <remarks>
/// <para>
/// Cette classe ne contient <b>presque aucune logique</b>, délibérément : elle traduit des
/// appels natifs et rien d'autre. Toute la logique qui mérite des tests — découpage des lots,
/// classification, table de flux, mise en forme — vit au-dessus, derrière
/// <see cref="IPacketInterceptor"/>, et se vérifie sans pilote ni privilèges.
/// </para>
/// <para>
/// Le code ci-dessous ne peut être validé que par les tests d'intégration sur VM (porte de
/// fusion n° 3) : il exige un pilote enregistré et des privilèges administrateur. C'est
/// précisément pour cela qu'il est réduit au minimum.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WinDivertInterceptor : IPacketInterceptor
{
    private readonly string _filter;
    private readonly WinDivertLayer _layer;
    private readonly short _priority;
    private readonly WinDivertOpenOptions _options;
    private readonly Lock _gate = new();

    private WinDivertHandle? _handle;

    /// <summary>Crée un intercepteur pour une couche donnée.</summary>
    public WinDivertInterceptor(
        string filter,
        WinDivertLayer layer,
        short priority,
        WinDivertOpenOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);

        _filter = filter;
        _layer = layer;
        _priority = priority;
        _options = options;
    }

    /// <summary>
    /// Crée l'intercepteur de la couche <c>FLOW</c>, qui alimente la table de flux.
    /// </summary>
    /// <remarks>
    /// <c>Sniff</c> et <c>RecvOnly</c> sont obligatoires sur cette couche : elle observe des
    /// événements, elle ne détourne rien. <c>NoInstall</c> impose que le pilote ait déjà été
    /// enregistré par l'installeur — jamais installé silencieusement au premier usage (R-011).
    /// </remarks>
    public static WinDivertInterceptor CreateFlowInterceptor() =>
        new(FilterBuilder.FlowFilter,
            WinDivertLayer.Flow,
            priority: 0,
            WinDivertOpenOptions.Sniff | WinDivertOpenOptions.RecvOnly | WinDivertOpenOptions.NoInstall);

    /// <summary>Crée l'intercepteur de la couche <c>NETWORK</c>, qui met en forme le trafic.</summary>
    public static WinDivertInterceptor CreateNetworkInterceptor() =>
        new(FilterBuilder.NetworkFilter,
            WinDivertLayer.Network,
            priority: 0,
            WinDivertOpenOptions.NoInstall);

    /// <inheritdoc />
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _handle is { IsInvalid: false, IsClosed: false };
            }
        }
    }

    /// <inheritdoc />
    public void Open()
    {
        // Valider le filtre avant d'ouvrir transforme une faute de syntaxe en message clair,
        // plutot qu'en echec d'ouverture sans cause lisible.
        FilterBuilder.Validate(_filter, _layer);

        lock (_gate)
        {
            if (_handle is { IsInvalid: false, IsClosed: false })
            {
                return;
            }

            WinDivertHandle handle = WinDivertNative.Open(_filter, _layer, _priority, _options);

            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();

                throw new InterceptionUnavailableException(DescribeOpenFailure(error), error);
            }

            ConfigureQueue(handle);
            _handle = handle;
        }
    }

    /// <inheritdoc />
    public unsafe int Receive(Span<byte> packetBuffer, Span<WinDivertAddress> addresses, out int bytesReceived)
    {
        bytesReceived = 0;

        WinDivertHandle handle = RequireHandle();

        uint received = 0;
        uint addressBytes = (uint)(addresses.Length * WinDivertAddress.SizeInBytes);

        fixed (byte* packetPtr = packetBuffer)
        fixed (WinDivertAddress* addressPtr = addresses)
        {
            bool ok = WinDivertNative.RecvEx(
                handle,
                packetBuffer.IsEmpty ? null : packetPtr,
                (uint)packetBuffer.Length,
                &received,
                flags: 0,
                addressPtr,
                &addressBytes,
                overlapped: 0);

            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();

                // ERROR_NO_DATA (232) apres un arret : le handle se ferme proprement, il n'y
                // a plus rien a lire. Ce n'est pas une defaillance.
                if (error is 232 or 995)
                {
                    return 0;
                }

                throw new InterceptionUnavailableException(
                    $"Réception WinDivert en échec (code {error}).", error);
            }
        }

        bytesReceived = (int)received;

        // addressBytes est en OCTETS, pas en nombre d'entrees.
        return (int)(addressBytes / WinDivertAddress.SizeInBytes);
    }

    /// <inheritdoc />
    public unsafe void Send(ReadOnlySpan<byte> packets, ReadOnlySpan<WinDivertAddress> addresses)
    {
        if (packets.IsEmpty || addresses.IsEmpty)
        {
            return;
        }

        WinDivertHandle handle = RequireHandle();
        uint sent = 0;

        fixed (byte* packetPtr = packets)
        fixed (WinDivertAddress* addressPtr = addresses)
        {
            bool ok = WinDivertNative.SendEx(
                handle,
                packetPtr,
                (uint)packets.Length,
                &sent,
                flags: 0,
                addressPtr,
                (uint)(addresses.Length * WinDivertAddress.SizeInBytes),
                overlapped: 0);

            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();

                throw new InterceptionUnavailableException(
                    $"Réinjection WinDivert en échec (code {error}).", error);
            }
        }
    }

    /// <inheritdoc />
    public void Close()
    {
        lock (_gate)
        {
            if (_handle is null)
            {
                return;
            }

            try
            {
                // Shutdown debloque une reception en cours ; sans lui, la boucle de drainage
                // resterait bloquee dans l'appel natif et le processus ne s'arreterait pas.
                if (!_handle.IsInvalid && !_handle.IsClosed)
                {
                    WinDivertNative.Shutdown(_handle, WinDivertShutdown.Both);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Close ne doit JAMAIS lever : c'est le geste qui rend le reseau a
                // l'utilisateur, appele sur tous les chemins de sortie, y compris depuis un
                // gestionnaire d'exception. Propager ici pourrait interrompre la sequence de
                // liberation et laisser du trafic etrangle (principe IV).
            }

            _handle.Dispose();
            _handle = null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Close();

    private WinDivertHandle RequireHandle()
    {
        lock (_gate)
        {
            if (_handle is null || _handle.IsInvalid || _handle.IsClosed)
            {
                throw new InterceptionUnavailableException("Le handle d'interception est fermé.");
            }

            return _handle;
        }
    }

    private static void ConfigureQueue(WinDivertHandle handle)
    {
        // Valeurs maximales de windivert.h. La file du noyau est un tampon de TRANSIT a vider
        // au plus vite, jamais le tampon de mise en forme : au-dela de ces bornes, WinDivert
        // rejette les paquets, ce qui convertirait la limitation en perte non maitrisee
        // (research.md R-004).
        WinDivertNative.SetParam(handle, WinDivertParam.QueueLength, 16_384);
        WinDivertNative.SetParam(handle, WinDivertParam.QueueSize, 33_554_432);
    }

    private static string DescribeOpenFailure(int win32Error) => win32Error switch
    {
        2 => "Le pilote WinDivert n'est pas installé. Réinstallez NetworkLimiter.",
        5 => "Accès refusé : le service doit s'exécuter avec des privilèges administrateur.",
        87 => "Filtre ou paramètres d'ouverture invalides.",
        577 => "Le pilote WinDivert a été refusé par Windows : signature invalide ou bloquée.",
        1058 => "Le service du pilote WinDivert est désactivé.",
        _ => $"Ouverture de WinDivert impossible (code {win32Error}).",
    };
}
