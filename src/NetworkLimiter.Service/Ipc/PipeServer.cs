using System.IO.Pipes;

namespace NetworkLimiter.Service.Ipc;

/// <summary>
/// Crée les instances du tuyau nommé de contrôle.
/// </summary>
/// <remarks>
/// <para>
/// Le canal est <b>local uniquement</b> : aucun socket TCP, même sur la boucle locale
/// (constitution, contraintes techniques). La protection contre l'accès distant par SMB est
/// portée par la DACL de <see cref="PipeSecurityFactory"/>.
/// </para>
/// <para>
/// La boucle d'acceptation des connexions et le traitement des messages viendront avec les
/// gestionnaires de chaque user story ; cette classe ne porte que la création correctement
/// sécurisée d'une instance.
/// </para>
/// </remarks>
public static class PipeServer
{
    /// <summary>
    /// Nom du tuyau, versionné.
    /// </summary>
    /// <remarks>
    /// La version figure dans le nom pour que deux versions incompatibles ne se rencontrent
    /// jamais sur le même canal. La vérification de version de la poignée de main reste
    /// nécessaire — elle traite le cas d'un changement de contrat à l'intérieur d'une même
    /// version majeure — mais le nom évite le dialogue de sourds le plus grossier.
    /// </remarks>
    public const string PipeName = "NetworkLimiter.v1";

    /// <summary>
    /// Nombre maximal d'instances simultanées du tuyau.
    /// </summary>
    /// <remarks>
    /// Borné volontairement : sans limite, un utilisateur local ouvrirait autant de
    /// connexions qu'il le souhaite et épuiserait les ressources du service. Quatre suffisent
    /// largement — l'interface n'en ouvre qu'une, plus une éventuelle instance élevée.
    /// </remarks>
    public const int MaxServerInstances = 4;

    /// <summary>Délai d'inactivité au-delà duquel une connexion est fermée.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private const int BufferBytes = 64 * 1024;

    /// <summary>
    /// Crée une instance du tuyau, prête à accepter une connexion.
    /// </summary>
    /// <remarks>
    /// Le mode message préserve les frontières entre messages côté Windows ; le cadrage
    /// applicatif reste néanmoins appliqué, parce qu'on ne fait pas reposer une garantie de
    /// sécurité sur une seule couche.
    /// </remarks>
    public static NamedPipeServerStream Create() =>
        NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            MaxServerInstances,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: BufferBytes,
            outBufferSize: BufferBytes,
            PipeSecurityFactory.Create());
}
