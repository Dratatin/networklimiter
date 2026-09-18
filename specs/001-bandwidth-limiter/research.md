# Phase 0 — Recherche : Limiteur de bande passante par application

**Feature**: `001-bandwidth-limiter` | **Date**: 2026-09-18 | **Spec**: [spec.md](./spec.md)

Ce document résout les inconnues techniques du plan. Chaque entrée suit le format
*Décision / Justification / Alternatives écartées*. Les faits vérifiés en ligne portent leur
source ; les valeurs numériques issues de `windivert.h` sont citées telles quelles.

---

## R-001 — Version du runtime .NET

**Décision** : **.NET 10 LTS**, et non .NET 8.

**Justification** : .NET 8 atteint sa fin de support le **10 novembre 2026**, soit moins de deux
mois après la date de ce plan ; il est déjà en phase de maintenance. .NET 10 LTS est supporté
jusqu'au **14 novembre 2028**. Livrer un outil qui s'exécute en `LocalSystem` et manipule la pile
réseau sur un runtime qui cesse de recevoir des correctifs de sécurité avant même la fin du
développement contredit frontalement les exigences de sécurité de la constitution.

**Conséquence** : la constitution mentionnait « .NET 8 (LTS) ». Elle doit être amendée en 1.0.1.
Aucun autre impact : WPF, les services worker et le P/Invoke sont identiques entre les deux
versions ; il n'y a pas de migration à prévoir puisqu'aucun code n'existe.

**Alternatives écartées** :

- *.NET 8* : fin de support imminente (voir ci-dessus).
- *.NET 9* : STS, fin de support le 10 novembre 2026 également. Pire choix encore.
- *.NET Framework 4.8* : supporté très longtemps car composant de Windows, mais pas de worker
  service moderne, pas de `System.Text.Json` performant, outillage de test daté.

---

## R-002 — Matrice de compatibilité concrète (FR-035)

**Décision** :

| Cible | Statut | Vérification |
|-------|--------|--------------|
| Windows 11 21H2 (22000) et ultérieur, **x64** | **Supporté, testé** | CI + VM de recette |
| Windows 10 22H2 (19045), **x64** | **Supporté, testé** | VM de recette |
| Windows 10 antérieur à 22H2 | Non supporté | Bloqué à l'installation |
| **ARM64** (toutes versions) | **Non supporté** | Bloqué à l'installation, message dédié |
| x86 32 bits | Non supporté | Bloqué à l'installation |
| Windows Server | Hors périmètre | Non bloqué, non testé, non supporté |

**Justification du blocage ARM64** : WinDivert ne publie **aucun pilote ARM64**. La demande est
une *issue* ouverte depuis janvier 2025 et les builds officiels ne couvrent que i386 et amd64.
Un pilote noyau ne peut pas s'exécuter sous l'émulation x64 de Windows ARM64 : l'application
en mode utilisateur démarrerait, mais `WinDivertOpen` échouerait systématiquement au chargement
du pilote. Le principe II de la constitution impose alors de déclarer l'architecture non
supportée et d'échouer proprement à l'installation — c'est exactement ce que fait le MSI, par
une condition de lancement et un message nommant la cause.

**Justification du plancher Windows 10 22H2** : WinDivert 2.2 cible Windows 10 et Windows 11.
.NET 10 descend formellement jusqu'à Windows 10 1607, mais les versions de Windows 10
antérieures à 22H2 ne reçoivent plus de mises à jour de sécurité ; les tester reviendrait à
valider l'outil sur un socle vulnérable. Windows 10 22H2 lui-même est en fin de servicing grand
public depuis le 14 octobre 2025 : il est supporté en v1 parce que le parc installé le justifie,
mais il est explicitement candidat à l'abandon dans une version ultérieure.

**Alternatives écartées** :

- *Supporter ARM64 en repli sans limitation* : l'utilisateur installerait un limiteur qui ne
  limite pas. Le principe IV interdit la dégradation silencieuse.
- *Compiler WinDivert pour ARM64 soi-même* : exigerait un certificat EV, l'attestation
  Microsoft, et la maintenance d'un fork de pilote noyau. Sans commune mesure avec le projet.

---

## R-003 — Architecture d'interception WinDivert

**Décision** : **deux handles WinDivert distincts**, ouverts par le service.

1. **Handle FLOW** — couche `WINDIVERT_LAYER_FLOW`, drapeaux `SNIFF | RECV_ONLY`. Il reçoit les
   événements `FLOW_ESTABLISHED` et `FLOW_DELETED`. Sa structure `WINDIVERT_DATA_FLOW` fournit
   `ProcessId`, `LocalAddr`, `RemoteAddr`, `LocalPort`, `RemotePort`, `Protocol` et
   `EndpointId`. Il alimente une **table de flux** (quintuplet → PID).
2. **Handle NETWORK** — couche `WINDIVERT_LAYER_NETWORK`, mode divert. Il reçoit les paquets à
   mettre en forme, les classe via la table de flux, applique le seau à jetons et les réinjecte.

**Justification** : la structure `WINDIVERT_DATA_NETWORK` ne contient que `IfIdx` et `SubIfIdx`
— **aucun identifiant de processus**. La couche NETWORK seule ne permet donc pas d'attribuer un
paquet à une application. Seules les couches FLOW et SOCKET portent `ProcessId`. FLOW est
préférable à SOCKET : elle décrit les flux réellement établis, couvre TCP comme UDP, et émet un
événement de suppression qui permet de purger la table sans fuite mémoire.

**Conséquences de conception** :

- Un paquet dont le flux n'est pas encore connu (course entre les deux handles) est
  **réinjecté immédiatement sans limitation**, jamais retenu ni rejeté. Cohérent avec le
  principe IV : dans le doute, on laisse passer.
- La table de flux est purgée sur `FLOW_DELETED`, plus un balayage périodique de sécurité pour
  les entrées orphelines.

**Alternatives écartées** :

- *`GetExtendedTcpTable` / `GetExtendedUdpTable`* : sondage périodique, coûteux, intrinsèquement
  en retard sur les flux courts, et muet sur les flux UDP sans état stable.
- *Couche SOCKET* : donne le PID mais pas le cycle de vie complet du flux ; impose de recouper
  `CONNECT` et `CLOSE` à la main pour reconstruire ce que FLOW fournit directement.

---

## R-004 — Paramètres de file et risque de perte de paquets

**Valeurs issues de `windivert.h`** (WinDivert 2.2, branche master) :

| Paramètre | Défaut | Minimum | Maximum |
|-----------|--------|---------|---------|
| `QUEUE_LENGTH` | 4096 paquets | 32 | 16384 |
| `QUEUE_TIME` | 2000 ms | 100 ms | 16000 ms |
| `QUEUE_SIZE` | 4 Mo | 64 Ko | 32 Mo |
| `WINDIVERT_BATCH_MAX` | — | — | 255 paquets par appel |

**Décision** : la file du noyau est configurée **au maximum** (16384 / 32 Mo, `QUEUE_TIME` laissé
au défaut de 2000 ms) et traitée comme un **tampon de transit à vider au plus vite**, jamais
comme le tampon de mise en forme. La mise en forme se fait dans une file utilisateur bornée,
propre à chaque règle, avec rejet en queue quand elle est pleine.

**Justification** : WinDivert **rejette** les paquets quand la longueur, la taille ou le temps de
file sont dépassés. Laisser les paquets vieillir dans la file noyau pour réaliser le délai de
mise en forme reviendrait à convertir la limitation en perte de paquets non maîtrisée — un mode
de défaillance opaque, qui dégrade la latence de tout le système et pas seulement de
l'application visée. La lecture se fait donc par lots de 255 paquets (`WinDivertRecvEx`) et la
réinjection par lots (`WinDivertSendEx`).

**Garde-fou (principe IV)** : un indicateur de pression de file est exposé dans l'état de santé.
Si la boucle de drainage ne tient plus la cadence au-delà d'un seuil, le service **ferme le
handle NETWORK** — le trafic redevient non limité — plutôt que de laisser le noyau rejeter des
paquets silencieusement. Mieux vaut ne plus limiter que dégrader la connexion sans le dire.

---

## R-005 — Algorithme de mise en forme

**Décision** : **seau à jetons hiérarchique à deux niveaux**. Un seau par règle et par sens, dont
le parent est le seau global correspondant. Un paquet n'est émis que lorsque les deux seaux ont
assez de jetons. Capacité du seau = `max(2 × MTU, débit × 100 ms)`.

**Justification** : le seau à jetons est l'algorithme le plus simple qui tienne un débit moyen
tout en tolérant une rafale bornée, et il est trivialement testable en pur avec une horloge
injectée, comme l'exige le principe III. La hiérarchie à deux niveaux réalise directement la
règle « le plus restrictif l'emporte » de FR-010 sans code de résolution séparé.

**Limite connue, à ne pas masquer** : en **montant**, le délai appliqué avant réinjection
contrôle le débit directement et précisément. En **descendant**, le paquet est déjà arrivé sur la
machine ; le retenir ne réduit pas la consommation de la ligne à cet instant, il provoque un
signal de congestion qui fait refluer l'émetteur. Conséquences :

- **TCP descendant** : converge vers le plafond en quelques secondes, tolérance de 10 % tenable.
- **UDP descendant** : aucune boucle de rétroaction. Seul le rejet permet de tenir un plafond,
  au prix d'une perte visible pour l'application.

**Résolu le 18/09/2026** : la spécification a été qualifiée. FR-003 conserve 10 % d'écart pour
le montant tous protocoles et pour le descendant à contrôle de congestion ; **FR-003a** fixe
20 % pour le descendant sans contrôle de congestion, tenu par rejet ; **FR-003b** impose de
signaler le rejet et sa proportion dans l'interface ; **FR-003c** interdit le rejet lorsque la
temporisation suffit. SC-002 et SC-002a suivent.

Conséquence de conception : le rejet est un **chemin explicite et compté**, jamais un effet de
bord. Le compteur de rejets remonte jusqu'à l'interface via `MetricsTick.droppedPackets`, et
FR-003c se traduit par un invariant testable — pour un flux temporisable, le compteur de rejets
reste à zéro quel que soit le plafond.

**Alternatives écartées** :

- *Seau percé (leaky bucket)* : lisse parfaitement mais interdit toute rafale ; pénalise les
  transferts courts et rend l'outil désagréable sur un usage interactif.
- *Files hiérarchiques type HTB avec emprunt entre classes* : permettrait un partage équitable
  sous plafond global, mais la spec exclut explicitement toute garantie de répartition en v1.
  Complexité non justifiée (principe V).

---

## R-006 — Distinction internet / réseau local (FR-040)

**Décision** : classification **par adresse distante**, appliquée dans le filtre WinDivert
lui-même quand c'est possible, et revérifiée en mode utilisateur. Sont **exclus** des plafonds :

- boucle locale : `127.0.0.0/8`, `::1`, plus le bit `Loopback` de `WINDIVERT_ADDRESS` ;
- plages privées : `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `fc00::/7` ;
- lien-local : `169.254.0.0/16`, `fe80::/10` ;
- multicast et diffusion : `224.0.0.0/4`, `255.255.255.255`, `ff00::/8`.

**Justification** : la classification porte sur l'**adresse de destination finale**, pas sur la
route empruntée. Le cas limite « passerelle internet en plage privée » relevé dans la spec se
résout donc de lui-même : une requête vers un serveur public porte une adresse publique en
destination, même si le premier saut est un routeur en `192.168.x.x`. Le bit `Loopback` fourni
par WinDivert évite d'avoir à traiter la boucle locale par plages d'adresses.

**Cas non résolu, documenté** : sous **VPN**, la limitation s'applique aux paquets tels que
WinDivert les voit. Selon que le client VPN chiffre au-dessus ou au-dessous du point
d'interception, l'outil verra soit le trafic en clair vers des adresses publiques, soit le seul
tunnel chiffré vers l'adresse du concentrateur. Dans le second cas, la distinction
internet/local devient inopérante et la ventilation par application aussi. FR-037 et FR-040c
sont donc satisfaits par la **détection et l'affichage**, non par la garantie : le service
détecte la présence d'un adaptateur de type VPN et affiche un avertissement explicite dans
l'état de santé.

---

## R-007 — Identité d'une application (FR-039)

**Décision** : identifiant primaire = **chemin complet normalisé** de l'exécutable ; identifiant
de repli = **nom de fichier** de l'exécutable. Résolution `ProcessId` → chemin via
`QueryFullProcessImageName`. Clé de cache = couple **(PID, heure de démarrage du processus)**.

**Justification** : `LocalSystem` dispose du droit `PROCESS_QUERY_LIMITED_INFORMATION` sur les
processus des autres sessions, y compris protégés en lecture de chemin. Le couple avec l'heure de
démarrage est indispensable : les PID sont réutilisés par Windows, et une entrée de cache périmée
attribuerait le trafic d'une application à une autre — donc appliquerait une limite à la mauvaise
application, exactement le genre de défaillance silencieuse que le principe VI cherche à exclure.

**Normalisation** : résolution des chemins `\Device\HarddiskVolumeN\...` en lettre de lecteur,
résolution des liens symboliques et des jonctions, comparaison en casse invariante. Sans cela,
deux écritures du même chemin produiraient deux règles distinctes.

**Applications packagées (FR-038)** : **supportées**. Elles possèdent un PID et un chemin réel
sous `%ProgramFiles%\WindowsApps`, lisible par `LocalSystem`. Limite assumée en v1 : le nom
affiché est celui de l'exécutable, pas le nom convivial du paquet — la lecture du manifeste de
paquet est reportée. L'interface affiche donc le vrai chemin pour lever toute ambiguïté.

---

## R-008 — Canal IPC et contrôle d'accès

**Décision** : `NamedPipeServerStream` sur `\\.\pipe\NetworkLimiter.v1`, messages JSON préfixés
de leur longueur, sérialisation par **générateur de source** `System.Text.Json`.

**Contrôle d'accès, en trois couches** :

1. **ACL du tuyau** : lecture/écriture accordée à `BUILTIN\Users`, plus une **ACE de refus
   explicite sur `NT AUTHORITY\NETWORK` (S-1-5-2)** et sur `ANONYMOUS LOGON`.
2. **Autorisation par message** : le service impersonne l'appelant (`RunAsClient`) et vérifie
   `WindowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator)` pour toute commande de
   modification. Les commandes de lecture ne l'exigent pas.
3. **Validation du contenu** : discriminant de type explicite, liste blanche de types de
   messages, bornes vérifiées sur chaque champ numérique, longueur maximale de message de
   64 Ko, refus de la désérialisation polymorphe.

**Justification de l'ACE de refus réseau** : un tuyau nommé est **joignable à distance** via SMB
(`\\machine\pipe\nom`). Sans refus explicite du SID `NETWORK`, le canal de contrôle d'un outil
privilégié serait exposé au réseau — ce que FR-031 et le principe I interdisent. C'est le
détail qui transforme une conception correcte en conception sûre, et il ne s'obtient pas par
défaut.

**Justification du contrôle par impersonation** : sous UAC, le jeton filtré d'un administrateur
non élevé **ne porte pas** le SID Administrateurs activé. `IsInRole` renvoie donc `false` pour
une interface non élevée et `true` pour une interface élevée, sans code supplémentaire. Le
comportement voulu par FR-034 découle directement du modèle de sécurité de Windows.

**Alternatives écartées** :

- *Socket TCP sur la boucle locale* : joignable par tout processus local sans contrôle d'accès
  possible, et interdit par la constitution.
- *Jeton partagé dans un fichier* : déplace le problème vers les ACL du fichier, sans gain.
- *gRPC sur tuyau nommé* : apporte un générateur de code et des dépendances pour un protocole de
  douze messages. Complexité non justifiée (principe V).

---

## R-009 — Élévation depuis une interface non élevée (FR-034a à FR-034c)

**Décision** : l'interface démarre **non élevée et en lecture seule**. Toute action de
modification déclenche `ShellExecute` avec le verbe `runas` sur **le même exécutable**, muni d'un
commutateur `--elevated`. L'instance élevée reprend la main et devient la fenêtre d'édition ;
l'instance non élevée se referme.

**Justification** : un processus ne peut pas acquérir de privilèges après son lancement — seul un
nouveau processus peut naître élevé. Le modèle « une invite UAC par session d'édition » satisfait
FR-034c sans que le service ait à mémoriser une autorisation. C'est le schéma employé par le
Gestionnaire des tâches et Process Explorer, donc familier aux utilisateurs.

**Alternative écartée** : faire mémoriser au service qu'un appelant a été autorisé une fois.
Toute connexion ultérieure, même non élevée, hériterait alors de ce droit. Faille d'élévation
de privilèges par conception.

---

## R-010 — Persistance et intégrité de la configuration

**Décision** : fichier unique `%ProgramData%\NetworkLimiter\config.json`, champ `schemaVersion`,
écriture atomique (fichier temporaire puis `File.Replace`). ACL : `SYSTEM` et
`BUILTIN\Administrators` en contrôle total, `BUILTIN\Users` en **lecture seule**.

**Justification** : les ACL sont ici un **contrôle de sécurité**, pas une commodité. Si un
utilisateur standard pouvait écrire ce fichier, il lèverait ses propres limites en éditant du
JSON, et FR-034 ne serait qu'un verrou d'interface. Elles sont aussi le mécanisme testé par
SC-013. L'écriture atomique traite le cas de la coupure de courant en pleine sauvegarde.

**Traitement de la corruption (FR-022)** : fichier illisible ou `schemaVersion` inconnue →
renommage en `config.corrupt.<horodatage>.json`, démarrage **sans aucune limite**, état de santé
en avertissement, restauration proposée dans l'interface. Jamais de réparation silencieuse.

---

## R-011 — Installation du pilote et désinstallation (FR-027)

**Décision** : le pilote est enregistré **par le MSI, à l'installation**, et le service ouvre ses
handles avec le drapeau `WINDIVERT_FLAG_NO_INSTALL`. Installeur : **WiX v5**.

**Justification** : `WinDivertOpen` installe sinon le pilote silencieusement à la première
ouverture, ce qui déplace une modification du système vers un chemin d'exécution non audité.
L'installer explicitement à un moment où l'utilisateur a consenti à une élévation rend
l'opération visible, journalisable et surtout **réversible par la désinstallation** — condition
de FR-027 et de SC-010.

**Lacune corrigée le 18/09/2026 — qui démarre le pilote ?** Cette entrée prescrivait
d'enregistrer le pilote à l'installation et d'ouvrir avec `NO_INSTALL`, sans dire qui le
**charge**. Un service noyau déclaré « à la demande » ne se charge <b>pas</b> à l'ouverture d'un
périphérique : il faut appeler `StartService`. Sans `NO_INSTALL`, `WinDivertOpen` s'en charge
lui-même ; avec, personne ne le faisait, et l'ouverture échouait sur un
`ERROR_SERVICE_DOES_NOT_EXIST` trompeur alors que le service était bel et bien enregistré.

C'est désormais la responsabilité du service, via `WinDivertDriverService.EnsureRunning()`. Le
pilote n'est délibérément **pas arrêté** à l'extinction : un autre outil peut utiliser WinDivert
sur la même machine — clumsy, GoodbyeDPI — et l'arrêter lui couperait le réseau. Un pilote chargé
sans handle ouvert ne détourne rien ; le laisser est sans conséquence, l'arrêter peut en avoir.

**Chaîne native validée de bout en bout sur machine réelle le 18/09/2026** — Windows 11
build 26200 x64, via `NetworkLimiter.Service.exe --diagnose` :

| Étape | Résultat |
|-------|----------|
| Chargement du pilote | Passe à `RUNNING` ; ni Secure Boot ni l'intégrité de la mémoire ne le bloquent |
| Ouverture du handle `FLOW` | Réussie |
| Ouverture du handle `NETWORK` | Réussie |
| Fermeture des handles | Propre, aucun trafic détourné après coup |

Ce résultat valide d'un coup ce qu'aucun test unitaire ne pouvait atteindre : les liaisons
P/Invoke, les chaînes de filtre, les drapeaux d'ouverture, et surtout l'acceptation du pilote
par Windows. C'était la seule hypothèse majeure du projet qui restait à vérifier.

**Séquence de désinstallation, dans cet ordre** : arrêt du service → fermeture des handles (le
trafic redevient non limité) → arrêt et suppression du service pilote → suppression des fichiers
→ suppression de `%ProgramData%\NetworkLimiter`. Chaque étape est vérifiée par le test de
désinstallation.

**Version de WiX : rester en v5.** Constaté le 18/09/2026 en installant l'outillage : **WiX v6
et v7 exigent l'acceptation de la licence « Open Source Maintenance Fee »**, un modèle de
contribution financière annuelle. `wix build` refuse de s'exécuter tant qu'elle n'est pas
acceptée. WiX 5.0.2 est la dernière version sans cette contrainte et suffit intégralement à nos
besoins. Une montée en version majeure de WiX est donc une **décision de licence**, pas une mise
à jour de routine, et ne doit pas être faite sans arbitrage explicite.

**Enregistrement du pilote : par registre, pas par `ServiceInstall`.** Windows Installer ne
supporte pas le type `kernelDriver` — WiX refuse la construction. Le service pilote est donc
déclaré par entrées de registre (`Type=1`, `Start=3`, `ImagePath`). Cette forme a trois
avantages sur une action personnalisée appelant `sc.exe` : elle est **transactionnelle** (MSI
annule tout en cas d'échec), elle est **retirée à la désinstallation sans code à écrire**
(SC-010), et elle **n'ajoute aucun script privilégié à auditer** — ce qui compte pour un produit
dont la constitution impose de minimiser le code privilégié.

**Licence** : WinDivert est sous **double licence LGPL v3 ou GPL v2**. La liaison dynamique avec
la LGPL v3 convient à une distribution binaire, à condition de redistribuer le texte de licence
et de permettre le remplacement de la bibliothèque. Un passage à une distribution commerciale en
source fermée imposerait la licence commerciale de WinDivert : à vérifier **avant**, pas après.

---

## R-012 — Méthode de mesure de débit (FR-003, SC-002, SC-005)

**Décision** : banc de mesure à **deux machines virtuelles** reliées par un commutateur virtuel
interne, la machine distante portant une adresse de la plage **`203.0.113.0/24` (TEST-NET-3)**.

**Justification** : c'est le point subtil du dispositif. Le classificateur de R-006 exclut les
plages privées des plafonds ; un banc monté en `192.168.x.x` mesurerait donc un trafic que
l'outil a par conception décidé de ne pas limiter, et tous les tests passeraient en ne prouvant
rien. Une plage documentaire publique est vue comme « internet » par le classificateur tout en
restant confinée à un lien de laboratoire — reproductible, hors ligne, sans dépendance à un
serveur tiers.

**Protocole de mesure** :

1. Transfert continu pendant 30 s, les 5 premières secondes écartées (convergence de TCP).
2. Débit relevé par fenêtre glissante de 10 s, conformément à FR-003.
3. Assertion : moyenne dans les 10 % du plafond ; p95 des fenêtres consigné pour suivi.
4. Matrice : {montant, descendant} × {TCP, UDP} × {100 Ko/s, 1 Mo/s, 10 Mo/s, 100 Mo/s}.
5. Référence indépendante pour SC-005 : compteurs par processus du fournisseur ETW
   **`Microsoft-Windows-Kernel-Network`**, Windows n'exposant pas de compteur de performance
   d'octets par processus.

**Alternative écartée** : mesurer sur la boucle locale. Impossible par construction — la boucle
locale est exclue de la limitation.

### Première mesure de bout en bout — 18/09/2026

Hors banc, sur la machine de développement (Windows 11 build 26200 x64), contre
`speed.cloudflare.com`. Ce n'est pas le protocole ci-dessus et cela ne le remplace pas : une
mesure unique, sur un lien internet réel, sans fenêtre glissante ni écartement de la phase de
convergence. Elle est consignée parce qu'elle établit un fait qui manquait — **la chaîne
complète limite réellement du trafic**.

| Grandeur | Valeur |
|---|---|
| Référence sans service | 4,40 Mo/s |
| Plafond posé sur `curl.exe` | 200 Ko/s en descente, montée illimitée |
| Débit mesuré par `curl` | **190,8 Ko/s** |
| Écart au plafond | −4,6 % (tolérance FR-003 : 10 %) |

Les compteurs d'étape confirment le mécanisme, et pas seulement le résultat :

- `sans-regle + regle-trouvee = paquets` à chaque seconde — aucun paquet n'échappe au comptage ;
- `passe + retarde = paquets` — aucun paquet n'est perdu en route ;
- **zéro rejet**, conforme à FR-003c : le trafic TCP est temporisable, il ne doit jamais être
  rejeté ;
- sur ~219 paquets de `curl` par seconde, 146 retardés et 73 laissés passer — ces derniers sont
  ses ACK sortants, que la règle ne plafonne pas.

**Ce que cette mesure ne prouve pas**, et qui reste au banc de R-012 : la limitation en montée,
le chemin de rejet (UDP en descente, FR-003a), le plafond global (US3), la non-affectation du
trafic local (SC-014), et le comportement aux plafonds extrêmes de la matrice.

---

## Synthèse des points à arbitrer par le porteur du projet

| # | Point | Décision | Statut |
|---|-------|----------|--------|
| 1 | .NET 8 en fin de support le 10/11/2026 | Passage à .NET 10 LTS | **Tranché** — constitution amendée en 1.0.1 |
| 2 | FR-003 : 10 % d'écart inatteignable en UDP descendant | Exigence qualifiée par sens et protocole | **Tranché** — FR-003 à FR-003c, SC-002 et SC-002a |
| 3 | Sous VPN, la ventilation par application peut devenir inopérante | Détecter et avertir, ne pas garantir | **Tranché** — FR-037 et FR-040c satisfaits par la détection |
| 4 | WinDivert en LGPL/GPL | Liaison dynamique, texte redistribué | **Tranché pour le gratuit** — licence commerciale à prendre avant toute vente en source fermée |

Sources : [WinDivert 2.2 Documentation](https://reqrypt.org/windivert-doc.html) ·
[basil00/WinDivert — issue ARM64 #379](https://github.com/basil00/WinDivert/issues/379) ·
[windivert.h](https://github.com/basil00/WinDivert/blob/master/include/windivert.h) ·
[Politique de support .NET](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) ·
[.NET 10 — systèmes supportés](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
