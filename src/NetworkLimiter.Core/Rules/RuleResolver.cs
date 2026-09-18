namespace NetworkLimiter.Core.Rules;

/// <summary>
/// Identité d'une application, telle qu'une règle la vise.
/// </summary>
/// <param name="ExecutablePath">Chemin complet normalisé. Critère <b>primaire</b>.</param>
/// <param name="ExecutableName">Nom de fichier de l'exécutable. Critère de <b>repli</b>.</param>
/// <param name="DisplayName">Nom lisible. Affichage seulement, jamais utilisé pour apparier.</param>
public sealed record AppIdentity(string ExecutablePath, string ExecutableName, string DisplayName);

/// <summary>Façon dont une règle a été appariée à un processus.</summary>
public enum RuleMatchMode
{
    /// <summary>Aucune règle ne vise ce processus.</summary>
    None,

    /// <summary>Le chemin complet correspond exactement.</summary>
    ExactPath,

    /// <summary>
    /// Le chemin enregistré n'existe plus ; l'appariement s'est fait sur le nom d'exécutable.
    /// </summary>
    /// <remarks>
    /// Doit <b>toujours</b> être remonté à l'interface (FR-039a, FR-039c). Un appariement par
    /// repli silencieux ferait croire à l'utilisateur que sa règle vise l'exécutable qu'il a
    /// désigné, alors qu'elle vise tout programme portant le même nom de fichier.
    /// </remarks>
    FallbackName,
}

/// <summary>Une règle, réduite à ce qui sert à l'appariement.</summary>
/// <param name="RuleId">Identifiant de la règle.</param>
/// <param name="Target">Application visée.</param>
/// <param name="Enabled">La règle est active.</param>
/// <param name="TargetPathExists">
/// Le chemin enregistré existe encore sur le disque. Constaté par le service et fourni ici,
/// pour que la résolution reste une fonction pure, sans accès disque.
/// </param>
public sealed record RuleTarget(Guid RuleId, AppIdentity Target, bool Enabled, bool TargetPathExists);

/// <summary>Résultat d'un appariement.</summary>
/// <param name="RuleId">Règle appariée.</param>
/// <param name="Mode">Façon dont l'appariement s'est fait.</param>
public sealed record RuleMatch(Guid RuleId, RuleMatchMode Mode);

/// <summary>
/// Apparie un processus observé à la règle qui le vise.
/// </summary>
/// <remarks>
/// <para>
/// Implémente FR-039 et FR-039a. Le chemin complet prime ; le nom d'exécutable ne sert de
/// repli que lorsque le chemin enregistré n'existe plus — cas typique d'une application mise à
/// jour vers un dossier versionné, comme <c>app-1.2.3</c> devenant <c>app-1.2.4</c>.
/// </para>
/// <para>
/// Le repli n'est jamais silencieux. Sans cette distinction, une règle visant
/// <c>C:\Outils\updater.exe</c> s'appliquerait aussi à un <c>updater.exe</c> sans rapport situé
/// ailleurs, et l'utilisateur ne comprendrait ni pourquoi une application qu'il n'a pas limitée
/// rame, ni pourquoi celle qu'il a limitée échappe parfois à sa règle.
/// </para>
/// </remarks>
public sealed class RuleResolver
{
    /// <summary>Jeu de règles indexé, remplacé d'un bloc et jamais modifié en place.</summary>
    private sealed record Snapshot(
        Dictionary<string, RuleTarget> ByPath,
        Dictionary<string, List<RuleTarget>> FallbackByName)
    {
        public static Snapshot Empty => new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
    }

    // Publication par echange de reference plutot que par verrou. La resolution est sur le
    // chemin par paquet et n'ecrit rien ; les mises a jour sont rares. Surtout, cela donne une
    // garantie qu'un verrou autour de dictionnaires modifiables ne donnerait pas aussi
    // simplement : un lecteur voit soit l'ancien jeu complet, soit le nouveau complet, jamais
    // un jeu a demi construit — ce qui reviendrait a appliquer un plafond jamais configure.
    private volatile Snapshot _snapshot = Snapshot.Empty;

    /// <summary>Remplace le jeu de règles courant.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="rules"/> est <c>null</c>.</exception>
    public void Update(IReadOnlyList<RuleTarget> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        Dictionary<string, RuleTarget> byPath = new(StringComparer.Ordinal);
        Dictionary<string, List<RuleTarget>> fallbackByName = new(StringComparer.Ordinal);

        foreach (RuleTarget rule in rules)
        {
            // Une regle desactivee n'apparie rien : elle reste definie, mais inactive, et
            // l'etat de sante en portera la raison (FR-006).
            if (!rule.Enabled)
            {
                continue;
            }

            byPath[rule.Target.ExecutablePath] = rule;

            // Seules les regles dont le chemin a disparu alimentent le repli. Une regle dont
            // le chemin existe encore ne doit surtout pas capturer les homonymes d'ailleurs.
            if (!rule.TargetPathExists)
            {
                if (!fallbackByName.TryGetValue(rule.Target.ExecutableName, out List<RuleTarget>? candidates))
                {
                    candidates = [];
                    fallbackByName[rule.Target.ExecutableName] = candidates;
                }

                candidates.Add(rule);
            }
        }

        // Ordre deterministe pour le repli : a candidats multiples, le meme processus doit
        // toujours tomber sur la meme regle, d'une execution a l'autre.
        foreach (List<RuleTarget> candidates in fallbackByName.Values)
        {
            candidates.Sort((left, right) => left.RuleId.CompareTo(right.RuleId));
        }

        // Publication : a partir d'ici, et pas avant, les lecteurs voient le nouveau jeu.
        _snapshot = new Snapshot(byPath, fallbackByName);
    }

    /// <summary>Nombre de règles actives.</summary>
    public int ActiveRuleCount => _snapshot.ByPath.Count;

    /// <summary>
    /// Cherche la règle qui vise un processus observé.
    /// </summary>
    /// <returns><c>null</c> si aucune règle ne le vise ; son trafic n'est alors pas limité.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="observed"/> est <c>null</c>.</exception>
    public RuleMatch? Resolve(AppIdentity observed)
    {
        ArgumentNullException.ThrowIfNull(observed);

        // Lu une seule fois : les deux recherches doivent porter sur le MEME jeu de regles.
        // Relire le champ entre les deux pourrait apparier un repli d'un jeu contre un chemin
        // exact d'un autre.
        Snapshot snapshot = _snapshot;

        if (snapshot.ByPath.TryGetValue(observed.ExecutablePath, out RuleTarget? exact))
        {
            return new RuleMatch(exact.RuleId, RuleMatchMode.ExactPath);
        }

        if (snapshot.FallbackByName.TryGetValue(observed.ExecutableName, out List<RuleTarget>? candidates) &&
            candidates.Count > 0)
        {
            return new RuleMatch(candidates[0].RuleId, RuleMatchMode.FallbackName);
        }

        return null;
    }
}
