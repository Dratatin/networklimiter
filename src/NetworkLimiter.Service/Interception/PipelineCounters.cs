using System.Globalization;
using System.Text;

namespace NetworkLimiter.Service.Interception;

/// <summary>Étapes comptées de la boucle d'interception.</summary>
/// <remarks>
/// L'ordre des membres est celui du trajet d'un paquet. Il est significatif : lues dans
/// l'ordre, ces valeurs désignent l'étape exacte où la chaîne s'interrompt.
/// </remarks>
public enum PipelineCounter
{
    /// <summary>Événements reçus sur la couche <c>FLOW</c>.</summary>
    FlowEvents,

    /// <summary>Événements rejetés parce que la couche annoncée n'est pas <c>FLOW</c>.</summary>
    FlowWrongLayer,

    /// <summary>Flux ajoutés à la table.</summary>
    FlowsEstablished,

    /// <summary>Flux retirés de la table.</summary>
    FlowsDeleted,

    /// <summary>Flux dont le processus n'a pas pu être identifié.</summary>
    FlowIdentityUnknown,

    /// <summary>Paquets reçus sur la couche <c>NETWORK</c>.</summary>
    PacketsReceived,

    /// <summary>Paquets réinjectés sans examen : boucle locale ou déjà réinjectés par un tiers.</summary>
    PacketsBypassed,

    /// <summary>Paquets dont l'en-tête n'a pas pu être lu.</summary>
    PacketsUnreadable,

    /// <summary>Paquets rattachés à un flux connu.</summary>
    FlowMatched,

    /// <summary>Paquets dont le flux est absent de la table.</summary>
    FlowUnmatched,

    /// <summary>Paquets dont le correspondant n'est pas sur internet.</summary>
    ScopeExcluded,

    /// <summary>Paquets rattachés à une règle.</summary>
    RuleMatched,

    /// <summary>Paquets dont le processus ne correspond à aucune règle.</summary>
    RuleUnmatched,

    /// <summary>Paquets laissés passer faute de limite applicable.</summary>
    Passed,

    /// <summary>Paquets envoyés dans le budget du seau.</summary>
    Sent,

    /// <summary>Paquets temporisés.</summary>
    Delayed,

    /// <summary>Paquets rejetés.</summary>
    Dropped,
}

/// <summary>
/// Compte le passage des paquets à chaque étape de la boucle.
/// </summary>
/// <remarks>
/// <para>
/// Sans ces compteurs, une limitation qui ne s'applique pas est indiscernable d'une limitation
/// qui s'applique : le trafic passe dans les deux cas, et rien ne dit à quelle étape la chaîne
/// s'est interrompue. Deux causes très différentes — table de flux vide, ou paquets réinjectés
/// avant examen — produisent exactement la même observation de l'extérieur.
/// </para>
/// <para>
/// Le coût est un <c>Interlocked.Increment</c> par étape et par paquet, sans allocation ni
/// formatage tant que personne ne lit. C'est ce qui permet de les laisser <b>toujours</b>
/// actifs : un compteur qu'il faut activer est un compteur absent le jour où il servirait.
/// </para>
/// </remarks>
public sealed class PipelineCounters
{
    private static readonly PipelineCounter[] AllCounters = Enum.GetValues<PipelineCounter>();

    private readonly long[] _values = new long[AllCounters.Length];

    /// <summary>Incrémente une étape.</summary>
    public void Add(PipelineCounter counter, long delta = 1) =>
        Interlocked.Add(ref _values[(int)counter], delta);

    /// <summary>Lit une étape.</summary>
    public long this[PipelineCounter counter] => Interlocked.Read(ref _values[(int)counter]);

    /// <summary>Copie l'état courant.</summary>
    public long[] Snapshot()
    {
        long[] copy = new long[_values.Length];

        for (int index = 0; index < _values.Length; index++)
        {
            copy[index] = Interlocked.Read(ref _values[index]);
        }

        return copy;
    }

    /// <summary>
    /// Décrit ce qui a changé depuis un instantané, ou <c>null</c> si rien n'a bougé.
    /// </summary>
    /// <remarks>
    /// Rendre <c>null</c> quand rien ne change est délibéré : un service qui tourne en
    /// permanence ne doit pas écrire une ligne par seconde pour dire qu'il ne s'est rien passé.
    /// </remarks>
    public string? DescribeDelta(long[] previous)
    {
        ArgumentNullException.ThrowIfNull(previous);

        var text = new StringBuilder();

        for (int index = 0; index < _values.Length && index < previous.Length; index++)
        {
            long delta = Interlocked.Read(ref _values[index]) - previous[index];

            if (delta == 0)
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(CultureInfo.InvariantCulture, $"{Label(AllCounters[index])}={delta}");
        }

        return text.Length > 0 ? text.ToString() : null;
    }

    private static string Label(PipelineCounter counter) => counter switch
    {
        PipelineCounter.FlowEvents => "evt-flux",
        PipelineCounter.FlowWrongLayer => "evt-mauvaise-couche",
        PipelineCounter.FlowsEstablished => "flux-ouverts",
        PipelineCounter.FlowsDeleted => "flux-fermes",
        PipelineCounter.FlowIdentityUnknown => "flux-sans-identite",
        PipelineCounter.PacketsReceived => "paquets",
        PipelineCounter.PacketsBypassed => "contournes",
        PipelineCounter.PacketsUnreadable => "illisibles",
        PipelineCounter.FlowMatched => "flux-trouve",
        PipelineCounter.FlowUnmatched => "flux-inconnu",
        PipelineCounter.ScopeExcluded => "hors-internet",
        PipelineCounter.RuleMatched => "regle-trouvee",
        PipelineCounter.RuleUnmatched => "sans-regle",
        PipelineCounter.Passed => "passe",
        PipelineCounter.Sent => "envoye",
        PipelineCounter.Delayed => "retarde",
        PipelineCounter.Dropped => "rejete",
        _ => counter.ToString(),
    };
}
