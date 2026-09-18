using System.Runtime.Versioning;

namespace NetworkLimiter.Service.Interception;

/// <summary>
/// Accès à l'interception réseau.
/// </summary>
/// <remarks>
/// <para>
/// Cette abstraction existe pour une raison précise : tout le code au-dessus — pipeline de mise
/// en forme, table de flux, résolution de règles — doit être testable <b>sans pilote noyau
/// chargé et sans privilèges administrateur</b>. Sans elle, la majeure partie du service ne
/// serait vérifiable que sur une machine spécialement préparée, ce qui reviendrait en pratique
/// à ne pas la vérifier.
/// </para>
/// <para>
/// L'implémentation réelle <see cref="WinDivertInterceptor"/> ne contient donc presque aucune
/// logique : elle traduit des appels natifs, et rien d'autre. La logique qui mérite des tests
/// vit au-dessus.
/// </para>
/// </remarks>
public interface IPacketInterceptor : IDisposable
{
    /// <summary>Indique si un handle est actuellement ouvert.</summary>
    bool IsOpen { get; }

    /// <summary>Ouvre le handle d'interception.</summary>
    /// <exception cref="InterceptionUnavailableException">Le pilote est absent ou l'ouverture échoue.</exception>
    void Open();

    /// <summary>
    /// Reçoit un lot d'événements ou de paquets.
    /// </summary>
    /// <param name="packetBuffer">Tampon recevant les paquets concaténés. Vide pour la couche Flow.</param>
    /// <param name="addresses">Tampon recevant les métadonnées, une par paquet ou événement.</param>
    /// <param name="bytesReceived">Nombre d'octets de paquet effectivement reçus.</param>
    /// <returns>Nombre d'entrées remplies dans <paramref name="addresses"/>.</returns>
    int Receive(Span<byte> packetBuffer, Span<WinDivertAddress> addresses, out int bytesReceived);

    /// <summary>Réinjecte un lot de paquets.</summary>
    void Send(ReadOnlySpan<byte> packets, ReadOnlySpan<WinDivertAddress> addresses);

    /// <summary>
    /// Ferme le handle et rend le trafic à l'utilisateur.
    /// </summary>
    /// <remarks>
    /// Doit être idempotent et ne jamais lever : c'est le geste qui libère le réseau, et il
    /// est appelé sur tous les chemins de sortie, y compris depuis un gestionnaire
    /// d'exception (principe IV).
    /// </remarks>
    void Close();
}

/// <summary>L'interception ne peut pas démarrer.</summary>
[SupportedOSPlatform("windows")]
public sealed class InterceptionUnavailableException : Exception
{
    /// <summary>Crée l'exception avec un code d'erreur Windows.</summary>
    public InterceptionUnavailableException(string message, int win32ErrorCode)
        : base(message) => Win32ErrorCode = win32ErrorCode;

    /// <inheritdoc cref="Exception(string)" />
    public InterceptionUnavailableException(string message) : base(message)
    {
    }

    /// <inheritdoc cref="Exception(string, Exception)" />
    public InterceptionUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Crée l'exception sans détail.</summary>
    public InterceptionUnavailableException()
    {
    }

    /// <summary>Code d'erreur Windows, ou zéro.</summary>
    public int Win32ErrorCode { get; }
}
