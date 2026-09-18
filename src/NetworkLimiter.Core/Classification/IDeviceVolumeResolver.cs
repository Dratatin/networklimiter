namespace NetworkLimiter.Core.Classification;

/// <summary>
/// Traduit un nom de périphérique Windows en lettre de lecteur.
/// </summary>
/// <remarks>
/// <para>
/// Les chemins remontés par les API noyau sont de la forme
/// <c>\Device\HarddiskVolume3\Program Files\App\app.exe</c>. Les traduire en
/// <c>C:\Program Files\App\app.exe</c> exige <c>QueryDosDevice</c>, une API Windows.
/// </para>
/// <para>
/// Cette interface existe pour que <c>NetworkLimiter.Core</c> reste sans dépendance à
/// Windows, comme l'impose le principe III. L'implémentation réelle vit dans le service ;
/// les tests en injectent une table fixe. C'est l'une des rares abstractions du projet, et
/// elle est justifiée par deux implémentations réelles, pas par anticipation.
/// </para>
/// </remarks>
public interface IDeviceVolumeResolver
{
    /// <summary>
    /// Tente de traduire un nom de périphérique, tel que <c>\Device\HarddiskVolume3</c>, en
    /// lettre de lecteur, telle que <c>C:</c>.
    /// </summary>
    /// <returns><c>true</c> si la traduction a réussi.</returns>
    bool TryResolve(string deviceName, out string driveLetter);
}
