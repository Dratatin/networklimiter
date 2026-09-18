using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using NetworkLimiter.Core.Units;

namespace NetworkLimiter.App.ViewModels;

/// <summary>Unité de saisie d'un débit.</summary>
public enum RateUnit
{
    /// <summary>Kilo-octets par seconde, base 1024.</summary>
    KilobytesPerSecond,

    /// <summary>Méga-octets par seconde, base 1024.</summary>
    MegabytesPerSecond,
}

/// <summary>
/// Saisie et validation d'un plafond.
/// </summary>
/// <remarks>
/// <para>
/// FR-007 impose de refuser une valeur hors bornes <b>à la saisie</b>, et d'indiquer les bornes
/// à ce moment-là plutôt qu'après validation. L'utilisateur doit comprendre pourquoi sa valeur
/// est refusée pendant qu'il la tape, pas après avoir cliqué.
/// </para>
/// <para>
/// L'unité est <b>toujours visible</b>. C'est le détail qui évite le malentendu le plus
/// courant du domaine : les offres commerciales annoncent des mégabits, l'outil manipule des
/// méga-octets, et un facteur huit sépare ce que l'utilisateur croit saisir de ce qu'il
/// saisit.
/// </para>
/// </remarks>
public sealed partial class RateInputViewModel : ObservableObject
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("fr-FR");

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private RateUnit _unit = RateUnit.MegabytesPerSecond;

    [ObservableProperty]
    private bool _unlimited = true;

    /// <summary>Message expliquant pourquoi la saisie est refusée, ou <c>null</c>.</summary>
    public string? ValidationMessage { get; private set; }

    /// <summary>La saisie courante est exploitable.</summary>
    public bool IsValid => ValidationMessage is null;

    /// <summary>Plafond résultant, ou <c>null</c> si illimité.</summary>
    public long? BytesPerSecond { get; private set; }

    /// <summary>Bornes rappelées à l'utilisateur, dans l'unité courante.</summary>
    public string BoundsHint => Unit == RateUnit.KilobytesPerSecond
        ? $"de {ToUnit(ByteRate.MinBytesPerSecond):0.##} à {ToUnit(ByteRate.MaxBytesPerSecond):0.##} Ko/s"
        : $"de {ToUnit(ByteRate.MinBytesPerSecond):0.####} à {ToUnit(ByteRate.MaxBytesPerSecond):0.##} Mo/s";

    partial void OnTextChanged(string value) => Revalidate();

    partial void OnUnitChanged(RateUnit value)
    {
        OnPropertyChanged(nameof(BoundsHint));
        Revalidate();
    }

    partial void OnUnlimitedChanged(bool value) => Revalidate();

    private void Revalidate()
    {
        (ValidationMessage, BytesPerSecond) = Evaluate();

        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(BytesPerSecond));
    }

    private (string? Message, long? Value) Evaluate()
    {
        if (Unlimited)
        {
            return (null, null);
        }

        if (string.IsNullOrWhiteSpace(Text))
        {
            return ("Saisissez un plafond, ou cochez « illimité ».", null);
        }

        // La virgule ET le point sont acceptes : l'utilisateur francais tape « 1,5 » au pave
        // numerique mais « 1.5 » sur un clavier programmeur, et refuser l'un des deux serait
        // une friction gratuite.
        string normalized = Text.Trim().Replace(',', '.');

        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            double.IsNaN(value) || double.IsInfinity(value))
        {
            return ("Cette valeur n'est pas un nombre.", null);
        }

        if (value <= 0)
        {
            // Zero serait un blocage total, hors perimetre (FR-028).
            return ("Un plafond doit être strictement positif.", null);
        }

        double bytes = value * BytesPerUnit(Unit);

        if (bytes > long.MaxValue)
        {
            return ($"Ce plafond dépasse le maximum ({BoundsHint}).", null);
        }

        long rounded = (long)Math.Round(bytes);

        if (!ByteRate.IsValid(rounded))
        {
            return ($"Le plafond doit être compris {BoundsHint}.", null);
        }

        return (null, rounded);
    }

    private double ToUnit(long bytesPerSecond) => bytesPerSecond / BytesPerUnit(Unit);

    private static double BytesPerUnit(RateUnit unit) => unit switch
    {
        RateUnit.KilobytesPerSecond => 1024d,
        RateUnit.MegabytesPerSecond => 1024d * 1024d,
        _ => 1d,
    };

    /// <summary>Renseigne la saisie depuis un plafond existant.</summary>
    public void LoadFrom(long? bytesPerSecond)
    {
        if (bytesPerSecond is not { } value)
        {
            Unlimited = true;
            Text = string.Empty;
            return;
        }

        Unlimited = false;

        // Choisit l'unite la plus lisible : « 1,5 Mo/s » plutot que « 1536 Ko/s ».
        Unit = value >= 1024 * 1024 ? RateUnit.MegabytesPerSecond : RateUnit.KilobytesPerSecond;
        Text = ToUnit(value).ToString("0.##", Culture);
    }
}
