<!--
SYNC IMPACT REPORT (scratch — à supprimer avant commit)
Version: TEMPLATE (non versionné) → 1.0.0
Bump: MAJOR initial — première ratification, tous les placeholders remplacés.

Principes ajoutés (aucun renommage, aucune suppression : document vierge au départ) :
- I. Moindre privilège par défaut (NON NÉGOCIABLE)
- II. Compatibilité Windows explicite et vérifiée
- III. Test-First sur le cœur réseau (NON NÉGOCIABLE)
- IV. Sécurité du trafic : fail-open, jamais fail-closed
- V. Simplicité et YAGNI
- VI. Observabilité et diagnosticabilité

Sections ajoutées :
- Contraintes techniques et exigences de sécurité (SECTION_2)
- Workflow de développement et quality gates (SECTION_3)
- Governance

TODO différés : aucun.
-->

# NetworkLimiter Constitution

## Core Principles

### I. Moindre privilège par défaut (NON NÉGOCIABLE)

Le code privilégié MUST être réduit au strict minimum et isolé du reste de l'application.

- L'interface utilisateur MUST s'exécuter dans le contexte de l'utilisateur courant, sans
  élévation UAC. Un manifeste demandant `requireAdministrator` sur l'UI est un rejet de revue.
- Seul le service Windows (contexte `LocalSystem`, ou compte plus restreint si suffisant)
  MUST détenir les capacités réseau privilégiées (chargement du pilote, interception,
  écriture de règles).
- L'IPC entre UI et service MUST être authentifié et autorisé : named pipe local avec ACL
  explicite, refus des connexions distantes, vérification de l'identité de l'appelant côté
  service, et validation systématique de chaque message reçu (schéma, bornes, longueurs).
- Le service MUST traiter tout message de l'UI comme non fiable. Aucune donnée reçue ne
  MUST être utilisée pour construire un chemin de fichier, une commande ou une requête sans
  validation préalable.
- Les fichiers de configuration écrits par le service MUST résider dans un répertoire dont
  les ACL interdisent l'écriture aux utilisateurs non-administrateurs, afin qu'une règle ne
  puisse pas être détournée en vecteur d'élévation.

**Rationale** : une application qui inspecte et modifie le trafic réseau de toute la machine
est une cible de choix. Séparer un service minimal et privilégié d'une UI non privilégiée
borne l'impact d'une faille dans le code le plus exposé (parsing, rendu, dépendances UI).

### II. Compatibilité Windows explicite et vérifiée

La matrice de compatibilité MUST être déclarée, testée et jamais élargie implicitement.

- La version minimale supportée MUST être documentée dans le README et vérifiée au démarrage
  du service ; sous cette version, l'application MUST refuser de démarrer avec un message
  explicite plutôt que de dégrader silencieusement.
- Toute API Windows utilisée MUST être vérifiée comme disponible sur l'ensemble de la matrice
  (éditions Home incluses) ou encapsulée derrière une détection de capacité avec repli défini.
- L'application MUST supporter x64 et ARM64, ou déclarer explicitement l'architecture non
  supportée et échouer proprement à l'installation.
- Toute dépendance à un pilote noyau MUST documenter sa signature, son éditeur, sa version
  épinglée et son comportement en cas de Secure Boot, de HVCI ou de blocage par un antivirus.
- Les cas dégradés MUST être traités comme des exigences, pas des bugs : VPN actif, adaptateurs
  virtuels (Hyper-V, WSL, Docker), IPv6, réseau mesuré, changement d'adaptateur à chaud.

**Rationale** : un limiteur de bande passante touche la pile réseau du système. Un
comportement non défini sur une configuration non testée ne se traduit pas par une
fonctionnalité manquante mais par une perte de connectivité pour l'utilisateur.

### III. Test-First sur le cœur réseau (NON NÉGOCIABLE)

Le comportement de limitation MUST être couvert par des tests automatisés écrits avant le code.

- Toute logique de calcul (token bucket / shaping, agrégation de débit, résolution
  process → règle, priorité des règles, limite globale vs limites par application) MUST être
  implémentée dans des composants purs, sans dépendance à Windows, et couverte par des tests
  unitaires déterministes utilisant une horloge injectée. Aucun `Thread.Sleep` dans ces tests.
- Le contrat IPC MUST avoir des tests de contrat exécutés des deux côtés (sérialisation,
  rejet des messages malformés, rejet des valeurs hors bornes, compatibilité de version).
- Les tests d'intégration MUST couvrir le cycle de vie complet : installation du pilote,
  application d'une règle, persistance, redémarrage du service, désinstallation propre.
- Une procédure de vérification de débit reproductible MUST exister (mesure du débit réel
  obtenu pour un plafond donné, tolérance documentée) et MUST être exécutée avant toute
  release.
- Aucune correction de bug dans le cœur réseau ne MUST être fusionnée sans un test qui échoue
  avant le correctif.

**Rationale** : un shaper faux est indétectable à l'œil nu — il « marche » tout en délivrant
le mauvais débit. Seule une mesure automatisée face à une valeur attendue le prouve.

### IV. Sécurité du trafic : fail-open, jamais fail-closed

En cas de défaillance, l'application MUST rendre le réseau à l'utilisateur.

- Crash du service, arrêt du processus, échec de chargement du pilote, exception non gérée :
  le trafic MUST reprendre sans limitation. Aucun chemin de code ne MUST laisser le système
  dans un état où le trafic reste bloqué ou étranglé sans processus vivant pour le libérer.
- La désinstallation MUST retirer intégralement le pilote, les règles persistées et toute
  configuration système modifiée. Un test de désinstallation MUST le vérifier.
- Un « interrupteur d'arrêt » MUST être accessible et lever instantanément toutes les limites.
- Le blocage total d'une application n'est PAS une fonctionnalité de ce projet : le périmètre
  est la limitation de débit. Toute évolution vers du blocage MUST faire l'objet d'un
  amendement à cette constitution.

**Rationale** : la perte de connectivité est le pire mode de défaillance possible pour ce
produit, et le plus difficile à diagnostiquer pour l'utilisateur, précisément parce que les
outils de diagnostic passent par le réseau.

### V. Simplicité et YAGNI

La complexité MUST être justifiée au moment où elle est introduite, pas anticipée.

- L'application MUST rester compréhensible et utilisable sans documentation : limiter une
  application MUST se faire en moins de trois interactions.
- Aucune couche d'abstraction ne MUST être ajoutée pour un seul implémenteur. Les interfaces
  n'existent que là où un test ou une seconde implémentation réelle les exige.
- Toute nouvelle dépendance MUST être justifiée par écrit dans la PR : ce qu'elle apporte,
  sa licence, sa maintenance, et le coût de s'en passer.
- Les fonctionnalités hors périmètre v1 (planification horaire, règles par réseau, priorités
  QoS, statistiques long terme) MUST rester hors du code tant qu'elles ne sont pas spécifiées.

**Rationale** : chaque ligne de ce projet s'exécute soit avec des privilèges élevés, soit à
proximité de code qui en a. Moins de code signifie directement moins de surface d'attaque.

### VI. Observabilité et diagnosticabilité

L'application MUST pouvoir expliquer ce qu'elle fait sans débogueur attaché.

- Le service MUST produire des logs structurés, horodatés, avec niveaux, dans un fichier
  rotatif borné en taille, plus les événements critiques dans le journal d'événements Windows.
- Les logs MUST enregistrer : démarrage/arrêt, chargement du pilote, application et retrait de
  chaque règle, erreurs d'interception, et transitions de l'interrupteur d'arrêt.
- Les logs MUST NOT contenir de contenu de paquet, d'URL, de nom d'hôte distant ou de donnée
  personnelle. Les métadonnées de flux (processus, volume, débit) sont autorisées.
- L'UI MUST exposer un état de santé lisible : service joignable ou non, pilote chargé ou non,
  nombre de règles actives, et la raison d'une règle inactive.
- Aucune télémétrie ne MUST être émise hors de la machine. L'application MUST fonctionner
  entièrement hors ligne.

**Rationale** : l'utilisateur doit pouvoir répondre seul à « pourquoi cette application n'est
pas limitée ? ». Sans cette réponse, le produit devient imprévisible et donc inutilisable.

## Contraintes techniques et exigences de sécurité

**Stack imposée** :

- .NET 8 (LTS), C#, `nullable` activé et warnings traités en erreurs sur tous les projets.
- Service Windows : worker `BackgroundService`, démarrage automatique différé.
- Interface : WPF avec icône de zone de notification, exécutée sans élévation.
- Interception : WinDivert, en liaison dynamique, version épinglée. Sa licence (LGPL/GPL)
  MUST être respectée et son texte redistribué avec l'application.
- Persistance : fichiers locaux (JSON) dans `ProgramData`, ACL restreintes en écriture.
  Aucune base de données, aucun service réseau, aucun compte utilisateur.
- IPC : named pipe local uniquement. Aucun socket TCP, même en loopback.

**Exigences de sécurité applicables à toute contribution** :

- Aucun secret, clé ou identifiant en dur dans le dépôt.
- Les dépendances MUST être épinglées et auditées (`dotnet list package --vulnerable` en CI,
  build en échec sur vulnérabilité connue de sévérité haute ou critique).
- Tout code interopérant avec le natif (P/Invoke) MUST valider les tailles de buffer, libérer
  ses handles de façon déterministe, et MUST NOT utiliser de `unsafe` sans justification écrite.
- Les binaires de release MUST être signés. Un binaire non signé ne MUST NOT être distribué.
- Le processus d'installation MUST NOT désactiver de protection Windows (Defender, SmartScreen,
  Secure Boot, contrôle d'intégrité) ni demander à l'utilisateur de le faire.

## Workflow de développement et quality gates

**Portes bloquantes avant fusion** — une PR qui échoue à l'une d'elles MUST NOT être fusionnée :

1. Build sans warning sur toutes les configurations.
2. Tests unitaires et de contrat au vert.
3. Tests d'intégration au vert sur au moins une VM de la matrice de compatibilité.
4. Analyse de vulnérabilité des dépendances au vert.
5. Revue humaine attestant explicitement la conformité aux principes I à VI.

**Portes supplémentaires avant release** :

6. Vérification de débit mesurée (principe III) sur limite par application et limite globale.
7. Cycle installation → usage → désinstallation vérifié sur une machine propre, sans résidu.
8. Vérification manuelle du fail-open : arrêt brutal du service, confirmation du retour au
   trafic non limité.

**Règles de contribution** :

- Toute modification touchant le service, le pilote ou l'IPC MUST référencer la spécification
  correspondante et MUST être revue par une seconde personne.
- Les changements du contrat IPC MUST être versionnés ; le service MUST refuser proprement une
  UI de version incompatible avec un message actionnable plutôt que d'échouer silencieusement.
- Le versionnage du produit suit `MAJOR.MINOR.PATCH`.

## Governance

Cette constitution prévaut sur toute autre pratique, convention ou préférence de ce projet. En
cas de conflit entre ce document et un plan, une tâche ou une habitude de code, ce document
l'emporte.

**Amendements** : toute modification MUST être proposée par écrit, énoncer le principe touché,
la motivation, et l'impact sur le code existant, incluant un plan de migration si des
changements sont nécessaires. Un amendement entre en vigueur une fois ce document mis à jour et
la version incrémentée.

**Versionnage de la constitution** :

- MAJOR : suppression ou redéfinition incompatible d'un principe ou d'une règle de gouvernance.
- MINOR : ajout d'un principe ou d'une section, ou extension matérielle d'une règle existante.
- PATCH : clarification, reformulation, correction n'altérant pas le sens.

**Conformité** : chaque revue de code MUST vérifier la conformité aux principes et l'attester
dans la PR. Toute complexité non triviale MUST être justifiée dans la PR ; à défaut, elle MUST
être retirée. Les manquements constatés MUST être consignés et corrigés avant la release
suivante.

**Version**: 1.0.0 | **Ratified**: 2026-09-18 | **Last Amended**: 2026-09-18
