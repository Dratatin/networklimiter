using CommunityToolkit.Mvvm.ComponentModel;
using NetworkLimiter.Contracts.Messages;

namespace NetworkLimiter.App.ViewModels;

/// <summary>Gravité d'un point d'état de santé.</summary>
public enum HealthSeverity
{
    /// <summary>Tout va bien.</summary>
    Ok,

    /// <summary>La limitation fonctionne, mais quelque chose mérite d'être su.</summary>
    Warning,

    /// <summary>Aucune limite ne s'applique.</summary>
    Blocking,
}

/// <summary>Un constat d'état de santé, avec ce qu'il faut en faire.</summary>
/// <param name="Severity">Gravité.</param>
/// <param name="Title">Constat, en une ligne.</param>
/// <param name="Action">Ce que l'utilisateur peut faire, ou <c>null</c> s'il n'y a rien à faire.</param>
public sealed record HealthFinding(HealthSeverity Severity, string Title, string? Action);

/// <summary>
/// Traduit l'état de santé en constats actionnables (T120, FR-026).
/// </summary>
/// <remarks>
/// <para>
/// Toute la valeur est dans le champ <c>Action</c>. Afficher « pilote non chargé » informe sans
/// aider ; l'outil doit dire <b>quoi faire</b>, sinon il transforme un problème technique en
/// impasse pour quelqu'un qui n'a pas à connaître WinDivert.
/// </para>
/// <para>
/// Les constats sont ordonnés du plus englobant au plus local, et le premier bloquant suffit :
/// annoncer « application non lancée » à quelqu'un dont la machine est hors matrice l'enverrait
/// chercher pendant une heure au mauvais endroit.
/// </para>
/// </remarks>
public sealed partial class HealthViewModel : ObservableObject
{
    [ObservableProperty]
    private HealthResultPayload? _health;

    /// <summary>Constats, du plus grave au plus anodin.</summary>
    public IReadOnlyList<HealthFinding> Findings => Health is { } health ? Describe(health) : [Unreachable];

    /// <summary>La limitation s'applique réellement.</summary>
    public bool IsHealthy => Findings.All(finding => finding.Severity == HealthSeverity.Ok);

    /// <summary>Résumé d'une ligne, pour un affichage compact.</summary>
    public string Summary => Findings[0].Title;

    /// <summary>Gravité la plus élevée constatée.</summary>
    public HealthSeverity Severity => Findings.Max(finding => finding.Severity);

    partial void OnHealthChanged(HealthResultPayload? value)
    {
        OnPropertyChanged(nameof(Findings));
        OnPropertyChanged(nameof(IsHealthy));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Severity));
    }

    private static HealthFinding Unreachable { get; } = new(
        HealthSeverity.Blocking,
        "Le service NetworkLimiter est injoignable.",
        "Vérifiez qu'il est démarré. Aucune limite n'est appliquée tant qu'il ne répond pas.");

    private static List<HealthFinding> Describe(HealthResultPayload health)
    {
        var findings = new List<HealthFinding>();

        // L'ordre suit celui des causes. Une machine hors matrice rend tout le reste sans
        // objet : inutile de parler de pilote a quelqu'un dont l'architecture n'a jamais eu
        // de pilote disponible.
        if (!health.Compatibility.Supported)
        {
            findings.Add(new HealthFinding(
                HealthSeverity.Blocking,
                "Cette machine n'est pas prise en charge.",
                health.Compatibility.Diagnostic ??
                    $"Windows build {health.Compatibility.OsBuild}, architecture " +
                    $"{health.Compatibility.Architecture}."));

            return findings;
        }

        if (!health.DriverLoaded)
        {
            findings.Add(new HealthFinding(
                HealthSeverity.Blocking,
                "Le pilote d'interception n'est pas chargé.",
                health.DegradedReason ??
                    "Réinstallez NetworkLimiter. Si le problème persiste, vérifiez que " +
                    "l'intégrité de la mémoire (Sécurité Windows › Sécurité des appareils) " +
                    "n'empêche pas le chargement du pilote."));

            return findings;
        }

        if (health.Suspended)
        {
            // Volontaire, donc ni une panne ni une anomalie : un avertissement qui rappelle
            // l'etat et le geste pour en sortir.
            findings.Add(new HealthFinding(
                HealthSeverity.Warning,
                "La limitation est suspendue.",
                "Vos règles sont conservées. Utilisez « Réappliquer les limites » pour reprendre."));
        }
        else if (!health.InterceptionActive)
        {
            findings.Add(new HealthFinding(
                HealthSeverity.Blocking,
                "L'interception n'est pas opérationnelle.",
                health.DegradedReason ?? "Consultez le journal du service."));
        }

        if (health.DegradedReason is { Length: > 0 } reason && health.InterceptionActive)
        {
            findings.Add(new HealthFinding(HealthSeverity.Warning, reason, null));
        }

        if (health.VpnWarning is { Length: > 0 } vpn)
        {
            findings.Add(new HealthFinding(
                HealthSeverity.Warning,
                vpn,
                "Vérifiez vos débits réels si vous comptez sur les limites pendant que le VPN est actif."));
        }

        if (health.ConfigWarning is { Length: > 0 } config)
        {
            findings.Add(new HealthFinding(
                HealthSeverity.Warning,
                config,
                "Corrigez ou réinitialisez la configuration depuis l'interface."));
        }

        if (health.InactiveRuleCount > 0)
        {
            findings.Add(new HealthFinding(
                HealthSeverity.Warning,
                $"{health.InactiveRuleCount} règle(s) définie(s) ne s'appliquent pas.",
                "Chaque règle concernée indique sa raison dans la liste."));
        }

        if (findings.Count == 0)
        {
            findings.Add(new HealthFinding(
                HealthSeverity.Ok,
                health.ActiveRuleCount == 0
                    ? "La limitation fonctionne. Aucune règle n'est définie."
                    : $"La limitation fonctionne. {health.ActiveRuleCount} règle(s) appliquée(s).",
                null));
        }

        return findings;
    }
}
