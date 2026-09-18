using System.Windows.Controls;

namespace NetworkLimiter.App.Views;

/// <summary>
/// Éditeur d'une règle (T073).
/// </summary>
/// <remarks>
/// Sans code : toute la logique — validation à la saisie, bornes, blocage de l'enregistrement —
/// vit dans <see cref="ViewModels.RuleEditorSessionViewModel"/> et
/// <see cref="ViewModels.RateInputViewModel"/>, où elle se teste sans ouvrir de fenêtre.
/// </remarks>
public partial class RuleEditorView : UserControl
{
    /// <summary>Crée la vue.</summary>
    public RuleEditorView() => InitializeComponent();
}
