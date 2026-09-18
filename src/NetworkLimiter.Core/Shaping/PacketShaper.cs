using NetworkLimiter.Core.Classification;
using NetworkLimiter.Core.Units;

namespace NetworkLimiter.Core.Shaping;

/// <summary>Plafonds appliqués à une règle.</summary>
/// <param name="RuleId">Identifiant de la règle.</param>
/// <param name="Download">Plafond descendant, ou <c>null</c> pour illimité.</param>
/// <param name="Upload">Plafond montant, ou <c>null</c> pour illimité.</param>
public sealed record ShaperRule(Guid RuleId, ByteRate? Download, ByteRate? Upload);

/// <summary>Un paquet soumis à la mise en forme.</summary>
/// <param name="Token">Référence opaque vers le paquet, gérée par l'appelant.</param>
/// <param name="RuleId">Règle qui vise ce paquet, ou <c>null</c> si aucune.</param>
/// <param name="Direction">Sens.</param>
/// <param name="Protocol">Protocole de transport.</param>
/// <param name="Scope">Portée réseau.</param>
/// <param name="SizeBytes">Taille, en octets.</param>
public readonly record struct ShapingRequest(
    long Token,
    Guid? RuleId,
    PacketDirection Direction,
    TransportProtocol Protocol,
    NetworkScope Scope,
    int SizeBytes);

/// <summary>Sort réservé à un paquet.</summary>
public enum ShapingOutcome
{
    /// <summary>
    /// Hors du champ de la limitation : réinjecté immédiatement, sans consommer de budget.
    /// </summary>
    /// <remarks>
    /// Trafic local ou de boucle locale, application sans règle, ou sens non plafonné. C'est
    /// le sort <b>par défaut</b> : dans le doute, on laisse passer (principe IV).
    /// </remarks>
    PassThrough,

    /// <summary>Dans le budget : réinjecté maintenant.</summary>
    Send,

    /// <summary>Hors budget mais temporisable : mis en file, réinjecté plus tard.</summary>
    Delay,

    /// <summary>Rejeté : soit la file déborde, soit la temporisation ne peut pas tenir le plafond.</summary>
    Drop,
}

/// <summary>
/// Applique les plafonds à chaque paquet.
/// </summary>
/// <remarks>
/// <para>
/// Assemble <see cref="TokenBucket"/>, <see cref="DelayQueue"/> et <see cref="DropPolicy"/> en
/// une décision par paquet. Aucune dépendance à Windows ni au pilote : la partie du produit
/// dont une erreur serait invisible se vérifie donc intégralement en temps simulé.
/// </para>
/// <para>
/// Le sort par défaut est <see cref="ShapingOutcome.PassThrough"/>. Tout ce qui n'est pas
/// explicitement soumis à un plafond passe sans être touché — trafic local, application sans
/// règle, flux inconnu. C'est le principe IV appliqué au grain du paquet : dans le doute, on
/// rend le réseau à l'utilisateur.
/// </para>
/// <para>
/// Cette classe n'est pas sûre vis-à-vis des accès concurrents ; elle est utilisée depuis la
/// seule boucle de mise en forme.
/// </para>
/// </remarks>
public sealed class PacketShaper
{
    private readonly TimeProvider _clock;
    private readonly Dictionary<Guid, DirectionalShaper> _byRule = [];

    /// <summary>Crée un metteur en forme.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> est <c>null</c>.</exception>
    public PacketShaper(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>Nombre de règles ayant au moins un plafond actif.</summary>
    public int ShapedRuleCount => _byRule.Count;

    /// <summary>Nombre total de paquets en attente, toutes règles confondues.</summary>
    public int QueuedPacketCount => _byRule.Values.Sum(shaper => shaper.QueuedCount);

    /// <summary>
    /// Remplace le jeu de plafonds.
    /// </summary>
    /// <remarks>
    /// Les paquets déjà en file pour une règle retirée sont <b>libérés</b>, jamais abandonnés :
    /// supprimer une règle ne doit pas faire disparaître des paquets déjà acceptés. L'appelant
    /// les récupère par <see cref="DrainReleased"/> et les réinjecte sans limitation.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="rules"/> est <c>null</c>.</exception>
    public void ApplyRules(IReadOnlyList<ShaperRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var seen = new HashSet<Guid>();

        foreach (ShaperRule rule in rules)
        {
            if (rule.Download is null && rule.Upload is null)
            {
                // Regle sans aucun plafond : valide mais sans effet. Inutile de lui allouer
                // des seaux ; son trafic passera par PassThrough.
                continue;
            }

            seen.Add(rule.RuleId);

            if (_byRule.TryGetValue(rule.RuleId, out DirectionalShaper? existing))
            {
                existing.UpdateRates(rule.Download, rule.Upload);
            }
            else
            {
                _byRule[rule.RuleId] = new DirectionalShaper(_clock, rule.Download, rule.Upload);
            }
        }

        foreach (Guid removed in _byRule.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _byRule[removed].ReleaseAll(_released);
            _byRule.Remove(removed);
        }
    }

    private readonly List<PendingPacket> _released = [];

    /// <summary>
    /// Décide du sort d'un paquet.
    /// </summary>
    public ShapingOutcome Evaluate(ShapingRequest request)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.SizeBytes, 0);

        // Seul le trafic internet est soumis aux plafonds (FR-040). Le trafic local et la
        // boucle locale passent sans consommer le moindre jeton : une sauvegarde vers un NAS
        // ne doit pas epuiser le budget d'une limite destinee a proteger la connexion.
        if (!NetworkScopeClassifier.IsSubjectToLimits(request.Scope))
        {
            return ShapingOutcome.PassThrough;
        }

        if (request.RuleId is not { } ruleId ||
            !_byRule.TryGetValue(ruleId, out DirectionalShaper? shaper))
        {
            return ShapingOutcome.PassThrough;
        }

        return shaper.Evaluate(request, _clock.GetTimestamp());
    }

    /// <summary>
    /// Retire les paquets dont le budget est désormais disponible.
    /// </summary>
    /// <returns>Nombre de paquets prêts, à réinjecter dans l'ordre rendu.</returns>
    public int DrainReady(List<PendingPacket> ready)
    {
        ArgumentNullException.ThrowIfNull(ready);

        int before = ready.Count;

        foreach (DirectionalShaper shaper in _byRule.Values)
        {
            shaper.DrainReady(ready);
        }

        return ready.Count - before;
    }

    /// <summary>
    /// Retire les paquets libérés par la suppression d'une règle.
    /// </summary>
    /// <remarks>
    /// Ils doivent être réinjectés <b>sans limitation</b> : la règle qui les retenait n'existe
    /// plus, et les garder en attente les perdrait définitivement.
    /// </remarks>
    public int DrainReleased(List<PendingPacket> released)
    {
        ArgumentNullException.ThrowIfNull(released);

        released.AddRange(_released);
        int count = _released.Count;
        _released.Clear();

        return count;
    }

    /// <summary>Nombre de paquets rejetés pour une règle donnée.</summary>
    public long GetDroppedPackets(Guid ruleId) =>
        _byRule.TryGetValue(ruleId, out DirectionalShaper? shaper) ? shaper.DroppedPackets : 0;

    /// <summary>
    /// Libère tous les paquets en attente et oublie les plafonds.
    /// </summary>
    /// <remarks>
    /// Appelé à la suspension et à la fermeture des handles. Aucun paquet ne doit rester
    /// prisonnier d'une file dont plus personne ne s'occupe (principe IV).
    /// </remarks>
    public void ReleaseAll(List<PendingPacket> released)
    {
        ArgumentNullException.ThrowIfNull(released);

        foreach (DirectionalShaper shaper in _byRule.Values)
        {
            shaper.ReleaseAll(released);
        }

        released.AddRange(_released);
        _released.Clear();
        _byRule.Clear();
    }

    /// <summary>Les deux sens d'une règle : un seau et une file par direction.</summary>
    private sealed class DirectionalShaper
    {
        private readonly TimeProvider _clock;
        private TokenBucket? _download;
        private TokenBucket? _upload;
        private readonly DelayQueue _downloadQueue = new();
        private readonly DelayQueue _uploadQueue = new();

        public DirectionalShaper(TimeProvider clock, ByteRate? download, ByteRate? upload)
        {
            _clock = clock;
            UpdateRates(download, upload);
        }

        public int QueuedCount => _downloadQueue.Count + _uploadQueue.Count;

        public long DroppedPackets => _downloadQueue.DroppedPackets + _uploadQueue.DroppedPackets;

        public void UpdateRates(ByteRate? download, ByteRate? upload)
        {
            _download = Reconcile(_download, download);
            _upload = Reconcile(_upload, upload);
        }

        public ShapingOutcome Evaluate(in ShapingRequest request, long timestamp)
        {
            bool inbound = request.Direction == PacketDirection.Inbound;
            TokenBucket? bucket = inbound ? _download : _upload;

            // Sens non plafonne : FR-001 rend les deux plafonds independants, et l'absence de
            // l'un ne doit rien changer a l'autre.
            if (bucket is null)
            {
                return ShapingOutcome.PassThrough;
            }

            DelayQueue queue = inbound ? _downloadQueue : _uploadQueue;

            // Un paquet ne double jamais ceux qui attendent : le laisser passer casserait
            // l'ordre et provoquerait des retransmissions.
            if (queue.Count == 0 && bucket.TryConsume(request.SizeBytes))
            {
                return ShapingOutcome.Send;
            }

            ShapingAction action = DropPolicy.Decide(
                request.Direction, request.Protocol, queue.IsFullFor(request.SizeBytes));

            if (action == ShapingAction.Drop)
            {
                queue.CountExternalDrop();
                return ShapingOutcome.Drop;
            }

            return queue.TryEnqueue(new PendingPacket(request.Token, request.SizeBytes, timestamp))
                ? ShapingOutcome.Delay
                : ShapingOutcome.Drop;
        }

        public void DrainReady(List<PendingPacket> ready)
        {
            Drain(_downloadQueue, _download, ready);
            Drain(_uploadQueue, _upload, ready);
        }

        public void ReleaseAll(List<PendingPacket> released)
        {
            while (_downloadQueue.TryDequeue(out PendingPacket packet))
            {
                released.Add(packet);
            }

            while (_uploadQueue.TryDequeue(out PendingPacket packet))
            {
                released.Add(packet);
            }
        }

        private static void Drain(DelayQueue queue, TokenBucket? bucket, List<PendingPacket> ready)
        {
            if (bucket is null)
            {
                // Le plafond a disparu en cours de route : les paquets retenus n'ont plus de
                // raison d'attendre, et les garder les perdrait.
                while (queue.TryDequeue(out PendingPacket orphan))
                {
                    ready.Add(orphan);
                }

                return;
            }

            while (queue.TryPeek(out PendingPacket next) && bucket.TryConsume(next.SizeBytes))
            {
                queue.TryDequeue(out PendingPacket packet);
                ready.Add(packet);
            }
        }

        private TokenBucket? Reconcile(TokenBucket? current, ByteRate? rate)
        {
            if (rate is not { } value)
            {
                return null;
            }

            if (current is null)
            {
                return new TokenBucket(value, _clock);
            }

            // Changer le debit a chaud plutot que recreer le seau : recreer remettrait les
            // jetons a la capacite et laisserait passer une rafale a chaque modification.
            current.Rate = value;
            return current;
        }
    }
}
