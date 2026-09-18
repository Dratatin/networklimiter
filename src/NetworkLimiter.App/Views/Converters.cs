using System.Globalization;
using System.Windows;
using System.Windows.Data;
using NetworkLimiter.App.ViewModels;

namespace NetworkLimiter.App.Views;

/// <summary>Inverse un booléen.</summary>
public sealed class NotConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && !flag;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && !flag;
}

/// <summary>Affiche un élément seulement si la valeur est vraie.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>Inverse la condition.</summary>
    public bool Invert { get; set; }

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool boolean && boolean;

        return flag != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Conversion unidirectionnelle.");
}

/// <summary>Affiche un élément seulement si la valeur liée n'est pas <c>null</c>.</summary>
/// <remarks>
/// Distinct de <see cref="TextToVisibilityConverter"/>, et l'oubli de cette distinction a
/// coûté un bug : la superposition de l'éditeur était liée par le convertisseur de texte à un
/// modèle de vue. <c>value as string</c> rendait <c>null</c> pour tout objet non textuel, donc
/// <c>Collapsed</c> en permanence — l'éditeur ne se serait jamais ouvert, et le bouton aurait
/// paru inerte sans qu'aucune erreur ne soit signalée.
/// </remarks>
public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Conversion unidirectionnelle.");
}

/// <summary>Affiche un élément seulement si le texte est renseigné.</summary>
/// <remarks>
/// Évite le piège classique d'un bandeau d'erreur qui occupe la place alors qu'il est vide :
/// l'utilisateur voit un espace inexpliqué là où il attend du contenu.
/// </remarks>
public sealed class TextToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Conversion unidirectionnelle.");
}

/// <summary>Relie l'unité de débit à l'index d'une liste déroulante.</summary>
/// <remarks>
/// La liste énumère les unités dans l'ordre de <see cref="RateUnit"/>. Passer par l'index plutôt
/// que par la valeur évite de dupliquer les libellés dans le code, où ils divergeraient du XAML
/// à la première traduction.
/// </remarks>
public sealed class RateUnitIndexConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RateUnit unit ? (int)unit : 0;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index && Enum.IsDefined((RateUnit)index)
            ? (RateUnit)index
            : RateUnit.MegabytesPerSecond;
}
