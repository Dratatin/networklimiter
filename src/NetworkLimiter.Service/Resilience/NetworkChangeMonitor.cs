using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace NetworkLimiter.Service.Resilience;

/// <summary>
/// Écoute les changements d'environnement réseau du système.
/// </summary>
/// <remarks>
/// <para>
/// Implémente FR-036 et le principe II, qui élève les cas dégradés au rang d'exigences :
/// bascule Wi-Fi ↔ Ethernet, veille et reprise, partage de connexion mobile, adaptateurs
/// virtuels. Ce ne sont pas des situations exotiques mais l'ordinaire d'un portable.
/// </para>
/// <para>
/// Cette classe ne fait que <b>traduire</b> les événements système vers
/// <see cref="NetworkChangeCoalescer"/>, qui porte la logique de regroupement et ses tests.
/// Les abonnements système ne se simulent pas ; le regroupement, si.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class NetworkChangeMonitor : IDisposable
{
    private readonly NetworkChangeCoalescer _coalescer;
    private bool _started;
    private bool _disposed;

    /// <summary>Crée un moniteur autour d'un regroupeur.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="coalescer"/> est <c>null</c>.</exception>
    public NetworkChangeMonitor(NetworkChangeCoalescer coalescer)
    {
        ArgumentNullException.ThrowIfNull(coalescer);
        _coalescer = coalescer;
    }

    /// <summary>Commence à écouter.</summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _started = true;
    }

    /// <summary>Cesse d'écouter.</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        _started = false;
    }

    /// <summary>
    /// Rend la cause à traiter si l'environnement s'est stabilisé, sinon <c>null</c>.
    /// </summary>
    public NetworkChangeReason? TryConsumeChange() => _coalescer.TryConsume();

    private void OnAddressChanged(object? sender, EventArgs e) =>
        _coalescer.Record(NetworkChangeReason.AddressChanged);

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        _coalescer.Record(NetworkChangeReason.AvailabilityChanged);

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // Seule la reprise nous interesse : a la mise en veille, il n'y a plus de trafic a
        // mettre en forme, et les handles seront de toute facon invalides au reveil.
        if (e.Mode == PowerModes.Resume)
        {
            _coalescer.Record(NetworkChangeReason.ResumedFromSleep);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }
}
