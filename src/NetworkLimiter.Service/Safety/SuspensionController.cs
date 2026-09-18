using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Service.Interception;

namespace NetworkLimiter.Service.Safety;

/// <summary>Fournit les règles du profil actif.</summary>
/// <remarks>
/// Abstraction minuscule mais utile : elle permet d'éprouver la suspension sans fichier de
/// configuration ni écriture disque, alors que c'est justement le geste qu'on veut pouvoir
/// vérifier sous tous ses angles.
/// </remarks>
public interface IRuleSource
{
    /// <summary>Règles du profil actif.</summary>
    IReadOnlyList<RuleDto> ActiveRules { get; }
}

/// <summary>
/// Suspension globale de la limitation (FR-024).
/// </summary>
/// <remarks>
/// <para>
/// C'est le bouton d'arrêt d'urgence du produit. L'utilisateur constate que quelque chose ne
/// marche plus et veut rendre son réseau <b>maintenant</b>, sans avoir à identifier laquelle de
/// ses règles est en cause.
/// </para>
/// <para>
/// Deux propriétés doivent tenir <b>ensemble</b> : toutes les limites tombent, et aucune règle
/// n'est perdue. Lever les limites en supprimant les règles serait plus court à écrire et ferait
/// de la suspension un piège — l'utilisateur perdrait sa configuration en cherchant seulement à
/// dépanner sa connexion.
/// </para>
/// <para>
/// La suspension n'est <b>pas persistée</b>, et c'est délibéré. Un service qui redémarrerait
/// encore suspendu laisserait l'utilisateur sans limites sans qu'il se souvienne pourquoi, des
/// jours plus tard. Un état d'urgence doit s'effacer de lui-même ; c'est la désactivation d'une
/// règle qui sert à ne plus limiter durablement.
/// </para>
/// </remarks>
public sealed class SuspensionController
{
    private readonly ShapingCoordinator _coordinator;
    private readonly Lock _gate = new();

    /// <summary>Crée le contrôleur.</summary>
    /// <exception cref="ArgumentNullException">Un argument est <c>null</c>.</exception>
    public SuspensionController(ShapingCoordinator coordinator, IRuleSource rules)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(rules);

        _coordinator = coordinator;
        Rules = rules;
    }

    /// <summary>Source des règles, conservées pendant la suspension.</summary>
    public IRuleSource Rules { get; }

    /// <summary>Coordinateur piloté par la suspension.</summary>
    public ShapingCoordinator Coordinator => _coordinator;

    /// <summary>La limitation est suspendue.</summary>
    public bool IsSuspended => _coordinator.Suspended;

    /// <summary>Émis à chaque changement effectif d'état.</summary>
    /// <remarks>
    /// À chaque <b>changement</b>, pas à chaque appel : rediffuser un état identique ferait
    /// clignoter les interfaces sans qu'aucune valeur n'ait bougé.
    /// </remarks>
    public event EventHandler<bool>? Changed;

    /// <summary>Lève toutes les limites en conservant les règles.</summary>
    public void Suspend() => Set(suspended: true);

    /// <summary>Réapplique les règles conservées.</summary>
    public void Resume() => Set(suspended: false);

    /// <summary>Applique un état de suspension.</summary>
    public void Set(bool suspended)
    {
        lock (_gate)
        {
            if (_coordinator.Suspended == suspended)
            {
                return;
            }

            // Le coordinateur porte deja tout le travail : il libere les paquets en attente,
            // vide le resolveur et cesse d'appliquer les plafonds. Le dupliquer ici creerait
            // deux chemins pour lever les limites, dont un seul serait maintenu.
            _coordinator.Suspended = suspended;
        }

        Changed?.Invoke(this, suspended);
    }
}
