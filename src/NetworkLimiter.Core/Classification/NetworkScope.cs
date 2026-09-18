namespace NetworkLimiter.Core.Classification;

/// <summary>
/// Portée d'un flux réseau, qui détermine s'il est soumis aux plafonds.
/// </summary>
public enum NetworkScope
{
    /// <summary>
    /// Trafic à destination ou en provenance d'internet. Seule portée soumise aux plafonds
    /// (FR-040).
    /// </summary>
    Internet = 0,

    /// <summary>
    /// Trafic du réseau local : plages privées, lien-local, multicast, diffusion.
    /// Jamais soumis aux plafonds — une sauvegarde vers un NAS ne doit pas être bridée par
    /// un plafond destiné à protéger la connexion internet (FR-040a).
    /// </summary>
    Local = 1,

    /// <summary>
    /// Trafic de boucle locale, interne à la machine. Jamais soumis aux plafonds.
    /// </summary>
    Loopback = 2,
}
