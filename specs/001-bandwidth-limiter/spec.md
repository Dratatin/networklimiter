# Feature Specification: Limiteur de bande passante par application

**Feature Branch**: `001-bandwidth-limiter`

**Created**: 2026-09-18

**Status**: Draft

**Input**: User description: "Application Windows de bureau permettant de limiter la bande passante des applications Windows. Périmètre v1 : limite de débit descendant ET montant par application, limite globale applicable à toutes les applications, monitoring temps réel du débit par application, profils et règles persistantes qui survivent au redémarrage. Architecture : service Windows privilégié + interface utilisateur non élevée. Priorités : compatibilité, sécurité, testabilité. Simple et intuitif. Hors périmètre v1 : planification horaire, blocage total."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Limiter une application qui monopolise la connexion (Priority: P1)

L'utilisateur constate que sa connexion est saturée. Il ouvre l'application, repère dans la
liste le programme fautif (un client de synchronisation cloud, un téléchargeur de jeux, un
navigateur), saisit un plafond en descente et/ou en montée, et valide. Le débit du programme
retombe sous le plafond en quelques secondes, sans redémarrer ce programme ni couper sa
connexion en cours.

**Why this priority**: c'est la raison d'être du produit. Sans cette capacité, rien d'autre
n'a de valeur. Elle constitue à elle seule un MVP livrable.

**Independent Test**: lancer un téléchargement volumineux, appliquer une limite de 1 Mo/s,
mesurer le débit réel du processus pendant 60 secondes et vérifier qu'il reste dans la
tolérance annoncée, puis retirer la limite et vérifier le retour au débit nominal.

**Acceptance Scenarios**:

1. **Given** une application télécharge à plein débit, **When** l'utilisateur lui applique une
   limite en descente de 1 Mo/s, **Then** le débit mesuré de cette application redescend sous
   1 Mo/s en moins de 5 secondes et les transferts en cours ne sont pas interrompus.
2. **Given** une application est limitée, **When** l'utilisateur modifie le plafond, **Then**
   la nouvelle valeur s'applique en moins de 5 secondes sans action supplémentaire.
3. **Given** une application est limitée, **When** l'utilisateur désactive ou supprime la règle,
   **Then** l'application retrouve le débit non limité en moins de 5 secondes.
4. **Given** une règle est appliquée à une application, **When** une autre application
   télécharge simultanément, **Then** le débit de cette autre application n'est pas affecté.
5. **Given** une limite en descente est définie sans limite en montée, **When** l'application
   envoie des données, **Then** son débit montant reste non limité.

---

### User Story 2 - Voir en temps réel qui consomme la bande passante (Priority: P2)

L'utilisateur ouvre l'application et voit immédiatement la liste des programmes qui utilisent
le réseau maintenant, avec leur débit descendant et montant instantané, triés par consommation
décroissante. Il identifie le coupable sans avoir à ouvrir le gestionnaire des tâches ou à
deviner.

**Why this priority**: sans visibilité, l'utilisateur ne sait pas quoi limiter. C'est ce qui
rend l'action de la US1 possible en moins de trois interactions.

**Independent Test**: lancer deux transferts connus simultanément, vérifier que les deux
programmes apparaissent avec des débits cohérents avec la mesure d'un outil système de
référence, à l'écart de tolérance près.

**Acceptance Scenarios**:

1. **Given** plusieurs programmes utilisent le réseau, **When** l'utilisateur ouvre la vue
   principale, **Then** chaque programme actif est listé avec son nom lisible, son icône, son
   débit descendant et son débit montant instantanés.
2. **Given** la vue est ouverte, **When** le débit d'un programme change, **Then** l'affichage
   se met à jour au moins une fois par seconde.
3. **Given** un programme cesse toute activité réseau, **When** un délai court s'écoule,
   **Then** il disparaît de la liste des actifs sans faire disparaître sa règle éventuelle.
4. **Given** un programme est limité, **When** l'utilisateur regarde sa ligne, **Then** le
   plafond appliqué est visible à côté du débit mesuré.
5. **Given** un programme n'est pas encore en cours d'exécution mais possède une règle,
   **When** l'utilisateur consulte la liste des règles, **Then** il le voit avec un état
   « en attente » explicite.

---

### User Story 3 - Plafonner la consommation totale de la machine (Priority: P2)

L'utilisateur partage sa connexion avec d'autres personnes du foyer, ou travaille en partage de
connexion mobile. Il définit un plafond global unique qui s'applique à l'ensemble du trafic de
la machine, sans avoir à créer une règle par programme.

**Why this priority**: demandé explicitement et complémentaire de la US1. C'est le seul moyen
simple de garantir qu'une application inconnue ou nouvellement installée ne sature pas la
connexion.

**Independent Test**: définir un plafond global de 5 Mo/s, lancer trois transferts simultanés
dans trois programmes différents, vérifier que la somme des débits reste sous le plafond.

**Acceptance Scenarios**:

1. **Given** un plafond global est défini, **When** plusieurs programmes transfèrent
   simultanément, **Then** la somme de leurs débits reste sous le plafond global.
2. **Given** un plafond global et une règle par application coexistent, **When** les deux
   s'appliquent au même programme, **Then** le plafond effectif de ce programme est le plus
   restrictif des deux, et cette règle de résolution est affichée à l'utilisateur.
3. **Given** un plafond global est actif, **When** un programme sans règle propre démarre,
   **Then** il est soumis au plafond global sans configuration supplémentaire.
4. **Given** un plafond global est défini, **When** un seul programme transfère, **Then** ce
   programme peut consommer jusqu'à la totalité du plafond global.
5. **Given** un plafond global est actif, **When** l'utilisateur le désactive, **Then** seules
   les règles par application restent en vigueur.

---

### User Story 4 - Retrouver ses réglages après un redémarrage et basculer entre profils (Priority: P3)

L'utilisateur a configuré ses limites une fois. Elles s'appliquent toujours après un
redémarrage de la machine, sans qu'il ait à ouvrir la fenêtre. Il regroupe ses réglages en
profils nommés (par exemple « Télétravail », « Soirée », « Partage de connexion ») et bascule
de l'un à l'autre en une action.

**Why this priority**: transforme un outil de dépannage ponctuel en outil durable. Utile mais
non indispensable au premier usage.

**Independent Test**: créer deux profils avec des plafonds différents, redémarrer la machine,
vérifier que le profil actif au moment de l'arrêt est bien celui appliqué au démarrage, puis
basculer sur l'autre et vérifier que les débits changent en conséquence.

**Acceptance Scenarios**:

1. **Given** des règles sont définies, **When** la machine redémarre, **Then** les mêmes
   règles sont appliquées automatiquement, avant même que l'utilisateur ouvre la fenêtre.
2. **Given** plusieurs profils existent, **When** l'utilisateur en active un, **Then** les
   règles du profil précédent sont retirées et celles du nouveau appliquées en moins de
   5 secondes.
3. **Given** un profil est actif, **When** l'utilisateur modifie une règle, **Then** la
   modification est enregistrée dans ce profil et persiste après redémarrage.
4. **Given** l'utilisateur ferme la fenêtre de l'application, **When** il lance un
   téléchargement, **Then** les limites restent appliquées.
5. **Given** le fichier de configuration est corrompu ou illisible, **When** le système
   démarre, **Then** aucune limite n'est appliquée, l'utilisateur en est informé de façon
   explicite, et la configuration précédente valide est proposée en restauration.

---

### User Story 5 - Reprendre la main immédiatement en cas de problème (Priority: P3)

L'utilisateur soupçonne que l'outil perturbe sa connexion (visioconférence qui saute, jeu qui
lague, VPN professionnel instable). Il doit pouvoir tout suspendre en une action, comprendre
l'état réel du système, et désinstaller sans laisser de trace.

**Why this priority**: condition de confiance. Un utilisateur qui craint de perdre sa connexion
n'installera pas l'outil. Indispensable avant toute diffusion, mais pas avant la première démo.

**Independent Test**: activer plusieurs limites, déclencher la suspension globale, vérifier que
tous les débits reviennent au nominal en moins de 5 secondes ; puis arrêter brutalement le
composant privilégié et vérifier le même retour au nominal.

**Acceptance Scenarios**:

1. **Given** des limites sont actives, **When** l'utilisateur déclenche la suspension globale,
   **Then** tout le trafic redevient non limité en moins de 5 secondes et les règles sont
   conservées pour une réactivation ultérieure.
2. **Given** le composant privilégié s'arrête de façon inattendue, **When** l'utilisateur
   utilise le réseau, **Then** le trafic est non limité et l'interface signale explicitement
   que la limitation est inactive.
3. **Given** l'interface est ouverte, **When** l'utilisateur consulte l'état de santé,
   **Then** il voit si la limitation est opérationnelle, le nombre de règles actives, et pour
   chaque règle inactive la raison de son inactivité.
4. **Given** l'application est installée avec des règles actives, **When** l'utilisateur la
   désinstalle, **Then** toutes les limites sont levées, aucun composant système modifié ne
   subsiste, et le réseau fonctionne comme avant l'installation.
5. **Given** l'environnement rend la limitation impossible (composant requis non chargeable),
   **When** l'utilisateur lance l'application, **Then** un message explique la cause et
   l'action corrective, et aucune limite n'est appliquée silencieusement.

---

### Edge Cases

- **Application multi-processus** : un navigateur ou un client de jeu répartit son trafic sur
  plusieurs processus enfants. Une règle portant sur l'application doit couvrir l'ensemble de
  ces processus, sinon le plafond annoncé est faux.
- **Redémarrage d'un programme** : le programme est relancé avec un identifiant de processus
  différent ; la règle doit se réappliquer automatiquement, sans intervention.
- **Mise à jour d'un programme** : l'exécutable est remplacé à un chemin différent (dossiers
  versionnés type `app-1.2.3`). La règle doit continuer de s'appliquer via le repli sur le nom
  de fichier, le signaler, et proposer le remappage vers le nouveau chemin.
- **Deux exécutables de même nom** : deux programmes distincts nommés `updater.exe` dans des
  dossiers différents. Une règle visant l'un ne doit pas limiter l'autre silencieusement ; la
  liste des processus couverts doit rendre la situation visible.
- **Applications du Microsoft Store / packagées** : leur identité et leur chemin diffèrent des
  applications classiques ; elles doivent être soit correctement gérées, soit explicitement
  signalées comme non prises en charge.
- **VPN et adaptateurs virtuels** : avec un VPN actif, ou en présence d'adaptateurs Hyper-V,
  WSL ou Docker, la limitation ne doit ni dupliquer son effet ni cesser silencieusement de
  s'appliquer.
- **Changement de réseau à chaud** : passage Wi-Fi → Ethernet, veille/reprise, connexion d'un
  partage de connexion mobile : les règles doivent rester valides sans redémarrage.
- **IPv6** : le trafic IPv6 doit être limité au même titre que l'IPv4, sinon un plafond peut
  être contourné sans que l'utilisateur le sache.
- **Trafic local et boucle locale** : une copie vers un NAS ou une sauvegarde locale ne doit
  jamais être bridée par un plafond destiné à protéger la connexion internet. Le débit affiché
  doit rester cohérent avec cette exclusion, sans quoi l'utilisateur croira le plafond ignoré.
- **Réseau d'entreprise en plage privée** : une machine dont la passerelle internet passe par
  une plage d'adresses privées ne doit pas voir tout son trafic exclu des plafonds.
- **Plafond très bas** : une valeur inférieure à la taille d'un paquet réseau doit être soit
  refusée à la saisie avec un minimum documenté, soit tenir le débit annoncé sans rompre les
  connexions.
- **Flux descendant sans contrôle de congestion** : jeu en ligne, visioconférence, flux vidéo en
  UDP. Le plafond ne peut y être tenu que par rejet (FR-003a) ; l'utilisateur doit comprendre
  que la limite se paie en pertes, sans quoi il conclura que l'outil dégrade son réseau au
  hasard.
- **Plafond très élevé** : une valeur supérieure au débit réel de la connexion ne doit produire
  aucun surcoût ni dégradation mesurable.
- **Trafic système** : mises à jour Windows, résolution DNS, services système — leur soumission
  au plafond global doit être un choix conscient et documenté, pas un effet de bord.
- **Sessions multiples** : plusieurs utilisateurs ouverts simultanément sur la machine
  (changement rapide d'utilisateur, bureau à distance). Les règles s'appliquent à la machine,
  pas à une session ; un second utilisateur voit les mêmes limites et le même monitoring.
- **Utilisateur sans droits administrateur** : il doit pouvoir tout consulter et comprendre
  pourquoi il ne peut rien modifier, sans jamais se retrouver devant une action qui échoue
  après coup.
- **Élévation refusée** : l'utilisateur annule l'invite UAC au milieu d'une modification.
  L'état appliqué doit rester celui d'avant la tentative, sans configuration à moitié écrite.
- **Deux règles concurrentes** : une règle par application, un plafond global, et plusieurs
  profils — la résolution doit être déterministe et explicable en une phrase à l'utilisateur.
- **Interface et composant privilégié désynchronisés** : après une mise à jour partielle, les
  deux versions ne correspondent plus ; le système doit le détecter et le dire.
- **Ressources contraintes** : plusieurs centaines de connexions simultanées et plusieurs
  dizaines de règles actives sans dégradation perceptible de la latence.

## Requirements *(mandatory)*

### Functional Requirements

**Limitation par application**

- **FR-001**: Le système MUST permettre de définir, pour une application, un plafond de débit
  descendant et un plafond de débit montant, indépendants l'un de l'autre, chacun pouvant être
  laissé illimité.
- **FR-002**: Le système MUST appliquer une règle nouvellement créée ou modifiée en moins de
  5 secondes, sans redémarrer l'application cible ni interrompre ses connexions établies.
- **FR-003**: Le système MUST respecter un plafond avec un écart mesuré n'excédant pas 10 % du
  plafond, moyenné sur une fenêtre de 10 secondes, pour le trafic montant tous protocoles
  confondus et pour le trafic descendant des protocoles disposant d'un contrôle de congestion.
- **FR-003a**: Pour le trafic descendant sans contrôle de congestion, le système MUST tenir le
  plafond avec un écart n'excédant pas 20 %, moyenné sur la même fenêtre, en rejetant les
  données excédentaires.
- **FR-003b**: Lorsqu'un plafond est tenu par rejet de données, le système MUST le signaler
  explicitement dans l'interface, à côté de la règle concernée, et MUST afficher la proportion
  de données rejetées.
- **FR-003c**: Le système MUST NOT rejeter de données pour tenir un plafond lorsque le débit
  peut être contenu par temporisation. Le rejet est un dernier recours, jamais le mécanisme par
  défaut.
- **FR-004**: Le système MUST réappliquer automatiquement une règle lorsque l'application
  concernée est relancée, y compris après un redémarrage de la machine.
- **FR-005**: Le système MUST couvrir l'ensemble des processus appartenant à une même
  application lorsqu'une règle lui est appliquée, et MUST indiquer à l'utilisateur combien de
  processus sont couverts.
- **FR-006**: Le système MUST permettre d'activer ou de désactiver une règle sans la supprimer.
- **FR-007**: Le système MUST refuser une valeur de plafond hors des bornes supportées et MUST
  indiquer les bornes à la saisie plutôt qu'après validation.
- **FR-008**: Le système MUST limiter le trafic IPv4 et IPv6 avec le même plafond, ou MUST
  déclarer explicitement à l'utilisateur toute famille d'adresses non couverte.

**Limitation globale**

- **FR-009**: Le système MUST permettre de définir un plafond global descendant et un plafond
  global montant s'appliquant à l'ensemble du trafic de la machine.
- **FR-010**: Lorsqu'un plafond global et une règle par application s'appliquent au même
  trafic, le système MUST retenir la contrainte la plus restrictive et MUST afficher le plafond
  effectif résultant.
- **FR-011**: Le système MUST permettre d'exempter explicitement une application du plafond
  global.
- **FR-012**: Le système MUST permettre d'activer et de désactiver le plafond global
  indépendamment des règles par application.

**Monitoring**

- **FR-013**: Le système MUST afficher, pour chaque application ayant une activité réseau, son
  débit descendant et montant instantanés, rafraîchis au moins une fois par seconde.
- **FR-014**: Le système MUST afficher le débit descendant et montant total de la machine.
- **FR-015**: Le système MUST identifier chaque application par un nom lisible et son icône,
  et MUST rendre son chemin complet consultable.
- **FR-016**: Le système MUST permettre de créer une règle directement depuis la ligne d'une
  application affichée dans le monitoring, en une action.
- **FR-017**: Le système MUST conserver et afficher un historique court du débit (au moins les
  60 dernières secondes) pour permettre de distinguer un pic d'une saturation durable.
- **FR-018**: Le système MUST NOT enregistrer ni afficher les adresses distantes, noms d'hôtes,
  URL ou contenus échangés.

**Profils et persistance**

- **FR-019**: Le système MUST conserver règles, plafonds globaux et profils au travers des
  redémarrages de la machine.
- **FR-020**: Le système MUST appliquer les règles du profil actif au démarrage de la machine,
  sans que l'utilisateur ait à ouvrir l'interface.
- **FR-021**: Le système MUST permettre de créer, renommer, dupliquer et supprimer des profils,
  et d'en activer un seul à la fois.
- **FR-022**: Le système MUST détecter une configuration persistée illisible ou corrompue, MUST
  démarrer sans appliquer de limite dans ce cas, et MUST en informer l'utilisateur.
- **FR-023**: Le système MUST permettre d'exporter et d'importer la configuration afin de la
  reporter sur une autre machine.

**Sûreté et reprise en main**

- **FR-024**: Le système MUST offrir une suspension globale immédiate levant toutes les limites
  en moins de 5 secondes tout en conservant les règles.
- **FR-025**: Le système MUST rendre le trafic non limité si le composant chargé de la
  limitation s'arrête, tombe en panne ou ne peut pas démarrer. Aucun état ne MUST laisser le
  trafic limité ou bloqué sans composant actif pour le libérer.
- **FR-026**: Le système MUST afficher un état de santé indiquant si la limitation est
  opérationnelle, le nombre de règles actives et, pour chaque règle inactive, la raison.
- **FR-027**: Le système MUST se désinstaller sans laisser de limite active, de composant
  système modifié, ni de configuration résiduelle.
- **FR-028**: Le système MUST NOT bloquer totalement le trafic d'une application ; le périmètre
  se limite à la limitation de débit.

**Sécurité et privilèges**

- **FR-029**: L'interface utilisateur MUST fonctionner sans élévation de privilèges et MUST NOT
  demander d'élévation à l'usage courant.
- **FR-030**: Le système MUST appliquer les limites même lorsque l'interface est fermée.
- **FR-031**: Le composant privilégié MUST valider et borner toute instruction reçue de
  l'interface avant de l'appliquer, et MUST rejeter toute instruction provenant d'une source
  non autorisée.
- **FR-032**: Le système MUST NOT transmettre de donnée hors de la machine et MUST fonctionner
  intégralement hors ligne.
- **FR-033**: Le système MUST journaliser localement les événements significatifs (démarrage,
  arrêt, application et retrait de règle, échecs) sans y inclure de donnée personnelle ni de
  métadonnée de destination.
- **FR-034**: Le système MUST permettre à tout utilisateur connecté de consulter le monitoring,
  les règles existantes et l'état de santé sans élévation de privilèges.
- **FR-034a**: Le système MUST exiger une élévation de privilèges ponctuelle pour toute
  modification de l'état de limitation : création, modification, suppression, activation ou
  désactivation d'une règle, modification du plafond global, changement de profil actif et
  suspension globale.
- **FR-034b**: Le système MUST présenter l'interface en lecture seule de façon explicite à un
  utilisateur non élevé — les commandes de modification sont visibles mais désactivées, avec la
  raison affichée — plutôt que de laisser l'utilisateur échouer après saisie.
- **FR-034c**: Le système MUST conserver l'élévation obtenue pour la durée d'une session de
  modification, de façon à ne pas déclencher une invite par règle modifiée.

**Compatibilité**

- **FR-035**: Le système MUST déclarer sa matrice de compatibilité (versions et éditions de
  Windows, architectures) et MUST refuser de démarrer avec un message explicite hors de cette
  matrice, plutôt que de fonctionner partiellement.
- **FR-036**: Le système MUST continuer de fonctionner correctement lors d'un changement
  d'adaptateur réseau, d'une mise en veille suivie d'une reprise, et en présence d'adaptateurs
  virtuels.
- **FR-037**: Le système MUST se comporter de façon définie et documentée en présence d'un VPN
  actif : soit la limitation s'applique au trafic tunnelisé, soit l'utilisateur en est informé.
- **FR-038**: Le système MUST définir le traitement des applications packagées (Microsoft
  Store) : prise en charge, ou message explicite de non-prise-en-charge.

**Identification et périmètre du trafic**

- **FR-039**: Le système MUST identifier l'application visée par une règle par le chemin
  complet normalisé de son exécutable, et MUST enregistrer le nom de fichier de l'exécutable
  comme critère de repli.
- **FR-039a**: Lorsque le chemin enregistré n'existe plus, le système MUST appliquer la règle à
  tout processus dont le nom d'exécutable correspond au critère de repli, et MUST signaler
  visiblement que la règle s'applique via le repli plutôt que via le chemin exact.
- **FR-039b**: Le système MUST détecter qu'une règle vise un chemin devenu introuvable et MUST
  proposer à l'utilisateur de la remapper vers le nouveau chemin en une action.
- **FR-039c**: Le système MUST NOT appliquer une règle à un processus dont le nom correspond au
  repli mais dont le chemin appartient à un emplacement différent, sans le signaler à
  l'utilisateur — le nombre de processus couverts par une règle et leurs chemins MUST être
  consultables.
- **FR-040**: Le système MUST comptabiliser dans les plafonds le seul trafic à destination ou en
  provenance d'internet.
- **FR-040a**: Le système MUST exclure des plafonds le trafic de boucle locale et le trafic à
  destination ou en provenance du réseau local (plages d'adresses privées, adresses de
  lien-local, multicast et diffusion).
- **FR-040b**: Le système MUST appliquer la même exclusion à l'affichage du monitoring, ou MUST
  distinguer visuellement trafic internet et trafic local, de sorte que le débit affiché pour
  une application soit cohérent avec le plafond qui lui est appliqué.
- **FR-040c**: Le système MUST maintenir cette distinction lorsqu'un VPN est actif, ou MUST
  informer l'utilisateur que la distinction n'est pas fiable dans cette configuration.

### Key Entities

- **Application surveillée** : un programme identifiable de façon stable, porteur d'un nom
  lisible, d'une icône et d'un chemin. Peut correspondre à plusieurs processus simultanés.
  Peut être active ou non, avec ou sans règle associée.
- **Règle de limitation** : associe une application surveillée à un plafond descendant et un
  plafond montant, chacun optionnel. Possède un état actif/inactif et appartient à un profil.
- **Plafond global** : plafond descendant et montant s'appliquant à tout le trafic de la
  machine, avec une liste d'applications exemptées. Appartient à un profil.
- **Profil** : ensemble nommé de règles et d'un plafond global. Un seul profil est actif à la
  fois. Le profil actif est mémorisé au travers des redémarrages.
- **Mesure de débit** : débit descendant et montant instantanés d'une application ou de la
  machine, à un instant donné. Conservée sur une fenêtre courte, jamais persistée durablement.
- **État de santé** : indique si la limitation est opérationnelle, le nombre de règles
  effectivement appliquées, et les causes d'inapplication éventuelles.
- **Événement journalisé** : horodatage, niveau, catégorie et description d'un fait significatif
  du système, sans donnée personnelle ni métadonnée de destination.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Un utilisateur qui découvre l'application limite une application en cours
  d'exécution en moins de 30 secondes et en trois interactions au maximum, sans consulter de
  documentation.
- **SC-002**: Le débit réel d'une application limitée reste dans une marge de 10 % du plafond
  demandé, mesuré sur une fenêtre de 10 secondes, pour tout plafond compris entre 100 Ko/s et
  100 Mo/s, en montant tous protocoles et en descendant sur protocole à contrôle de congestion.
- **SC-002a**: En descendant sans contrôle de congestion, la marge est de 20 %, et la proportion
  de données rejetées pour tenir le plafond est visible par l'utilisateur.
- **SC-003**: Une limite créée, modifiée ou supprimée prend effet en moins de 5 secondes.
- **SC-004**: La somme des débits de toutes les applications reste sous le plafond global dans
  la même marge de 10 %, avec au moins trois applications transférant simultanément.
- **SC-005**: Les valeurs de débit affichées s'écartent de moins de 10 % de celles mesurées par
  un outil système de référence sur la même période.
- **SC-006**: Après un redémarrage de la machine, les limites du profil actif sont appliquées
  en moins de 60 secondes après l'ouverture de session, sans ouvrir l'interface.
- **SC-007**: En usage normal, l'outil consomme moins de 2 % de processeur en moyenne et moins
  de 150 Mo de mémoire, interface ouverte.
- **SC-008**: Lorsqu'aucune limite n'est active, la latence réseau et le débit maximal
  atteignable ne se dégradent pas de plus de 2 % par rapport à une machine sans l'outil.
- **SC-009**: 100 % des arrêts inattendus du composant de limitation aboutissent à un trafic non
  limité, vérifié par un test d'arrêt brutal répété.
- **SC-010**: Une désinstallation laisse zéro composant système modifié, zéro limite active et
  zéro fichier de configuration résiduel, vérifié sur une machine propre.
- **SC-011**: L'installation et la première utilisation réussissent sans intervention manuelle
  sur chaque configuration de la matrice de compatibilité déclarée.
- **SC-012**: Aucun octet n'est émis par l'outil vers une destination extérieure à la machine,
  vérifié par capture réseau sur une session complète d'utilisation.
- **SC-013**: Un utilisateur sans droits administrateur ne parvient à modifier aucune limite, y
  compris en manipulant directement les fichiers de configuration, vérifié par un test dédié.
- **SC-014**: Un transfert sur le réseau local atteint le même débit avec et sans plafond
  internet actif, à 2 % près.
- **SC-015**: Une application dont l'exécutable est déplacé vers un dossier versionné reste
  limitée après sa mise à jour, sans intervention de l'utilisateur.

## Assumptions

- **Architecture imposée en amont** : la limitation est assurée par un composant privilégié
  fonctionnant en permanence, indépendant de l'interface utilisateur, laquelle s'exécute sans
  élévation. Cette décision, prise avant la rédaction, conditionne FR-029, FR-030 et FR-031.
- **Poste personnel mono-utilisateur** : l'usage visé est une machine personnelle. Le
  fonctionnement en sessions simultanées est traité comme un cas limite à ne pas casser, pas
  comme un scénario optimisé.
- **Portée machine, pas session** : règles, plafond global et profil actif s'appliquent à la
  machine entière. Il n'existe pas de configuration par utilisateur Windows en v1.
- **Consultation libre, modification élevée** (FR-034) : le monitoring est un outil de
  diagnostic sans donnée sensible, donc ouvert à tous ; la modification touche l'état réseau de
  la machine, donc réservée à un administrateur. Conséquence assumée : un poste partagé peut
  imposer des limites qu'un compte standard ne peut pas lever.
- **Définition du « réseau local »** (FR-040) : plages privées RFC 1918, lien-local IPv4 et
  IPv6, uniques locales IPv6, boucle locale, multicast et diffusion. Cette liste est un défaut
  documenté, ajustable si un cas réel la met en défaut.
- **Machine de bureau ou portable** : aucun objectif de fonctionnement sur serveur, ni de
  pilotage à distance, ni d'administration centralisée de parc.
- **Unités** : les débits sont saisis et affichés en Ko/s et Mo/s (base 1024), avec l'unité
  toujours visible pour lever l'ambiguïté avec les Kb/s des offres commerciales.
- **Bornes de plafond** : minimum 10 Ko/s, maximum 1 Go/s. Un plafond au-delà du débit réel de
  la connexion est accepté et sans effet.
- **Pas de partage par priorité** : lorsqu'un plafond global est atteint, le partage entre
  applications n'est pas garanti équitable ni priorisé ; seule la contrainte du total est
  tenue. Une politique de priorité relève d'une version ultérieure.
- **Pas de quota de volume** : le produit limite un débit instantané, pas un volume cumulé par
  jour ou par mois.
- **Hors périmètre v1**, confirmé par l'utilisateur : planification horaire, règles dépendantes
  du réseau connecté, blocage total d'une application, statistiques de consommation à long
  terme, gestion multi-machines.
- **Distribution** : installation par programme d'installation classique ; pas de version
  portable en v1, l'installation d'un composant privilégié la rendant inapplicable.
- **Langue** : interface en français en v1, structure du produit permettant l'ajout d'autres
  langues sans refonte.
