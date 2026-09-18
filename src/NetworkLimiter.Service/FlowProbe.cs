using System.Globalization;
using System.Runtime.Versioning;
using NetworkLimiter.Service.Interception;

namespace NetworkLimiter.Service;

/// <summary>
/// Écoute la couche <c>FLOW</c> et rend compte de ce qu'elle délivre réellement.
/// </summary>
/// <remarks>
/// <para>
/// Née d'une panne précise : la boucle d'interception tournait, la couche <c>NETWORK</c>
/// recevait des milliers de paquets par seconde, et la couche <c>FLOW</c> n'a produit
/// <b>aucun</b> événement en quatre-vingt-dix secondes. Sans événement de flux, aucun paquet
/// n'est attribuable à un processus, donc aucune règle ne s'applique — et rien ne le signale.
/// </para>
/// <para>
/// La sonde compare plusieurs filtres sur le même handle, dans les mêmes conditions. C'est ce
/// qui distingue « le filtre ne correspond à rien » de « la couche ne délivre rien », deux
/// causes qu'aucune observation extérieure ne sépare.
/// </para>
/// <para>
/// Elle décode aussi les premiers événements reçus. Le champ de bits de
/// <c>WINDIVERT_ADDRESS</c> est déchiffré à la main : cette impression est la seule façon de
/// vérifier qu'il l'est correctement.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class FlowProbe
{
    private const int MaxDecodedSamples = 6;

    /// <summary>Exécute la sonde et rend un code de sortie.</summary>
    public static int Run(string[] args)
    {
        int seconds = args.Length > 1 && int.TryParse(args[1], CultureInfo.InvariantCulture, out int parsed)
            ? Math.Clamp(parsed, 1, 120)
            : 15;

        Console.WriteLine("Sonde de la couche FLOW");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();
        Console.WriteLine($"Chaque filtre est écouté {seconds} s. Produisez du trafic pendant ce temps :");
        Console.WriteLine("ouvrez une page, lancez un téléchargement, n'importe quoi de réseau.");
        Console.WriteLine();

        var driver = new WinDivertDriverService();
        DriverStartResult start = driver.EnsureRunning();

        if (!start.Started)
        {
            Console.Error.WriteLine(start.Diagnostic);
            return 1;
        }

        // Du plus permissif au plus specifique. Si « true » delivre et que le filtre de
        // production ne delivre pas, la cause est le filtre et rien d'autre.
        (string Label, string Filter)[] candidates =
        [
            ("tout (« true »)", "true"),
            ("production", FilterBuilder.FlowFilter),
            ("protocole seul", "tcp or udp"),
            ("famille seule", "ip or ipv6"),
        ];

        int delivered = 0;

        foreach ((string label, string filter) in candidates)
        {
            if (Listen(label, filter, seconds) > 0)
            {
                delivered++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 60));

        if (delivered == 0)
        {
            Console.WriteLine("Aucun filtre n'a délivré d'événement : la couche FLOW est muette.");
            Console.WriteLine("La cause n'est pas le filtre.");
            return 2;
        }

        Console.WriteLine($"{delivered} filtre(s) sur {candidates.Length} ont délivré des événements.");
        return 0;
    }

    private static int Listen(string label, string filter, int seconds)
    {
        Console.WriteLine($"--- Filtre {label} : « {filter} »");

        using var interceptor = new WinDivertInterceptor(
            filter,
            WinDivertLayer.Flow,
            priority: 0,
            WinDivertOpenOptions.Sniff | WinDivertOpenOptions.RecvOnly | WinDivertOpenOptions.NoInstall);

        try
        {
            interceptor.Open();
        }
        catch (Exception exception) when (
            exception is InterceptionUnavailableException or InvalidFilterException)
        {
            Console.WriteLine($"    ouverture refusée : {exception.Message}");
            Console.WriteLine();
            return 0;
        }

        int events = 0;
        int decoded = 0;
        int wrongLayer = 0;

        // La reception est bloquante : elle ne rend la main qu'a la fermeture du handle. Un
        // thread dedie permet de borner l'ecoute sans dependre de l'arrivee d'un evenement,
        // ce qui est exactement le cas a mesurer.
        var reader = new Thread(() =>
        {
            var addresses = new WinDivertAddress[64];

            try
            {
                while (true)
                {
                    int count = interceptor.Receive(Span<byte>.Empty, addresses, out _);

                    if (count == 0)
                    {
                        Console.WriteLine("    réception close (0 événement rendu).");
                        return;
                    }

                    for (int index = 0; index < count; index++)
                    {
                        events++;

                        if (addresses[index].Layer != WinDivertLayer.Flow)
                        {
                            wrongLayer++;
                        }

                        if (decoded < MaxDecodedSamples)
                        {
                            decoded++;
                            Describe(addresses[index]);
                        }
                    }
                }
            }
            catch (InterceptionUnavailableException exception)
            {
                Console.WriteLine($"    réception interrompue : {exception.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "FlowProbe",
        };

        reader.Start();

        // Attente active bornee : la sonde est un outil de diagnostic hors du coeur reseau,
        // elle n'est soumise ni au budget de latence ni a l'interdiction d'attente du
        // principe III, qui vise le code mis en forme et ses tests.
        Thread.Sleep(TimeSpan.FromSeconds(seconds));

        interceptor.Close();
        reader.Join(TimeSpan.FromSeconds(2));

        Console.WriteLine(events == 0
            ? $"    AUCUN événement en {seconds} s."
            : $"    {events} événement(s), dont {wrongLayer} hors couche FLOW.");

        Console.WriteLine();
        return events;
    }

    private static void Describe(in WinDivertAddress address)
    {
        WinDivertFlowData flow = address.AsFlowData();

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"      couche={address.Layer} evt={address.Event} pid={flow.ProcessId} " +
            $"proto={flow.Protocol} local={flow.LocalPort} distant={flow.RemotePort} " +
            $"sortant={address.Outbound} boucle={address.Loopback} ipv6={address.IPv6}"));
    }
}
