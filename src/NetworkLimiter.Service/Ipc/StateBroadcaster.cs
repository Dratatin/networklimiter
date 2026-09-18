using NetworkLimiter.Contracts.Messages;
using NetworkLimiter.Contracts.Serialization;

namespace NetworkLimiter.Service.Ipc;

/// <summary>Une connexion cliente capable de recevoir des messages poussés.</summary>
public interface IBroadcastTarget
{
    /// <summary>Envoie un message au client, ou ne fait rien si la connexion est morte.</summary>
    ValueTask PushAsync(MessageEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// Diffuse les changements d'état à toutes les connexions.
/// </summary>
/// <remarks>
/// <para>
/// <b>À tous les clients, y compris non élevés</b> (FR-034b). C'est le point qui mérite d'être
/// dit : on pourrait croire qu'une interface sans droit d'écriture n'a pas à être notifiée. Si
/// on le faisait, deux fenêtres ouvertes côte à côte — une élevée, une non — afficheraient des
/// règles différentes, et l'utilisateur n'aurait aucun moyen de savoir laquelle a raison.
/// </para>
/// <para>
/// La diffusion ne doit jamais faire échouer l'écriture qui l'a provoquée. Une règle a été
/// validée et persistée ; qu'un client soit devenu injoignable entre-temps ne change rien à ce
/// fait, et rendre une erreur à l'appelant lui ferait croire que sa modification a échoué.
/// </para>
/// </remarks>
public sealed class StateBroadcaster
{
    private readonly List<IBroadcastTarget> _targets = [];
    private readonly Lock _gate = new();

    /// <summary>Nombre de connexions abonnées.</summary>
    public int TargetCount
    {
        get
        {
            lock (_gate)
            {
                return _targets.Count;
            }
        }
    }

    /// <summary>Inscrit une connexion.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> est <c>null</c>.</exception>
    public void Add(IBroadcastTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_gate)
        {
            _targets.Add(target);
        }
    }

    /// <summary>Retire une connexion. Sans effet si elle n'est pas inscrite.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> est <c>null</c>.</exception>
    public void Remove(IBroadcastTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_gate)
        {
            _targets.Remove(target);
        }
    }

    /// <summary>Diffuse un état à toutes les connexions inscrites.</summary>
    /// <returns>Nombre de connexions atteintes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> est <c>null</c>.</exception>
    public async Task<int> BroadcastAsync(GetStateResultPayload state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        IBroadcastTarget[] snapshot;

        lock (_gate)
        {
            // Copie avant l'envoi : une connexion qui se ferme pendant la diffusion se retire
            // de la liste, et parcourir la liste vivante leverait.
            snapshot = [.. _targets];
        }

        if (snapshot.Length == 0)
        {
            return 0;
        }

        MessageEnvelope envelope = MessageEnvelope.CreateRequest(
            MessageTypes.StateChanged,
            new StateChangedPayload { State = state });

        int reached = 0;

        foreach (IBroadcastTarget target in snapshot)
        {
            try
            {
                await target.PushAsync(envelope, cancellationToken).ConfigureAwait(false);
                reached++;
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Client parti : sa connexion se nettoiera d'elle-meme. Interrompre la boucle
                // ici priverait de notification tous les clients suivants a cause d'un seul.
                Remove(target);
            }
        }

        return reached;
    }
}
