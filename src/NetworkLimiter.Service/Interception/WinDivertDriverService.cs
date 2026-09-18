using System.ComponentModel;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace NetworkLimiter.Service.Interception;

/// <summary>État du service pilote.</summary>
public enum DriverServiceState
{
    /// <summary>Le service n'est pas enregistré : l'installation est incomplète.</summary>
    NotRegistered,

    /// <summary>Enregistré mais arrêté : le pilote n'est pas chargé.</summary>
    Stopped,

    /// <summary>Chargé et prêt à accepter des handles.</summary>
    Running,

    /// <summary>L'état n'a pas pu être lu.</summary>
    Unknown,
}

/// <summary>Résultat d'une tentative de démarrage du pilote.</summary>
/// <param name="Started">Le pilote est chargé à l'issue de l'appel.</param>
/// <param name="WasAlreadyRunning">Il l'était déjà avant l'appel.</param>
/// <param name="Win32ErrorCode">Code d'erreur Windows en cas d'échec, sinon zéro.</param>
/// <param name="Diagnostic">Message exploitable, ou <c>null</c> si réussi.</param>
public sealed record DriverStartResult(
    bool Started,
    bool WasAlreadyRunning,
    int Win32ErrorCode,
    string? Diagnostic);

/// <summary>
/// Pilote le cycle de vie du service noyau WinDivert.
/// </summary>
/// <remarks>
/// <para>
/// Comble une lacune de conception que seule l'exécution sur une machine réelle a révélée.
/// R-011 prescrit d'enregistrer le pilote à l'installation et d'ouvrir les handles avec
/// <c>WINDIVERT_FLAG_NO_INSTALL</c> — mais ne disait pas <b>qui démarre le pilote</b>.
/// </para>
/// <para>
/// Le service pilote est déclaré à démarrage « à la demande ». Ouvrir un périphérique ne
/// déclenche <b>pas</b> le chargement d'un service noyau : quelqu'un doit appeler
/// <c>StartService</c>. Sans <c>NO_INSTALL</c>, WinDivert le fait lui-même ; avec, personne ne
/// le faisait, et l'ouverture échouait sur un <c>ERROR_SERVICE_DOES_NOT_EXIST</c> trompeur
/// alors que le service était bel et bien enregistré.
/// </para>
/// <para>
/// Le pilote n'est délibérément <b>pas arrêté</b> à l'extinction du service. Un autre outil
/// peut utiliser WinDivert sur la même machine — clumsy, GoodbyeDPI et d'autres s'en servent —
/// et l'arrêter lui couperait le réseau. Un pilote chargé sans handle ouvert ne détourne rien :
/// le laisser en place est sans conséquence, alors que l'arrêter peut en avoir. C'est la
/// désinstallation qui retire le service.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WinDivertDriverService
{
    /// <summary>Nom du service noyau, tel que l'installeur l'enregistre.</summary>
    public const string ServiceName = "WinDivert";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    private readonly string _serviceName;

    /// <summary>Crée un pilote de service.</summary>
    /// <param name="serviceName">
    /// Nom du service noyau. Paramétrable pour que les tests puissent viser un service
    /// inexistant ou factice sans toucher au pilote réel de la machine.
    /// </param>
    public WinDivertDriverService(string? serviceName = null)
    {
        _serviceName = string.IsNullOrWhiteSpace(serviceName) ? ServiceName : serviceName;
    }

    /// <summary>Nom du service noyau piloté.</summary>
    public string TargetServiceName => _serviceName;

    /// <summary>Lit l'état courant du service pilote.</summary>
    public DriverServiceState GetState()
    {
        try
        {
            using var controller = new ServiceController(_serviceName);

            return controller.Status switch
            {
                ServiceControllerStatus.Running => DriverServiceState.Running,
                ServiceControllerStatus.Stopped => DriverServiceState.Stopped,
                ServiceControllerStatus.StartPending => DriverServiceState.Stopped,
                _ => DriverServiceState.Unknown,
            };
        }
        catch (InvalidOperationException)
        {
            // ServiceController leve InvalidOperationException quand le service n'existe pas.
            return DriverServiceState.NotRegistered;
        }
    }

    /// <summary>
    /// S'assure que le pilote est chargé, en le démarrant si nécessaire.
    /// </summary>
    /// <remarks>
    /// Idempotent : un pilote déjà chargé n'est pas redémarré. Redémarrer un pilote en cours
    /// d'usage invaliderait les handles d'un autre outil.
    /// </remarks>
    public DriverStartResult EnsureRunning()
    {
        try
        {
            using var controller = new ServiceController(_serviceName);

            if (controller.Status == ServiceControllerStatus.Running)
            {
                return new DriverStartResult(
                    Started: true, WasAlreadyRunning: true, Win32ErrorCode: 0, Diagnostic: null);
            }

            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);

            return new DriverStartResult(
                Started: true, WasAlreadyRunning: false, Win32ErrorCode: 0, Diagnostic: null);
        }
        catch (InvalidOperationException exception)
        {
            // La cause reelle est dans l'exception interne : c'est elle qui porte le code
            // Windows du refus de chargement — signature rejetee, strategie de securite,
            // ressources insuffisantes.
            int code = (exception.InnerException as Win32Exception)?.NativeErrorCode ?? 0;

            return new DriverStartResult(
                Started: false,
                WasAlreadyRunning: false,
                Win32ErrorCode: code,
                Diagnostic: DescribeStartFailure(code, exception));
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return new DriverStartResult(
                Started: false,
                WasAlreadyRunning: false,
                Win32ErrorCode: 0,
                Diagnostic: $"Le pilote n'a pas démarré en moins de {StartTimeout.TotalSeconds:0} secondes.");
        }
    }

    private static string DescribeStartFailure(int code, Exception exception) => code switch
    {
        0 => $"Démarrage du pilote impossible : {exception.Message}",

        5 => "Accès refusé : le démarrage d'un service noyau exige des privilèges administrateur.",

        577 => "WINDOWS A REFUSÉ LA SIGNATURE DU PILOTE. Vérifiez Sécurité Windows > Sécurité " +
               "des appareils > Isolation du noyau > Intégrité de la mémoire : si elle est " +
               "activée, WinDivert ne peut pas se charger.",

        1058 => "Le service du pilote est désactivé.",

        1060 => "Le service du pilote n'est pas enregistré. L'installation est incomplète.",

        1275 => "Une stratégie de sécurité bloque le chargement de ce pilote.",

        _ => $"Démarrage du pilote impossible (code Windows {code}).",
    };
}
