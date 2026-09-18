using System.Runtime.Versioning;

namespace NetworkLimiter.Service.Interception;

/// <summary>Un filtre WinDivert refusé par le compilateur de filtres.</summary>
public sealed class InvalidFilterException : Exception
{
    /// <summary>Crée l'exception.</summary>
    public InvalidFilterException(string filter, string reason, uint position)
        : base($"Filtre WinDivert invalide en position {position} : {reason}. Filtre : « {filter} »")
    {
        Filter = filter;
        Position = position;
    }

    /// <inheritdoc cref="Exception(string)" />
    public InvalidFilterException(string message) : base(message) => Filter = string.Empty;

    /// <inheritdoc cref="Exception(string, Exception)" />
    public InvalidFilterException(string message, Exception innerException)
        : base(message, innerException) => Filter = string.Empty;

    /// <summary>Crée l'exception sans détail.</summary>
    public InvalidFilterException() => Filter = string.Empty;

    /// <summary>Filtre fautif.</summary>
    public string Filter { get; }

    /// <summary>Position de l'erreur dans le filtre.</summary>
    public uint Position { get; }
}

/// <summary>
/// Construit les chaînes de filtre passées à WinDivert.
/// </summary>
/// <remarks>
/// <para>
/// Deux filtres, un par handle. Celui de la couche <see cref="WinDivertLayer.Flow"/> alimente
/// la table qui associe chaque flux à un processus ; celui de la couche
/// <see cref="WinDivertLayer.Network"/> sélectionne les paquets à mettre en forme.
/// </para>
/// <para>
/// Les deux couvrent <b>IPv4 et IPv6</b> (FR-008). C'est un point que rien ne signale si on
/// l'oublie : la limitation semblerait fonctionner, mais le trafic IPv6 échapperait
/// silencieusement au plafond — et Windows préfère IPv6 dès qu'il est disponible.
/// </para>
/// <para>
/// La distinction internet / local n'est délibérément <b>pas</b> faite ici mais en mode
/// utilisateur, par <c>NetworkScopeClassifier</c>, déjà testé aux bornes exactes de chaque
/// plage. Dupliquer cette logique dans le langage de filtre créerait deux définitions du
/// « réseau local » qui divergeraient tôt ou tard. Le surcoût est nul quand aucune limite
/// n'est active, puisque le handle n'est alors pas ouvert.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class FilterBuilder
{
    /// <summary>
    /// Filtre de la couche réseau : trafic IP des deux familles, TCP et UDP, hors boucle locale.
    /// </summary>
    public const string NetworkFilter = "(ip or ipv6) and (tcp or udp) and not loopback";

    /// <summary>
    /// Filtre de la couche flux : tout flux TCP ou UDP des deux familles.
    /// </summary>
    /// <remarks>
    /// La boucle locale n'est pas exclue ici : connaître ces flux ne coûte rien et évite un
    /// trou dans la table si un flux change de nature. C'est le filtre réseau qui décide de
    /// ce qui est mis en forme.
    /// </remarks>
    public const string FlowFilter = "(ip or ipv6) and (tcp or udp)";

    /// <summary>
    /// Valide un filtre auprès du compilateur de WinDivert.
    /// </summary>
    /// <remarks>
    /// Purement en mode utilisateur : n'exige ni privilèges, ni pilote chargé. Valider avant
    /// d'ouvrir un handle transforme une erreur de syntaxe en message clair au démarrage,
    /// plutôt qu'en échec d'ouverture opaque.
    /// </remarks>
    /// <exception cref="InvalidFilterException">Le filtre est refusé par WinDivert.</exception>
    public static unsafe void Validate(string filter, WinDivertLayer layer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);

        byte* errorMessage = null;
        uint errorPosition = 0;

        bool compiled = WinDivertNative.HelperCompileFilter(
            filter, layer, @object: null, objectLength: 0, &errorMessage, &errorPosition);

        if (compiled)
        {
            return;
        }

        string reason = errorMessage is null
            ? "raison non fournie par WinDivert"
            : System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)errorMessage) ?? "raison illisible";

        throw new InvalidFilterException(filter, reason, errorPosition);
    }
}
