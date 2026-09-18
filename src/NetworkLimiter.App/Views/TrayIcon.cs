using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using NetworkLimiter.App.ViewModels;

namespace NetworkLimiter.App.Views;

/// <summary>
/// Icône de zone de notification et suspension rapide (T121, FR-024).
/// </summary>
/// <remarks>
/// <para>
/// La fenêtre reste fermée la plupart du temps : cette icône est souvent la <b>seule</b> chose
/// que l'utilisateur voit de l'outil. Elle porte donc les deux gestes qui ne doivent jamais
/// demander d'effort — savoir si quelque chose est limité, et tout suspendre.
/// </para>
/// <para>
/// <c>NotifyIcon</c> vient de Windows Forms, livré avec le framework. L'alternative — piloter
/// <c>Shell_NotifyIcon</c> à la main — exigerait une fenêtre cachée, une procédure de fenêtre et
/// la gestion du message de redémarrage de l'explorateur : beaucoup de code natif pour reproduire
/// ce que le framework fournit déjà, et autant d'occasions de se tromper. Ce n'est pas une
/// dépendance externe, donc aucune surface de chaîne d'approvisionnement en plus.
/// </para>
/// <para>
/// Aucune décision d'affichage n'est prise ici : <see cref="TrayPresentation"/> les porte toutes
/// et se vérifie sans zone de notification.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly Dictionary<TrayAppearance, Icon> _icons = [];

    private TrayPresentation _current =
        TrayPresentation.Compose(connected: false, suspended: false, interceptionActive: false, 0);

    /// <summary>Crée l'icône et son menu.</summary>
    public TrayIcon()
    {
        _toggleItem = new ToolStripMenuItem(_current.ToggleLabel);
        _toggleItem.Click += (_, _) => ToggleRequested?.Invoke(this, EventArgs.Empty);

        var openItem = new ToolStripMenuItem("Ouvrir NetworkLimiter");
        openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new ToolStripMenuItem("Quitter");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenuStrip();
        menu.Items.Add(openItem);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Text = _current.Tooltip,
            Icon = GetIcon(_current.Appearance),
            Visible = true,
        };

        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>L'utilisateur demande à voir la fenêtre.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>L'utilisateur demande à suspendre ou reprendre.</summary>
    public event EventHandler? ToggleRequested;

    /// <summary>L'utilisateur demande à quitter.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Met l'icône à jour.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="presentation"/> est <c>null</c>.</exception>
    public void Update(TrayPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);

        if (presentation == _current)
        {
            // Rien n'a change : reaffecter l'icone la fait clignoter dans la zone de
            // notification, dix fois par minute, pour rien.
            return;
        }

        _current = presentation;

        _icon.Text = presentation.Tooltip;
        _icon.Icon = GetIcon(presentation.Appearance);
        _toggleItem.Text = presentation.ToggleLabel;
        _toggleItem.Enabled = presentation.CanToggle;
    }

    /// <summary>
    /// Rend l'icône correspondant à un aspect, en la dessinant au besoin.
    /// </summary>
    /// <remarks>
    /// Dessinée plutôt que livrée en fichier : trois pastilles de couleur ne justifient pas
    /// trois binaires à versionner, et un fichier absent à l'exécution donnerait une icône
    /// vide sans la moindre explication.
    /// </remarks>
    private Icon GetIcon(TrayAppearance appearance)
    {
        if (_icons.TryGetValue(appearance, out Icon? cached))
        {
            return cached;
        }

        Color color = appearance switch
        {
            TrayAppearance.Limiting => Color.FromArgb(46, 125, 50),
            TrayAppearance.Suspended => Color.FromArgb(230, 145, 20),
            _ => Color.FromArgb(176, 0, 32),
        };

        using var bitmap = new Bitmap(16, 16);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, 1, 1, 14, 14);
        }

        Icon icon = Icon.FromHandle(bitmap.GetHicon());
        _icons[appearance] = icon;

        return icon;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Masquer AVANT de liberer : une icone dont le processus meurt sans l'avoir retiree
        // reste affichee jusqu'a ce que l'utilisateur survole la zone de notification, et
        // laisse croire que l'outil tourne encore.
        _icon.Visible = false;

        // Le menu possede ses entrees : le liberer les libere. Liberer _toggleItem seul le
        // retirerait d'un menu encore vivant.
        _icon.ContextMenuStrip?.Dispose();
        _toggleItem.Dispose();
        _icon.Dispose();

        foreach (Icon icon in _icons.Values)
        {
            icon.Dispose();
        }

        _icons.Clear();
    }
}
