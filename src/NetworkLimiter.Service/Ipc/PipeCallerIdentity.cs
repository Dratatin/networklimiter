using System.IO.Pipes;
using System.Security.Principal;

namespace NetworkLimiter.Service.Ipc;

/// <summary>
/// Constate l'élévation de l'appelant en l'impersonnant sur le tuyau nommé.
/// </summary>
/// <remarks>
/// L'impersonation est strictement encadrée : elle ne dure que le temps de lire le jeton, et
/// le service ne fait <b>rien d'autre</b> sous l'identité de l'appelant. Agir sous une
/// identité moins privilégiée pendant un traitement produirait des échecs imprévisibles ;
/// agir sous une identité qu'on n'a pas vérifiée serait pire.
/// </remarks>
public sealed class PipeCallerIdentity : ICallerIdentity
{
    private readonly NamedPipeServerStream _pipe;

    /// <summary>Crée l'identité pour une connexion donnée.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="pipe"/> est <c>null</c>.</exception>
    public PipeCallerIdentity(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        _pipe = pipe;
    }

    /// <inheritdoc />
    public bool IsElevatedAdministrator
    {
        get
        {
            bool elevated = false;

            _pipe.RunAsClient(() =>
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                elevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
            });

            return elevated;
        }
    }
}
