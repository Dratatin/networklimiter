namespace NetworkLimiter.App.ViewModels;

/// <summary>Aspect de l'icône de zone de notification.</summary>
public enum TrayAppearance
{
    /// <summary>La limitation s'applique.</summary>
    Limiting,

    /// <summary>La limitation est suspendue par l'utilisateur.</summary>
    Suspended,

    /// <summary>Rien ne s'applique, et ce n'est pas voulu.</summary>
    Impaired,
}

/// <summary>Ce que l'icône doit montrer et proposer.</summary>
/// <param name="Appearance">Aspect, qui détermine la couleur.</param>
/// <param name="Tooltip">Infobulle, au plus 63 caractères.</param>
/// <param name="ToggleLabel">Libellé de l'entrée de suspension, décrivant l'action.</param>
/// <param name="CanToggle">La suspension est actionnable.</param>
public sealed record TrayPresentation(
    TrayAppearance Appearance,
    string Tooltip,
    string ToggleLabel,
    bool CanToggle)
{
    /// <summary>
    /// Longueur maximale d'une infobulle de zone de notification.
    /// </summary>
    /// <remarks>
    /// Limite de Windows : 64 caractères terminaison comprise. Au-delà, l'API tronque
    /// silencieusement — et c'est la fin du texte, donc l'information, qui disparaît.
    /// </remarks>
    public const int MaxTooltipLength = 63;

    /// <summary>
    /// Compose l'aspect à partir de l'état observé.
    /// </summary>
    /// <remarks>
    /// <para>
    /// L'icône est souvent la <b>seule</b> chose que l'utilisateur regarde : la fenêtre reste
    /// fermée la plupart du temps. Elle doit donc distinguer les trois situations qui comptent,
    /// et surtout ne jamais laisser croire que tout va bien quand rien ne s'applique.
    /// </para>
    /// <para>
    /// La suspension et la panne partagent un symptôme — aucune limite — mais pas une couleur :
    /// l'une est un choix, l'autre un problème.
    /// </para>
    /// </remarks>
    /// <param name="connected">Le service répond.</param>
    /// <param name="suspended">La limitation est suspendue.</param>
    /// <param name="interceptionActive">L'interception est opérationnelle.</param>
    /// <param name="activeRuleCount">Nombre de règles appliquées.</param>
    public static TrayPresentation Compose(
        bool connected,
        bool suspended,
        bool interceptionActive,
        int activeRuleCount)
    {
        if (!connected)
        {
            return new TrayPresentation(
                TrayAppearance.Impaired,
                Trim("NetworkLimiter — service injoignable"),
                "Suspendre la limitation",
                CanToggle: false);
        }

        if (suspended)
        {
            return new TrayPresentation(
                TrayAppearance.Suspended,
                Trim("NetworkLimiter — limitation suspendue"),
                "Réappliquer les limites",
                CanToggle: true);
        }

        if (!interceptionActive)
        {
            return new TrayPresentation(
                TrayAppearance.Impaired,
                Trim("NetworkLimiter — interception inopérante"),
                "Suspendre la limitation",
                CanToggle: false);
        }

        string state = activeRuleCount == 0
            ? "aucune limite définie"
            : $"{activeRuleCount} limite(s) active(s)";

        return new TrayPresentation(
            TrayAppearance.Limiting,
            Trim($"NetworkLimiter — {state}"),
            "Suspendre la limitation",
            CanToggle: true);
    }

    private static string Trim(string text) =>
        text.Length <= MaxTooltipLength ? text : text[..MaxTooltipLength];
}
