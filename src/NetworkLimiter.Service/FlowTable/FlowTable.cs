using System.Net;

namespace NetworkLimiter.Service.FlowTable;

/// <summary>
/// Quintuplet identifiant un flux réseau.
/// </summary>
/// <param name="Protocol">Numéro de protocole IP (6 pour TCP, 17 pour UDP).</param>
/// <param name="LocalAddress">Adresse locale.</param>
/// <param name="LocalPort">Port local.</param>
/// <param name="RemoteAddress">Adresse distante.</param>
/// <param name="RemotePort">Port distant.</param>
public readonly record struct FlowKey(
    byte Protocol,
    IPAddress LocalAddress,
    ushort LocalPort,
    IPAddress RemoteAddress,
    ushort RemotePort);

/// <summary>
/// Associe chaque flux réseau au processus qui le possède.
/// </summary>
/// <remarks>
/// <para>
/// Alimentée par la couche <c>FLOW</c> de WinDivert, consultée par la boucle de mise en forme
/// qui travaille sur la couche <c>NETWORK</c>. Cette dernière ne porte aucun identifiant de
/// processus — sa structure ne contient que des indices d'interface — d'où l'existence de
/// cette table.
/// </para>
/// <para>
/// Deux propriétés comptent autant que la correspondance. La table est <b>bornée</b> : elle
/// est alimentée par le trafic, donc par l'extérieur, et sans limite un flux d'établissements
/// suffirait à épuiser la mémoire du service. Et un flux inconnu se traduit par « je ne sais
/// pas », jamais par une supposition : le pipeline réinjecte alors le paquet sans limitation.
/// </para>
/// <para>
/// Cette classe est <b>sûre vis-à-vis des accès concurrents</b>. Elle a longtemps porté la
/// mention inverse — « utilisée depuis la seule boucle d'interception » — qui était vraie à
/// l'écriture et a cessé de l'être le jour où le pipeline a pris deux threads. Personne n'est
/// revenu la corriger, et la couche réseau a consulté la table pendant que la couche flux
/// l'alimentait. Noter une hypothèse ne la protège pas : seul un verrou le fait.
/// </para>
/// </remarks>
public sealed class FlowTable
{
    private readonly Dictionary<FlowKey, FlowEntry> _entries;
    private readonly TimeProvider _clock;
    private readonly int _maxEntries;

    // Trois threads touchent cette table : la boucle des flux l'alimente, la boucle reseau la
    // consulte — en ecrivant, voir TryGetProcessId — et la maintenance la purge.
    private readonly Lock _gate = new();

    /// <summary>Crée une table de flux.</summary>
    /// <param name="clock">Horloge, injectée pour rendre la purge testable sans attente réelle.</param>
    /// <param name="maxEntries">Nombre maximal d'entrées conservées.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> est <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEntries"/> n'est pas positif.</exception>
    public FlowTable(TimeProvider clock, int maxEntries = 4096)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxEntries, 0);

        _clock = clock;
        _maxEntries = maxEntries;
        _entries = new Dictionary<FlowKey, FlowEntry>(maxEntries);
    }

    /// <summary>Nombre de flux connus.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Enregistre un flux établi.</summary>
    public void OnFlowEstablished(FlowKey key, ulong endpointId, uint processId)
    {
        lock (_gate)
        {
            // Les ports sont reutilises : un quintuplet identique peut appartenir a un autre
            // processus quelques instants plus tard. Ecraser est donc le comportement correct —
            // conserver l'ancien attribuerait le trafic a la mauvaise application, et lui
            // appliquerait la mauvaise limite.
            _entries[key] = new FlowEntry(endpointId, processId, _clock.GetTimestamp());

            if (_entries.Count > _maxEntries)
            {
                EvictOldest();
            }
        }
    }

    /// <summary>Retire un flux supprimé.</summary>
    public void OnFlowDeleted(FlowKey key)
    {
        lock (_gate)
        {
            _entries.Remove(key);
        }
    }

    /// <summary>
    /// Cherche le processus propriétaire d'un flux.
    /// </summary>
    /// <returns><c>false</c> si le flux est inconnu ; l'appelant laisse alors passer sans limiter.</returns>
    public bool TryGetProcessId(FlowKey key, out uint processId)
    {
        // Cette methode porte un nom de lecture mais ECRIT : elle rafraichit la date de
        // derniere vue. C'est ce qui la rendait particulierement dangereuse sans verrou — un
        // appelant pouvait raisonnablement la croire sans effet, alors qu'elle mute la table
        // a chaque paquet, depuis la boucle reseau, pendant que la boucle des flux mute aussi.
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out FlowEntry entry))
            {
                processId = 0;
                return false;
            }

            // Un flux long mais actif — telechargement, visioconference — ne doit pas etre
            // purge sous pretexte que son etablissement est ancien.
            _entries[key] = entry with { LastSeenTimestamp = _clock.GetTimestamp() };

            processId = entry.ProcessId;
            return true;
        }
    }

    /// <summary>
    /// Purge les entrées non vues depuis plus longtemps que <paramref name="maxAge"/>.
    /// </summary>
    /// <remarks>
    /// Tous les flux ne produisent pas d'événement de suppression : arrêt brutal d'un
    /// processus, perte d'événement sous charge. Ce balayage est le filet qui empêche la
    /// table de croître indéfiniment.
    /// </remarks>
    /// <returns>Nombre d'entrées retirées.</returns>
    public int PurgeStale(TimeSpan maxAge)
    {
        lock (_gate)
        {
            List<FlowKey>? expired = null;

            foreach ((FlowKey key, FlowEntry entry) in _entries)
            {
                if (_clock.GetElapsedTime(entry.LastSeenTimestamp) > maxAge)
                {
                    (expired ??= []).Add(key);
                }
            }

            if (expired is null)
            {
                return 0;
            }

            foreach (FlowKey key in expired)
            {
                _entries.Remove(key);
            }

            return expired.Count;
        }
    }

    /// <summary>
    /// Vide la table.
    /// </summary>
    /// <remarks>
    /// Appelée à la fermeture des handles : les flux connus n'ont plus de sens une fois
    /// l'interception arrêtée, et les conserver ferait repartir sur un état périmé.
    /// </remarks>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private void EvictOldest()
    {
        FlowKey oldestKey = default;
        long oldestTimestamp = long.MaxValue;

        foreach ((FlowKey key, FlowEntry entry) in _entries)
        {
            if (entry.LastSeenTimestamp < oldestTimestamp)
            {
                oldestTimestamp = entry.LastSeenTimestamp;
                oldestKey = key;
            }
        }

        _entries.Remove(oldestKey);
    }

    private readonly record struct FlowEntry(ulong EndpointId, uint ProcessId, long LastSeenTimestamp);
}
