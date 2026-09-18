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
/// Les deux couvrent <b>IPv4 et IPv6</b> (FR-008), mais pas de la même façon : la couche
/// réseau les nomme explicitement, la couche flux les obtient en ne les mentionnant pas. C'est
/// un point que rien ne signale si on l'oublie : la limitation semblerait fonctionner, mais le
/// trafic IPv6 échapperait silencieusement au plafond — et Windows préfère IPv6 dès qu'il est
/// disponible.
/// </para>
/// <para>
/// Un filtre accepté par WinDivert n'est pas un filtre qui correspond. Les deux constantes
/// ci-dessous sont donc évaluées contre des événements synthétiques par
/// <c>FilterMatchingTests</c>, et pas seulement compilées.
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
    /// <para>
    /// <b>Aucune clause de famille ici, et c'est délibéré.</b> <c>ip</c> et <c>ipv6</c> sont des
    /// prédicats d'en-tête — « ce paquet porte un en-tête IPv4 / IPv6 » — et à la couche flux il
    /// n'y a pas de paquet : ils y sont <b>toujours faux</b>. Ce filtre a d'abord été écrit
    /// <c>(ip or ipv6) and (tcp or udp)</c> ; WinDivert l'a compilé sans broncher, le handle
    /// s'est ouvert, et la couche est restée muette. Aucun flux n'était attribué à un processus,
    /// donc aucune règle ne s'appliquait, et rien ne le signalait.
    /// </para>
    /// <para>
    /// <c>tcp or udp</c> couvre bien les deux familles — vérifié sur machine réelle, des flux
    /// QUIC en IPv6 sont délivrés. La famille se lit ensuite dans le drapeau <c>IPv6</c> de
    /// l'adresse, ce que fait <c>BuildFlowKey</c>.
    /// </para>
    /// <para>
    /// La boucle locale n'est pas exclue ici : connaître ces flux ne coûte rien et évite un
    /// trou dans la table si un flux change de nature. C'est le filtre réseau qui décide de
    /// ce qui est mis en forme.
    /// </para>
    /// </remarks>
    public const string FlowFilter = "tcp or udp";

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

    /// <summary>
    /// Indique si un filtre correspond à un événement donné.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Complète <see cref="Validate"/>, qui ne répond qu'à « ce filtre est-il bien écrit ? ».
    /// Celle-ci répond à « ce filtre laisse-t-il passer quelque chose ? », la seule question
    /// dont dépend le fonctionnement : un filtre valide qui ne correspond jamais à rien rend
    /// une couche entière silencieuse, sans erreur ni avertissement.
    /// </para>
    /// <para>
    /// N'exige ni privilèges ni pilote : c'est ce qui permet d'en faire un test ordinaire.
    /// </para>
    /// </remarks>
    /// <param name="filter">Filtre à évaluer.</param>
    /// <param name="address">Événement soumis au filtre.</param>
    /// <param name="packet">Paquet associé, vide pour un événement sans paquet.</param>
    public static unsafe bool Matches(
        string filter,
        WinDivertAddress address,
        ReadOnlySpan<byte> packet = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);

        fixed (byte* packetPtr = packet)
        {
            return WinDivertNative.HelperEvalFilter(
                filter,
                packet.IsEmpty ? null : packetPtr,
                (uint)packet.Length,
                &address);
        }
    }
}
