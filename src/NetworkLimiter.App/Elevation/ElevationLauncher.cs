using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace NetworkLimiter.App.Elevation;

/// <summary>Issue d'une demande d'élévation.</summary>
public enum ElevationOutcome
{
    /// <summary>Une instance élevée a été lancée ; celle-ci doit se retirer.</summary>
    Launched,

    /// <summary>L'utilisateur a refusé l'invite UAC. L'état courant est inchangé.</summary>
    Declined,

    /// <summary>L'instance courante est déjà élevée.</summary>
    AlreadyElevated,

    /// <summary>Le lancement a échoué pour une autre raison.</summary>
    Failed,
}

/// <summary>Lance une instance élevée de l'application.</summary>
public interface IElevationLauncher
{
    /// <summary>Indique si l'instance courante est déjà élevée.</summary>
    bool IsElevated { get; }

    /// <summary>Demande l'élévation.</summary>
    ElevationOutcome RequestElevation();
}

/// <summary>
/// Relance l'application avec élévation.
/// </summary>
/// <remarks>
/// <para>
/// Implémente FR-034a à FR-034c. Un processus ne peut pas acquérir de privilèges après son
/// lancement : seul un <b>nouveau</b> processus peut naître élevé. Le modèle « une invite UAC
/// par session d'édition » en découle directement, et il évite au service d'avoir à mémoriser
/// une autorisation — ce qui serait une faille d'élévation par conception, puisque toute
/// connexion ultérieure en hériterait.
/// </para>
/// <para>
/// C'est le schéma qu'emploient le Gestionnaire des tâches et Process Explorer : familier à
/// l'utilisateur, et sans code privilégié résident.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ElevationLauncher : IElevationLauncher
{
    /// <summary>Commutateur passé à l'instance élevée.</summary>
    public const string ElevatedSwitch = "--elevated";

    /// <summary>Code d'erreur Windows correspondant au refus de l'invite UAC.</summary>
    private const int ErrorCancelled = 1223;

    private readonly Func<bool> _isElevated;
    private readonly string _executablePath;

    /// <summary>Crée un lanceur.</summary>
    public ElevationLauncher(Func<bool> isElevated, string? executablePath = null)
    {
        ArgumentNullException.ThrowIfNull(isElevated);

        _isElevated = isElevated;
        _executablePath = executablePath ?? Environment.ProcessPath ?? string.Empty;
    }

    /// <inheritdoc />
    public bool IsElevated => _isElevated();

    /// <inheritdoc />
    public ElevationOutcome RequestElevation()
    {
        if (IsElevated)
        {
            return ElevationOutcome.AlreadyElevated;
        }

        if (string.IsNullOrEmpty(_executablePath))
        {
            return ElevationOutcome.Failed;
        }

        var startInfo = new ProcessStartInfo(_executablePath)
        {
            // « runas » declenche l'invite UAC. UseShellExecute est obligatoire : sans lui,
            // le verbe est ignore et le processus naitrait avec les memes privileges.
            Verb = "runas",
            UseShellExecute = true,
            Arguments = ElevatedSwitch,
        };

        try
        {
            Process.Start(startInfo);
            return ElevationOutcome.Launched;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            // L'utilisateur a refuse. Ce n'est PAS une erreur : l'interface reste en lecture
            // seule, sans etat a moitie modifie (cas limite « elevation refusee » de la spec).
            return ElevationOutcome.Declined;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return ElevationOutcome.Failed;
        }
    }

    /// <summary>Indique si les arguments de ligne de commande demandent le mode élevé.</summary>
    public static bool WasLaunchedForElevation(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return Array.Exists(args, arg => string.Equals(arg, ElevatedSwitch, StringComparison.Ordinal));
    }
}
