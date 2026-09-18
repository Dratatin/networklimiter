using System.Windows.Controls;

namespace NetworkLimiter.App.Views;

/// <summary>
/// Panneau d'état de santé (T118, FR-026).
/// </summary>
/// <remarks>
/// Sans code : le diagnostic — quelle cause nommer, dans quel ordre, avec quelle action — vit
/// dans <see cref="ViewModels.HealthViewModel"/>, où il se vérifie sans ouvrir de fenêtre. C'est
/// la partie qui compte : se tromper de cause envoie l'utilisateur chercher au mauvais endroit.
/// </remarks>
public partial class HealthView : UserControl
{
    /// <summary>Crée la vue.</summary>
    public HealthView() => InitializeComponent();
}
