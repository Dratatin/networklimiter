using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Service.Health;
using NetworkLimiter.Service.Interception;
using NetworkLimiter.Service.Persistence;

namespace NetworkLimiter.Service.Ipc;

/// <summary>Rend l'état complet du service, tel que l'interface doit l'afficher.</summary>
public interface IStateSource
{
    /// <summary>Compose l'état courant.</summary>
    GetStateResultPayload GetState();
}

/// <summary>
/// Compose l'état à partir des règles persistées et de ce qui s'applique réellement.
/// </summary>
/// <remarks>
/// <para>
/// La distinction porte tout ce composant : <see cref="RuleStore"/> sait ce que l'utilisateur a
/// <b>demandé</b>, <see cref="ShapingCoordinator"/> sait ce qui <b>s'applique</b>. Les deux
/// diffèrent souvent — application non lancée, chemin disparu, limitation suspendue — et
/// n'afficher que la première ferait croire à des plafonds qui n'agissent pas.
/// </para>
/// </remarks>
public sealed class ServiceStateProvider : IStateSource
{
    private readonly RuleStore _rules;
    private readonly ShapingCoordinator _coordinator;
    private readonly Func<IReadOnlySet<string>> _runningPaths;

    /// <summary>Crée le fournisseur.</summary>
    /// <param name="rules">Dépôt des règles persistées.</param>
    /// <param name="coordinator">Coordinateur, qui sait ce qui s'applique réellement.</param>
    /// <param name="runningPaths">Chemins normalisés des processus ayant une activité réseau.</param>
    /// <exception cref="ArgumentNullException">Un argument est <c>null</c>.</exception>
    public ServiceStateProvider(
        RuleStore rules,
        ShapingCoordinator coordinator,
        Func<IReadOnlySet<string>> runningPaths)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(runningPaths);

        _rules = rules;
        _coordinator = coordinator;
        _runningPaths = runningPaths;
    }

    /// <inheritdoc />
    public GetStateResultPayload GetState()
    {
        IReadOnlySet<string> running = _runningPaths();
        PersistedConfig config = _rules.Config;

        HashSet<string> ruled = [.. config.ActiveProfile.Rules.Select(rule => rule.Target.ExecutablePath)];

        return new GetStateResultPayload
        {
            ActiveProfileId = config.ActiveProfileId,
            Suspended = _coordinator.Suspended,
            InterceptionAvailable = _coordinator.InterceptionAvailable,
            ObservedApplications =
            [
                .. running
                    .Select(path => new ObservedAppDto
                    {
                        ExecutablePath = path,
                        ExecutableName = Path.GetFileName(path),
                        AlreadyRuled = ruled.Contains(path),
                    })
                    // Ordre stable : une liste qui se reordonne a chaque rafraichissement
                    // rendrait impossible de cliquer sur ce qu'on vise.
                    .OrderBy(app => app.ExecutableName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(app => app.ExecutablePath, StringComparer.OrdinalIgnoreCase),
            ],
            Profiles =
            [
                .. config.Profiles.Select(profile => new ProfileStateDto
                {
                    Id = profile.Id,
                    Name = profile.Name,
                    GlobalLimit = profile.GlobalLimit,
                    Rules = [.. profile.Rules.Select(rule => Describe(rule, running))],
                }),
            ],
        };
    }

    private RuleStateDto Describe(RuleDto rule, IReadOnlySet<string> running)
    {
        // Interrogé pour CETTE règle, et non consulté dans une liste que le coordinateur aurait
        // constituée de son côté : les deux peuvent diverger le temps qu'une modification se
        // propage, et une règle absente de cette liste passerait alors pour appliquée.
        RuleInactiveReason? reason = _coordinator.GetInactiveReason(rule, running);

        return new RuleStateDto
        {
            Rule = rule,
            Status = reason is null ? RuleApplicationStatus.Active : RuleApplicationStatus.Inactive,
            InactiveReason = reason?.ToString(),
            MatchedProcessCount = CountMatching(rule, running),
        };
    }

    private static int CountMatching(RuleDto rule, IReadOnlySet<string> running)
    {
        // Comptage par chemin exact, puis par nom si le chemin a disparu — le meme ordre que
        // l'appariement reel (FR-039). Compter autrement afficherait un nombre que la
        // limitation ne suit pas.
        if (running.Contains(rule.Target.ExecutablePath))
        {
            return 1;
        }

        string suffix = '\\' + rule.Target.ExecutableName;

        return running.Count(path => path.EndsWith(suffix, StringComparison.Ordinal));
    }
}
