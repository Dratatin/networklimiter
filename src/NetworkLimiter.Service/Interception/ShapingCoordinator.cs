using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Core.Rules;
using NetworkLimiter.Core.Shaping;
using NetworkLimiter.Core.Units;
using NetworkLimiter.Service.Health;

namespace NetworkLimiter.Service.Interception;

/// <summary>Constate l'existence d'un chemin sur le disque.</summary>
/// <remarks>
/// Abstraction nécessaire pour que la traduction règles → mise en forme reste testable sans
/// créer de fichiers. L'existence du chemin décide de l'activation du repli par nom
/// d'exécutable (FR-039a) : la tester exigerait sinon de fabriquer une arborescence réelle à
/// chaque cas.
/// </remarks>
public interface IPathExistenceProbe
{
    /// <summary>Indique si un chemin d'exécutable existe encore.</summary>
    bool Exists(string executablePath);
}

/// <summary>Sonde réelle, adossée au système de fichiers.</summary>
public sealed class FileSystemPathProbe : IPathExistenceProbe
{
    /// <inheritdoc />
    public bool Exists(string executablePath)
    {
        try
        {
            return File.Exists(executablePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Chemin inaccessible — lecteur reseau deconnecte, permissions. On le considere
            // comme existant : activer le repli par nom sur un simple incident d'acces
            // ferait soudain viser tous les homonymes de la machine.
            return true;
        }
    }
}

/// <summary>
/// Traduit les règles persistées en état de mise en forme vivant.
/// </summary>
/// <remarks>
/// <para>
/// Pivot entre la configuration — ce que l'utilisateur a demandé — et le pipeline — ce qui
/// s'applique réellement au trafic. C'est le composant qui rend FR-002 vrai : une règle
/// modifiée prend effet sans redémarrer quoi que ce soit.
/// </para>
/// <para>
/// Il calcule aussi les raisons d'inactivité de chaque règle (FR-026). Une règle définie mais
/// non appliquée doit toujours pouvoir dire pourquoi, sans quoi l'utilisateur n'a aucun moyen
/// de répondre à « pourquoi cette application n'est pas limitée ? ».
/// </para>
/// </remarks>
public sealed class ShapingCoordinator
{
    private readonly PacketShaper _shaper;
    private readonly RuleResolver _resolver;
    private readonly IPathExistenceProbe _pathProbe;

    private IReadOnlyList<RuleDto> _rules = [];
    private bool _suspended;
    private bool _interceptionAvailable = true;

    /// <summary>Crée le coordinateur.</summary>
    /// <exception cref="ArgumentNullException">Un argument est <c>null</c>.</exception>
    public ShapingCoordinator(PacketShaper shaper, RuleResolver resolver, IPathExistenceProbe pathProbe)
    {
        ArgumentNullException.ThrowIfNull(shaper);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(pathProbe);

        _shaper = shaper;
        _resolver = resolver;
        _pathProbe = pathProbe;
    }

    /// <summary>
    /// Suspension globale : les règles restent définies mais rien n'est limité (FR-024).
    /// </summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value)
            {
                return;
            }

            _suspended = value;
            Reapply();
        }
    }

    /// <summary>L'interception est opérationnelle.</summary>
    /// <remarks>
    /// Passe à <c>false</c> quand les handles sont fermés. Les règles cessent alors de
    /// s'appliquer, et l'état de santé en porte la raison plutôt que d'afficher des règles
    /// « actives » qui ne le sont pas.
    /// </remarks>
    public bool InterceptionAvailable
    {
        get => _interceptionAvailable;
        set
        {
            if (_interceptionAvailable == value)
            {
                return;
            }

            _interceptionAvailable = value;
            Reapply();
        }
    }

    /// <summary>Paquets libérés par un changement de règles, à réinjecter sans limitation.</summary>
    public List<PendingPacket> ReleasedPackets { get; } = [];

    /// <summary>Applique un nouveau jeu de règles.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="rules"/> est <c>null</c>.</exception>
    public void Apply(IReadOnlyList<RuleDto> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        _rules = rules;
        Reapply();
    }

    /// <summary>
    /// Rend, pour chaque règle non appliquée, la raison de son inactivité (FR-026).
    /// </summary>
    /// <param name="runningPaths">Chemins normalisés des processus actuellement en cours.</param>
    public IReadOnlyList<InactiveRule> GetInactiveRules(IReadOnlySet<string> runningPaths)
    {
        ArgumentNullException.ThrowIfNull(runningPaths);

        var inactive = new List<InactiveRule>();

        foreach (RuleDto rule in _rules)
        {
            RuleInactiveReason? reason = GetInactiveReason(rule, runningPaths);

            if (reason is { } value)
            {
                inactive.Add(new InactiveRule(rule.Id, value));
            }
        }

        return inactive;
    }

    /// <summary>
    /// Rend la raison pour laquelle une règle ne s'applique pas, ou <c>null</c> si elle s'applique.
    /// </summary>
    /// <remarks>
    /// Publique, et appelée <b>règle par règle</b> par ce qui compose l'état affiché. La version
    /// précédente ne répondait que pour son propre jeu de règles : une règle qu'elle n'avait pas
    /// encore reçue passait pour « active » faute de raison d'inactivité trouvée. C'était le
    /// défaut le plus trompeur possible — afficher comme appliqué un plafond dont rien ne
    /// garantissait qu'il l'était.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Un argument est <c>null</c>.</exception>
    public RuleInactiveReason? GetInactiveReason(RuleDto rule, IReadOnlySet<string> runningPaths)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(runningPaths);

        // L'ordre compte : on nomme la cause la plus englobante d'abord. Dire « application
        // non lancee » alors que toute la limitation est suspendue enverrait l'utilisateur
        // chercher au mauvais endroit.
        if (!_interceptionAvailable)
        {
            return RuleInactiveReason.InterceptionUnavailable;
        }

        if (_suspended)
        {
            return RuleInactiveReason.GloballySuspended;
        }

        if (!rule.Enabled)
        {
            return RuleInactiveReason.RuleDisabled;
        }

        bool pathExists = _pathProbe.Exists(rule.Target.ExecutablePath);

        if (runningPaths.Contains(rule.Target.ExecutablePath))
        {
            return null;
        }

        if (!pathExists)
        {
            // Le chemin a disparu : la regle s'appliquera par repli sur le nom, ce que
            // l'interface doit signaler (FR-039a).
            return runningPaths.Any(path => path.EndsWith('\\' + rule.Target.ExecutableName, StringComparison.Ordinal))
                ? RuleInactiveReason.MatchedByFallbackName
                : RuleInactiveReason.ExecutablePathNotFound;
        }

        return RuleInactiveReason.ApplicationNotRunning;
    }

    private void Reapply()
    {
        // Suspension ou interception indisponible : aucune limite ne s'applique, mais les
        // regles restent definies. Liberer les paquets en attente est obligatoire — les
        // garder dans une file dont plus personne ne s'occupe les perdrait (principe IV).
        if (_suspended || !_interceptionAvailable)
        {
            _shaper.ReleaseAll(ReleasedPackets);
            _resolver.Update([]);
            return;
        }

        var shaperRules = new List<ShaperRule>(_rules.Count);
        var targets = new List<RuleTarget>(_rules.Count);

        foreach (RuleDto rule in _rules)
        {
            bool pathExists = _pathProbe.Exists(rule.Target.ExecutablePath);

            targets.Add(new RuleTarget(rule.Id, rule.Target.ToDomain(), rule.Enabled, pathExists));

            if (!rule.Enabled)
            {
                continue;
            }

            shaperRules.Add(new ShaperRule(
                rule.Id,
                rule.DownloadBytesPerSecond is { } download ? ByteRate.FromBytesPerSecond(download) : null,
                rule.UploadBytesPerSecond is { } upload ? ByteRate.FromBytesPerSecond(upload) : null));
        }

        _resolver.Update(targets);

        // ApplyRules conserve les seaux existants et libere ceux des regles retirees : les
        // paquets deja acceptes ne sont jamais abandonnes, et les plafonds inchanges ne
        // repartent pas d'un seau plein.
        _shaper.ApplyRules(shaperRules);
        _shaper.DrainReleased(ReleasedPackets);
    }
}
