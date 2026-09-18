using System.Globalization;

namespace NetworkLimiter.Core.Units;

/// <summary>
/// Un débit exprimé en octets par seconde, garanti dans les bornes supportées.
/// </summary>
/// <remarks>
/// <para>
/// La validation vit dans le type plutôt que dans l'interface, pour qu'aucun chemin d'entrée
/// — saisie, message IPC, fichier de configuration importé — ne puisse la contourner. Une
/// instance de <see cref="ByteRate"/> est par construction une valeur applicable.
/// </para>
/// <para>
/// L'absence de plafond se représente par <c>ByteRate?</c> valant <c>null</c>, jamais par
/// zéro : zéro serait un débit nul, c'est-à-dire un blocage, que FR-028 exclut du périmètre.
/// </para>
/// </remarks>
public readonly record struct ByteRate : IComparable<ByteRate>
{
    /// <summary>Plafond minimal supporté : 10 Ko/s.</summary>
    public const long MinBytesPerSecond = 10_240;

    /// <summary>Plafond maximal supporté : 1 Go/s.</summary>
    public const long MaxBytesPerSecond = 1_073_741_824;

    private const long BytesPerKilobyte = 1024;
    private const long BytesPerMegabyte = 1024 * BytesPerKilobyte;
    private const long BytesPerGigabyte = 1024 * BytesPerMegabyte;

    private ByteRate(long bytesPerSecond) => BytesPerSecond = bytesPerSecond;

    /// <summary>Le débit, en octets par seconde.</summary>
    public long BytesPerSecond { get; }

    /// <summary>Indique si une valeur brute est un débit applicable.</summary>
    public static bool IsValid(long bytesPerSecond) =>
        bytesPerSecond >= MinBytesPerSecond && bytesPerSecond <= MaxBytesPerSecond;

    /// <summary>Crée un débit, ou lève si la valeur est hors bornes.</summary>
    /// <exception cref="ArgumentOutOfRangeException">La valeur est hors des bornes supportées.</exception>
    public static ByteRate FromBytesPerSecond(long bytesPerSecond)
    {
        if (!IsValid(bytesPerSecond))
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytesPerSecond),
                bytesPerSecond,
                $"Le débit doit être compris entre {MinBytesPerSecond} et {MaxBytesPerSecond} octets par seconde.");
        }

        return new ByteRate(bytesPerSecond);
    }

    /// <summary>
    /// Tente de créer un débit sans lever. Destiné à la validation à la saisie (FR-007).
    /// </summary>
    public static bool TryCreate(long bytesPerSecond, out ByteRate rate)
    {
        if (!IsValid(bytesPerSecond))
        {
            rate = default;
            return false;
        }

        rate = new ByteRate(bytesPerSecond);
        return true;
    }

    /// <summary>
    /// Rend le plus restrictif de deux débits. Support direct de FR-010, où le plafond
    /// effectif est le minimum entre la règle et le plafond global.
    /// </summary>
    public static ByteRate Min(ByteRate left, ByteRate right) =>
        left.BytesPerSecond <= right.BytesPerSecond ? left : right;

    /// <summary>
    /// Rend le plus restrictif de deux plafonds éventuellement absents, <c>null</c> valant
    /// « illimité ». Deux absences donnent une absence.
    /// </summary>
    public static ByteRate? MostRestrictive(ByteRate? left, ByteRate? right) =>
        (left, right) switch
        {
            (null, null) => null,
            (null, { } r) => r,
            ({ } l, null) => l,
            ({ } l, { } r) => Min(l, r),
        };

    /// <inheritdoc />
    public int CompareTo(ByteRate other) => BytesPerSecond.CompareTo(other.BytesPerSecond);

    public static bool operator <(ByteRate left, ByteRate right) => left.CompareTo(right) < 0;

    public static bool operator >(ByteRate left, ByteRate right) => left.CompareTo(right) > 0;

    public static bool operator <=(ByteRate left, ByteRate right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ByteRate left, ByteRate right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// Rend une forme lisible avec son unité, en base 1024.
    /// </summary>
    /// <remarks>
    /// L'unité est toujours présente : FR-007 l'exige pour lever l'ambiguïté avec les Kb/s
    /// des offres commerciales, où un facteur huit sépare ce que l'utilisateur croit saisir
    /// de ce qu'il saisit réellement.
    /// </remarks>
    public override string ToString()
    {
        (double value, string unit) = BytesPerSecond switch
        {
            >= BytesPerGigabyte => (BytesPerSecond / (double)BytesPerGigabyte, "Go/s"),
            >= BytesPerMegabyte => (BytesPerSecond / (double)BytesPerMegabyte, "Mo/s"),
            _ => (BytesPerSecond / (double)BytesPerKilobyte, "Ko/s"),
        };

        return string.Create(
            CultureInfo.GetCultureInfo("fr-FR"),
            $"{value:0.##} {unit}");
    }
}
