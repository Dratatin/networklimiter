---
description: "Plan de tâches — Limiteur de bande passante par application"
---

# Tasks: Limiteur de bande passante par application

**Input**: Documents de conception dans `/specs/001-bandwidth-limiter/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md),
[data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md)

**Tests**: **OBLIGATOIRES**. Le principe III de la constitution rend le test-first non négociable
sur le cœur réseau, le contrat IPC et la logique de classification. Les tâches de test précèdent
les tâches d'implémentation dans chaque phase et **doivent échouer avant** d'être satisfaites.

**Organization**: tâches groupées par user story, chaque story livrable et testable seule.

**Révision du 2026-09-18** : quatre correctifs issus de `/speckit-analyze` — couverture des cas
dégradés du principe II (T047–T048, T128), couverture IPv6 (T040–T041, T125), porte de fusion
d'intégration sur VM (T012), et déplacement du squelette d'installeur en Phase 2 (T056) pour que
US4 ne dépende plus de US5.

## Format: `[ID] [P?] [Story] Description`

- **[P]** : parallélisable (fichiers distincts, aucune dépendance sur une tâche en cours)
- **[Story]** : US1 à US5, correspondant aux user stories de [spec.md](./spec.md)

## Path Conventions

Structure définie dans [plan.md](./plan.md) : projets de production sous `src/`, six projets de
test sous `tests/`, outillage sous `tools/`. Séparation par **frontière de privilège**, pas par
couche technique.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: squelette de solution, épinglage des dépendances, garde-fous de build.

- [X] T001 Créer la solution `NetworkLimiter.sln` et l'arborescence `src/`, `tests/`, `tools/` conforme à plan.md
- [X] T002 [P] Créer `Directory.Build.props` : `TargetFramework=net10.0`, `Nullable=enable`, `TreatWarningsAsErrors=true`, `EnforceCodeStyleInBuild=true`, `Deterministic=true`
- [X] T003 [P] Créer `Directory.Packages.props` avec gestion centralisée des paquets et **versions épinglées exactes** (constitution : dépendances épinglées et auditées)
- [X] T004 [P] Créer `.editorconfig` à la racine avec les règles d'analyse C# et le niveau d'avertissement en erreur
- [X] T005 [P] Écrire `tools/restore-windivert.ps1` : téléchargement de WinDivert 2.2.2, **vérification de l'empreinte SHA-256 et de la signature Authenticode** avant placement dans l'arborescence de build ; échec dur si l'une des deux ne correspond pas
- [X] T006 [P] Créer `src/NetworkLimiter.Core/NetworkLimiter.Core.csproj` sans aucune référence à un assembly Windows
- [X] T007 [P] Créer `src/NetworkLimiter.Contracts/NetworkLimiter.Contracts.csproj`
- [X] T008 [P] Créer `src/NetworkLimiter.Service/NetworkLimiter.Service.csproj` (worker, `Microsoft.Extensions.Hosting.WindowsServices`)
- [X] T009 [P] Créer `src/NetworkLimiter.App/NetworkLimiter.App.csproj` (WPF, `UseWPF=true`) **sans** manifeste `requireAdministrator`
- [X] T010 [P] Créer les six projets de test sous `tests/` conformément à plan.md (xUnit + FluentAssertions)
- [X] T011 [P] Créer `.github/workflows/ci.yml` : build, tests unitaires et de contrat, `dotnet list package --vulnerable --include-transitive` avec échec sur sévérité haute ou critique
- [X] T012 [P] Créer `.github/workflows/integration.yml` : exécution des tests d'intégration sur une **VM de la matrice de compatibilité** — c'est la porte de fusion n° 3 de la constitution, aujourd'hui sans automatisation
- [X] T013 [P] Créer `README.md` avec la matrice de compatibilité de plan.md et `THIRD-PARTY-NOTICES.md` contenant le texte de licence LGPL v3 de WinDivert

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: tout ce dont *chaque* user story dépend. Ce produit n'a pas de MVP possible sans sa
chaîne d'interception complète — c'est pourquoi cette phase est substantielle.

**⚠️ CRITIQUE** : aucune user story ne peut démarrer avant la fin de cette phase.

### Primitives pures (Core)

- [X] T014 [P] Écrire les tests caractérisant le contrat d'horloge dans `tests/NetworkLimiter.Core.Tests/Time/VirtualClockTests.cs` : mesure de durée exacte, monotonie, progression uniquement sur demande explicite
- [X] T015 [P] **Écart assumé** — adopter `TimeProvider` (BCL) et `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`) au lieu d'écrire `ISystemClock` / `SystemClock` / `FakeClock`. Mêmes garanties de déterminisme, zéro abstraction maison à maintenir : le principe V demande de ne pas ajouter de couche là où le framework en fournit une éprouvée
- [X] T016 [P] Écrire les tests de bornes de débit dans `tests/NetworkLimiter.Core.Tests/Units/ByteRateTests.cs` : rejet à **10 239**, acceptation à **10 240**, acceptation à **1 073 741 824**, rejet à **1 073 741 825**, acceptation de `null` (illimité)
- [X] T017 [P] Implémenter le type valeur `ByteRate` et sa validation dans `src/NetworkLimiter.Core/Units/ByteRate.cs`
- [X] T018 [P] Écrire les tests de classification dans `tests/NetworkLimiter.Core.Tests/Classification/NetworkScopeTests.cs`, aux **bornes exactes** de chaque plage de R-006 : `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `127.0.0.0/8`, `169.254.0.0/16`, `224.0.0.0/4`, `255.255.255.255`, `::1`, `fe80::/10`, `fc00::/7`, `ff00::/8`, plus des adresses publiques adjacentes à chaque borne
- [X] T019 [P] Implémenter `NetworkScopeClassifier` (`Internet` / `Local` / `Loopback`) dans `src/NetworkLimiter.Core/Classification/NetworkScopeClassifier.cs`
- [X] T020 [P] Écrire les tests de normalisation de chemin dans `tests/NetworkLimiter.Core.Tests/Classification/PathNormalizerTests.cs` : **idempotence** `normalize(normalize(p)) == normalize(p)`, casse invariante, `\Device\HarddiskVolumeN\` → lettre de lecteur, jonctions et liens symboliques
- [X] T021 [P] Implémenter `PathNormalizer` dans `src/NetworkLimiter.Core/Classification/PathNormalizer.cs`

### Contrat IPC (Contracts)

- [X] T022 [P] Écrire les tests de sérialisation dans `tests/NetworkLimiter.Contracts.Tests/SerializationTests.cs` : aller-retour de chaque message, **rejet de tout champ inconnu**, refus de la désérialisation polymorphe
- [X] T023 [P] Définir les types de message de `contracts/ipc-protocol.md` dans `src/NetworkLimiter.Contracts/Messages/` et le `JsonSerializerContext` **par générateur de source** dans `src/NetworkLimiter.Contracts/Serialization/`
- [X] T024 [P] Écrire les tests de cadrage dans `tests/NetworkLimiter.Contracts.Tests/FramingTests.cs` : préfixe de longueur 4 octets petit-boutiste, message de **65 537 octets rejeté sans désérialisation**, JSON tronqué, profondeur excessive, clés dupliquées
- [X] T025 [P] Implémenter le codec de cadrage dans `src/NetworkLimiter.Contracts/Serialization/MessageFraming.cs` avec plafond de **65 536 octets**

### Canal IPC et autorisation (Service)

- [X] T026 Écrire le test d'ACL dans `tests/NetworkLimiter.Service.Tests/Ipc/PipeSecurityTests.cs` : la DACL porte une **ACE de refus explicite sur `S-1-5-2` (NETWORK)** et sur `ANONYMOUS LOGON`, placées avant les ACE d'autorisation
- [X] T027 Implémenter `PipeServer` et sa `PipeSecurity` dans `src/NetworkLimiter.Service/Ipc/PipeServer.cs` : `\\.\pipe\NetworkLimiter.v1`, message-mode, 4 instances, délai d'inactivité 30 s
- [X] T028 Écrire les tests de poignée de main dans `tests/NetworkLimiter.Contracts.Tests/HandshakeTests.cs` : requête avant `Hello` → connexion fermée ; `protocolVersion` différent → `ProtocolVersionMismatch` puis fermeture ; `elevated` déclaré par le client **ignoré**
- [X] T029 Implémenter la poignée de main et la vérification de version dans `src/NetworkLimiter.Service/Ipc/HandshakeHandler.cs`
- [X] T030 Écrire les tests d'autorisation dans `tests/NetworkLimiter.Service.Tests/Ipc/AuthorizationTests.cs` : **chaque** message d'écriture émis sans élévation renvoie `ElevationRequired` **et ne laisse aucun changement persisté**
- [X] T031 Implémenter l'autorisation par `RunAsClient` + `WindowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator)` dans `src/NetworkLimiter.Service/Ipc/CallerAuthorization.cs`, **évaluée à chaque message**, jamais mémorisée
- [X] T032 Implémenter le client IPC dans `src/NetworkLimiter.App/Ipc/PipeClient.cs` avec reconnexion et remontée d'état de connexion

### Hôte de service, journalisation, compatibilité

- [X] T033 Implémenter l'hôte worker dans `src/NetworkLimiter.Service/Program.cs` avec Serilog vers fichier **rotatif borné en taille** dans `%ProgramData%\NetworkLimiter\logs\` et journal d'événements Windows pour le critique
- [X] T034 Écrire les tests de compatibilité dans `tests/NetworkLimiter.Service.Tests/CompatibilityGateTests.cs` : build < 19045 refusé, architecture ARM64 refusée, x86 refusé, message nommant la cause
- [X] T035 Implémenter `CompatibilityGate` au démarrage dans `src/NetworkLimiter.Service/CompatibilityGate.cs` — **refus explicite**, jamais de dégradation silencieuse
- [X] T036 Écrire les tests de transition dans `tests/NetworkLimiter.Service.Tests/Safety/FailOpenTests.cs` : tout chemin quittant `Shaping` **ferme les handles avant toute autre action** ; `Refused`, `Degraded` et `FailOpen` n'appliquent aucune limite
- [X] T037 Implémenter la machine à états et le chien de garde dans `src/NetworkLimiter.Service/Safety/` : fermeture des handles sur arrêt, `SERVICE_CONTROL_SHUTDOWN`, exception non gérée et blocage de la boucle

### Interception WinDivert

- [X] T038 [P] Implémenter les liaisons P/Invoke dans `src/NetworkLimiter.Service/Interception/WinDivertNative.cs` : `WinDivertOpen`, `WinDivertRecvEx`, `WinDivertSendEx`, `WinDivertSetParam`, `WinDivertShutdown`, `WinDivertClose`, avec `SafeHandle` et libération déterministe
- [X] T039 [P] Définir l'abstraction `IPacketInterceptor` et son double de test dans `src/NetworkLimiter.Service/Interception/` et `tests/NetworkLimiter.Service.Tests/Fakes/`
- [X] T040 [P] Écrire les tests du constructeur de filtre dans `tests/NetworkLimiter.Service.Tests/Interception/FilterBuilderTests.cs` : le filtre couvre **IPv4 et IPv6** (FR-008), exclut la boucle locale via le bit `Loopback`, et le trafic IPv6 n'échappe à aucun plafond
- [X] T041 [P] Implémenter `FilterBuilder` (chaîne de filtre WinDivert, IPv4 + IPv6) dans `src/NetworkLimiter.Service/Interception/FilterBuilder.cs` — un plafond contournable en IPv6 serait une défaillance silencieuse
- [X] T042 Ouvrir le handle FLOW (`WINDIVERT_LAYER_FLOW`, `SNIFF | RECV_ONLY`) dans `src/NetworkLimiter.Service/Interception/FlowInterceptor.cs`
- [X] T043 Écrire les tests de table de flux dans `tests/NetworkLimiter.Service.Tests/FlowTable/FlowTableTests.cs` : purge sur `FLOW_DELETED`, balayage des orphelins, table bornée, **flux inconnu → paquet réinjecté immédiatement sans limitation**
- [X] T044 Implémenter `FlowTable` (quintuplet → PID) dans `src/NetworkLimiter.Service/FlowTable/FlowTable.cs`
- [X] T045 Ouvrir le handle NETWORK et la boucle de drainage dans `src/NetworkLimiter.Service/Interception/NetworkInterceptor.cs` : lecture par lots de **255** paquets, `QUEUE_LENGTH=16384`, `QUEUE_SIZE=33554432`, `QUEUE_TIME` au défaut de 2000 ms
- [X] T046 Implémenter l'indicateur de pression de file et la **fermeture du handle au-delà du seuil** dans `src/NetworkLimiter.Service/Health/QueuePressureMonitor.cs` — ne plus limiter plutôt que laisser le noyau rejeter en silence (R-004)

### Résilience environnementale (principe II)

- [X] T047 [P] Écrire les tests de résilience dans `tests/NetworkLimiter.Service.Tests/Resilience/NetworkChangeTests.cs` : changement d'adaptateur Wi-Fi ↔ Ethernet, mise en veille suivie de reprise, arrivée d'un partage de connexion mobile, présence d'adaptateurs virtuels Hyper-V / WSL / Docker, réseau marqué comme mesuré. Attendu : **les règles restent appliquées ou sont réappliquées en moins de 10 secondes, sans redémarrage du service** (FR-036)
- [X] T048 Implémenter `NetworkChangeMonitor` dans `src/NetworkLimiter.Service/Resilience/NetworkChangeMonitor.cs` : abonnement aux changements d'adresse et d'adaptateur, reprise de veille, réouverture des handles si nécessaire, journalisation de chaque bascule

### Identité de processus

- [X] T049 [P] Écrire les tests dans `tests/NetworkLimiter.Service.Tests/ProcessIdentity/ProcessIdentityResolverTests.cs` : clé de cache `(processId, startTime)`, **PID réutilisé → nouvelle résolution**, chemin non résoluble → instance « inconnue » dont le trafic n'est jamais limité
- [X] T050 Implémenter `ProcessIdentityResolver` (`QueryFullProcessImageName`) dans `src/NetworkLimiter.Service/ProcessIdentity/ProcessIdentityResolver.cs`

### Persistance de base

- [X] T051 [P] Écrire les tests d'écriture atomique dans `tests/NetworkLimiter.Service.Tests/Persistence/AtomicWriteTests.cs` : interruption simulée à chaque étape → relecture de l'ancien **ou** du nouveau contenu, jamais d'un fichier tronqué
- [X] T052 Implémenter `ConfigStore` dans `src/NetworkLimiter.Service/Persistence/ConfigStore.cs` : `%ProgramData%\NetworkLimiter\config.json`, `schemaVersion=1`, séquence temporaire → flush → `File.Replace` → suppression de la sauvegarde
- [X] T053 Écrire le test d'ACL dans `tests/NetworkLimiter.Integration.Tests/Security/ConfigAclTests.cs` (SC-013) : sous un compte standard, écriture, remplacement, renommage et modification d'ACL tous refusés
- [X] T054 Implémenter la pose et la **revérification au démarrage** des ACL dans `src/NetworkLimiter.Service/Persistence/ConfigAcl.cs` : héritage désactivé, `SYSTEM` et `Administrators` en contrôle total, `Users` en lecture seule, correction et journalisation si trouvée trop permissive

### Déploiement minimal

- [X] T055 [P] Écrire `tools/register-windivert-dev.ps1` pour enregistrer le service pilote en développement, le service ouvrant ses handles avec `WINDIVERT_FLAG_NO_INSTALL`
- [X] T056 Créer le squelette WiX v5 dans `src/NetworkLimiter.Installer/` : installation du service, **enregistrement du pilote WinDivert à l'installation** (R-011), pose des ACL de `%ProgramData%`. Placé en Phase 2 parce que les tests de US4 sur machine propre en dépendent ; conditions d'architecture et désinstallation restent en US5

### État de santé

- [X] T057 Implémenter `HealthState`, l'énumération exhaustive des raisons d'inactivité et le gestionnaire `GetHealth` dans `src/NetworkLimiter.Service/Health/`

**Checkpoint**: chaîne d'interception IPv4/IPv6, IPC sécurisé, persistance, résilience réseau et
fail-open opérationnels. Les user stories peuvent démarrer.

---

## Phase 3: User Story 1 — Limiter une application (Priority: P1) 🎯 MVP

**Goal**: appliquer un plafond descendant et montant à une application en cours d'exécution, et
le retirer, sans interrompre ses connexions.

**Independent Test**: lancer un téléchargement volumineux, appliquer 1 Mo/s en descente, mesurer
le débit du processus pendant 60 s, vérifier qu'il reste sous le plafond, puis retirer la règle
et vérifier le retour au débit nominal.

### Tests for User Story 1 ⚠️

> Écrire ces tests **d'abord** et vérifier qu'ils échouent avant d'implémenter.

- [X] T058 [P] [US1] Écrire les tests d'invariants du seau à jetons dans `tests/NetworkLimiter.Core.Tests/Shaping/TokenBucketTests.cs` : `availableTokens ∈ [0, capacityBytes]`, volume autorisé à 10 % près sur T ≥ 10 s en **horloge virtuelle**, changement de débit à chaud sans remise à zéro, **horloge qui recule** ne crée pas de jetons et ne bloque pas le seau, **aucun `Thread.Sleep`**
- [X] T059 [P] [US1] Écrire les tests de file de retard dans `tests/NetworkLimiter.Core.Tests/Shaping/DelayQueueTests.cs` : borne par règle, rejet en queue **compté**, et FR-003c — pour un flux temporisable, le compteur de rejets reste à **zéro** quel que soit le plafond
- [X] T060 [P] [US1] Écrire les tests de résolution de règle dans `tests/NetworkLimiter.Core.Tests/Rules/RuleResolutionTests.cs` : appariement exact par chemin, repli par nom quand le chemin n'existe plus, **`matchMode` toujours remonté** (FR-039c), refus d'apparier un homonyme d'un autre emplacement sans le signaler
- [X] T061 [P] [US1] Écrire les tests de contrat dans `tests/NetworkLimiter.Contracts.Tests/RuleMessagesTests.cs` : `UpsertRule`, `DeleteRule`, `SetRuleEnabled` testés à `min-1`, `min`, `max`, `max+1`, sans élévation, avec cible en doublon
- [ ] T062 [P] [US1] Écrire le test d'intégration dans `tests/NetworkLimiter.Integration.Tests/Stories/ApplyRuleTests.cs` : règle appliquée en < 5 s sans rompre les connexions établies, modification en < 5 s, retrait en < 5 s, autres applications non affectées

### Implementation for User Story 1

- [X] T063 [P] [US1] Implémenter `TokenBucket` dans `src/NetworkLimiter.Core/Shaping/TokenBucket.cs`, capacité `max(2 × MTU, débit × 100 ms)`
- [X] T064 [P] [US1] Implémenter `DelayQueue` avec compteur de rejets dans `src/NetworkLimiter.Core/Shaping/DelayQueue.cs`
- [X] T065 [P] [US1] Définir `Rule` et `AppIdentity` dans `src/NetworkLimiter.Contracts/Messages/` avec les contraintes de data-model.md : plafonds `null` ou 10 240 à 1 073 741 824, `enabled`, `exemptFromGlobal`, `executablePath` ≤ 32 767 caractères
- [X] T066 [US1] Implémenter `RuleResolver` (appariement exact puis repli, `matchMode`, nombre de processus couverts) dans `src/NetworkLimiter.Core/Rules/RuleResolver.cs`
- [X] T067 [US1] Implémenter `ShapingPipeline` dans `src/NetworkLimiter.Service/Interception/ShapingPipeline.cs` : paquet → flux → PID → application → règle → seau → réinjection, avec passage sans limitation pour `scope != Internet` et pour tout flux inconnu
- [X] T068 [US1] Implémenter la politique de rejet dans `src/NetworkLimiter.Core/Shaping/DropPolicy.cs` : temporisation quand le protocole dispose d'un contrôle de congestion (FR-003c, zéro rejet), rejet uniquement en descendant sans contrôle de congestion avec tolérance 20 % (FR-003a)
- [ ] T069 [US1] Implémenter les gestionnaires `UpsertRule`, `DeleteRule`, `SetRuleEnabled` dans `src/NetworkLimiter.Service/Ipc/Handlers/RuleHandlers.cs`, **transactionnels du point de vue de l'appelant** : validation complète → application mémoire → persistance atomique, échec à toute étape laissant l'état inchangé
- [ ] T070 [US1] Propager les changements de règle au pipeline vivant en < 5 s, sans recréer les seaux existants, dans `src/NetworkLimiter.Service/Interception/ShapingPipeline.cs`
- [ ] T071 [US1] Diffuser `StateChanged` à **tous** les clients connectés, y compris non élevés, dans `src/NetworkLimiter.Service/Ipc/StateBroadcaster.cs`
- [ ] T072 [US1] Implémenter la liste minimale des applications ayant une activité réseau dans `src/NetworkLimiter.App/ViewModels/AppListViewModel.cs`
- [ ] T073 [US1] Implémenter l'éditeur de règle dans `src/NetworkLimiter.App/Views/RuleEditorView.xaml` avec validation des bornes **à la saisie** et unité toujours visible (FR-007)
- [ ] T074 [US1] Implémenter le mode lecture seule dans `src/NetworkLimiter.App/ViewModels/ShellViewModel.cs` : commandes visibles mais désactivées avec la raison affichée (FR-034b)
- [ ] T075 [US1] Implémenter la relance élevée dans `src/NetworkLimiter.App/Elevation/ElevationLauncher.cs` : `ShellExecute` verbe `runas` sur le même exécutable avec `--elevated`, une seule invite par session d'édition
- [ ] T076 [US1] Journaliser l'application et le retrait de chaque règle dans `src/NetworkLimiter.Service/Ipc/Handlers/RuleHandlers.cs` **sans** adresse distante, nom d'hôte ni URL (FR-033)

**Checkpoint**: US1 pleinement fonctionnelle et testable seule. Scénario 5 de quickstart.md
exécutable.

---

## Phase 4: User Story 2 — Monitoring temps réel (Priority: P2)

**Goal**: voir quelles applications consomment la bande passante, avec leur débit instantané et
un historique court, et créer une règle depuis cette vue en une action.

**Independent Test**: lancer deux transferts connus simultanément, vérifier que les deux
applications apparaissent avec des débits cohérents avec une mesure de référence.

### Tests for User Story 2 ⚠️

- [ ] T077 [P] [US2] Écrire les tests d'agrégation dans `tests/NetworkLimiter.Core.Tests/Metrics/RateAggregatorTests.cs` : fenêtre glissante de 60 s au pas de 1 s, **mémoire bornée** quel que soit le temps écoulé, échantillons jamais persistés
- [ ] T078 [P] [US2] Écrire le test de confidentialité dans `tests/NetworkLimiter.Contracts.Tests/MetricsPrivacyTests.cs` : inspection du JSON sérialisé de `MetricsTick` et `GetStateResult`, **aucune adresse distante, aucun nom d'hôte, aucune URL, aucun port distant** (FR-018)
- [ ] T079 [P] [US2] Écrire le test de plafond effectif dans `tests/NetworkLimiter.Contracts.Tests/EffectiveLimitTests.cs` : `effectiveDownloadLimit` est la valeur **résolue**, pas celle saisie (FR-010)
- [ ] T080 [P] [US2] Écrire le test d'exclusion dans `tests/NetworkLimiter.Integration.Tests/Stories/MonitoringScopeTests.cs` : les débits affichés excluent le trafic local et la boucle locale, en cohérence avec les plafonds (FR-040b)

### Implementation for User Story 2

- [ ] T081 [P] [US2] Implémenter `RateAggregator` (fenêtre 60 s) dans `src/NetworkLimiter.Core/Metrics/RateAggregator.cs`
- [ ] T082 [US2] Implémenter `SubscribeMetrics` et la diffusion `MetricsTick` à **1 Hz** dans `src/NetworkLimiter.Service/Ipc/Handlers/MetricsHandlers.cs`
- [ ] T083 [US2] Implémenter la liste temps réel dans `src/NetworkLimiter.App/ViewModels/MonitorViewModel.cs` : nom lisible, icône, débits montant et descendant, tri par consommation décroissante, chemin complet consultable
- [ ] T084 [US2] Implémenter le retrait d'une application inactive après un délai court, **sans supprimer sa règle**, dans `src/NetworkLimiter.App/ViewModels/MonitorViewModel.cs`
- [ ] T085 [US2] Implémenter l'historique 60 s sous forme de courbe dans `src/NetworkLimiter.App/Views/RateHistoryView.xaml` (FR-017)
- [ ] T086 [US2] Implémenter la création de règle depuis une ligne **en une action** dans `src/NetworkLimiter.App/ViewModels/MonitorViewModel.cs` (FR-016)
- [ ] T087 [US2] Afficher le plafond effectif, le `matchMode` en mode repli et la **proportion de données rejetées** à côté de chaque règle dans `src/NetworkLimiter.App/Views/MonitorView.xaml` (FR-003b, FR-039a)
- [ ] T088 [US2] Afficher les règles dont l'application n'est pas lancée avec l'état « en attente » explicite dans `src/NetworkLimiter.App/ViewModels/RuleListViewModel.cs`
- [ ] T089 [US2] Afficher le débit total montant et descendant de la machine dans `src/NetworkLimiter.App/Views/MonitorView.xaml` (FR-014)

**Checkpoint**: US1 et US2 fonctionnent indépendamment.

---

## Phase 5: User Story 3 — Plafond global (Priority: P2)

**Goal**: plafonner la consommation totale de la machine, avec exemptions par application.

**Independent Test**: définir un plafond global de 5 Mo/s, lancer trois transferts simultanés
dans trois applications différentes, vérifier que la somme des débits reste sous le plafond.

### Tests for User Story 3 ⚠️

- [ ] T090 [P] [US3] Écrire les tests de hiérarchie dans `tests/NetworkLimiter.Core.Tests/Shaping/HierarchicalBucketTests.cs` : un paquet n'est émis que si le seau **et** son parent ont assez de jetons, les deux sont débités **ensemble**, jamais l'un sans l'autre
- [ ] T091 [P] [US3] Écrire les tests de résolution dans `tests/NetworkLimiter.Core.Tests/Rules/EffectiveLimitTests.cs` : plafond effectif = `min(règle, global)` par sens, `null` traité comme l'infini ; désactiver le global laisse les règles intactes
- [ ] T092 [P] [US3] Écrire les tests d'exemption dans `tests/NetworkLimiter.Core.Tests/Rules/ExemptionTests.cs` : `exemptFromGlobal` avec plafond propre → limitée par sa règle seule
- [ ] T093 [P] [US3] Écrire les tests de contrat `SetGlobalLimit` dans `tests/NetworkLimiter.Contracts.Tests/GlobalLimitMessagesTests.cs` avec les mêmes bornes que les règles
- [ ] T094 [P] [US3] Écrire le test d'intégration dans `tests/NetworkLimiter.Integration.Tests/Stories/GlobalLimitTests.cs` : trois transferts simultanés sous plafond global, somme des débits dans les 10 % (SC-004) ; un seul transfert peut consommer la totalité du plafond

### Implementation for User Story 3

- [ ] T095 [P] [US3] Implémenter le chaînage parent du seau à jetons dans `src/NetworkLimiter.Core/Shaping/HierarchicalBucket.cs`
- [ ] T096 [P] [US3] Définir `GlobalLimit` dans `src/NetworkLimiter.Contracts/Messages/GlobalLimit.cs` : `enabled`, plafonds `null` ou 10 240 à 1 073 741 824
- [ ] T097 [US3] Implémenter `EffectiveLimitResolver` dans `src/NetworkLimiter.Core/Rules/EffectiveLimitResolver.cs`
- [ ] T098 [US3] Implémenter le gestionnaire `SetGlobalLimit` dans `src/NetworkLimiter.Service/Ipc/Handlers/GlobalLimitHandlers.cs`
- [ ] T099 [US3] Câbler le seau global dans le pipeline, y compris pour les applications **sans règle propre** (FR-009), dans `src/NetworkLimiter.Service/Interception/ShapingPipeline.cs`
- [ ] T100 [US3] Implémenter l'interface du plafond global et de la liste d'exemptions dans `src/NetworkLimiter.App/Views/GlobalLimitView.xaml`, avec affichage de la **règle de résolution appliquée** (FR-010)

**Checkpoint**: US1, US2 et US3 fonctionnent indépendamment.

---

## Phase 6: User Story 4 — Profils et persistance (Priority: P3)

**Goal**: les réglages survivent au redémarrage et se regroupent en profils activables en une
action.

**Independent Test**: créer deux profils aux plafonds différents, redémarrer, vérifier que le
profil actif au moment de l'arrêt est appliqué au démarrage, basculer et vérifier le changement.

### Tests for User Story 4 ⚠️

- [ ] T101 [P] [US4] Écrire les tests d'invariants dans `tests/NetworkLimiter.Core.Tests/Profiles/ProfileInvariantsTests.cs` : `activeProfileId` référence toujours un profil présent, **au moins un profil en permanence** (suppression du dernier refusée), noms uniques sans distinction de casse, 1 à 5 profils, 0 à 200 règles
- [ ] T102 [P] [US4] Écrire le test de bascule dans `tests/NetworkLimiter.Core.Tests/Profiles/ProfileSwitchTests.cs` : retrait intégral de l'ancien jeu **avant** application du nouveau, aucun état intermédiaire où les deux coexistent
- [ ] T103 [P] [US4] Écrire les tests de corruption dans `tests/NetworkLimiter.Service.Tests/Persistence/CorruptConfigTests.cs` : JSON invalide, `schemaVersion` inconnue et invariant violé → **aucune limite appliquée**, jamais d'application partielle des règles lisibles, fichier conservé sous `config.corrupt.<horodatage>.json`, aucune réparation silencieuse
- [ ] T104 [P] [US4] Écrire les tests d'import dans `tests/NetworkLimiter.Service.Tests/Persistence/ImportConfigTests.cs` : revalidation intégrale comme une saisie utilisateur, bornes comprises, **identifiants régénérés**
- [ ] T105 [P] [US4] Écrire le test d'intégration dans `tests/NetworkLimiter.Integration.Tests/Stories/PersistenceTests.cs` : après redémarrage, profil actif appliqué en < 60 s après ouverture de session **sans ouvrir l'interface** (SC-006). Dépend du squelette d'installeur T056

### Implementation for User Story 4

- [ ] T106 [P] [US4] Définir `Profile` et `Config` dans `src/NetworkLimiter.Contracts/Messages/` : `name` 1 à 64 caractères sans caractère de contrôle, `schemaVersion` exactement `1`
- [ ] T107 [US4] Implémenter les gestionnaires `UpsertProfile`, `DeleteProfile`, `SetActiveProfile` dans `src/NetworkLimiter.Service/Ipc/Handlers/ProfileHandlers.cs`
- [ ] T108 [US4] Implémenter la bascule atomique de profil dans `src/NetworkLimiter.Service/Interception/ShapingPipeline.cs`, effet en < 5 s
- [ ] T109 [US4] Configurer le démarrage automatique différé du service et l'application du profil actif sans interface dans `src/NetworkLimiter.Service/Program.cs` (FR-020)
- [ ] T110 [US4] Implémenter la mise en quarantaine et la proposition de restauration dans `src/NetworkLimiter.Service/Persistence/ConfigRecovery.cs` (FR-022)
- [ ] T111 [US4] Implémenter `ImportConfig` et l'export dans `src/NetworkLimiter.Service/Persistence/ConfigTransfer.cs` — l'export ne contient **ni nom de machine, ni nom d'utilisateur, ni mesure de trafic**
- [ ] T112 [US4] Implémenter le sélecteur de profil et la gestion créer/renommer/dupliquer/supprimer dans `src/NetworkLimiter.App/Views/ProfileView.xaml`

**Checkpoint**: US1 à US4 fonctionnent indépendamment.

---

## Phase 7: User Story 5 — Reprise en main (Priority: P3)

**Goal**: tout suspendre en une action, comprendre l'état réel du système, et désinstaller sans
laisser de trace.

**Independent Test**: activer plusieurs limites, déclencher la suspension globale, vérifier le
retour au débit nominal en < 5 s ; puis arrêter brutalement le service et vérifier le même retour.

### Tests for User Story 5 ⚠️

- [ ] T113 [P] [US5] Écrire les tests de suspension dans `tests/NetworkLimiter.Service.Tests/Safety/SuspensionTests.cs` : toutes limites levées en < 5 s, **règles conservées** pour réactivation
- [ ] T114 [P] [US5] Écrire le test d'arrêt brutal dans `tests/NetworkLimiter.Integration.Tests/Safety/AbruptTerminationTests.cs` : `TerminateProcess` répété sur le service → trafic non limité à **100 %** des essais (SC-009)
- [ ] T115 [P] [US5] Écrire le test de raison dans `tests/NetworkLimiter.Contracts.Tests/InactiveReasonTests.cs` : toute règle `Defined` porte **exactement une** raison d'inactivité ; une règle inactive sans raison fait échouer le contrat
- [ ] T116 [P] [US5] Écrire le test de désinstallation dans `tests/NetworkLimiter.Integration.Tests/Installer/UninstallTests.cs` : zéro limite active, service pilote supprimé, `%ProgramData%\NetworkLimiter` supprimé, réseau identique à l'état d'origine (SC-010)

### Implementation for User Story 5

- [ ] T117 [US5] Implémenter `SetSuspended` et le contournement du pipeline dans `src/NetworkLimiter.Service/Safety/SuspensionController.cs` (FR-024)
- [ ] T118 [US5] Implémenter le panneau d'état de santé dans `src/NetworkLimiter.App/Views/HealthView.xaml` : service joignable, pilote chargé, nombre de règles actives, **raison pour chaque règle inactive** (FR-026)
- [ ] T119 [US5] Implémenter la détection d'adaptateur VPN et l'avertissement associé dans `src/NetworkLimiter.Service/Health/VpnDetector.cs` (FR-037, FR-040c) — détecter et avertir, jamais garantir
- [ ] T120 [US5] Implémenter les messages de mode dégradé dans `src/NetworkLimiter.App/ViewModels/HealthViewModel.cs` : pilote absent, système hors matrice, configuration corrompue, chacun avec sa cause et son action corrective
- [ ] T121 [US5] Implémenter l'icône de zone de notification et la suspension rapide dans `src/NetworkLimiter.App/Views/TrayIcon.cs`
- [ ] T122 [US5] Implémenter les conditions de lancement du MSI dans `src/NetworkLimiter.Installer/Conditions.wxs` : **blocage ARM64** avec message nommant l'absence de pilote WinDivert ARM64, blocage x86, blocage build < 19045
- [ ] T123 [US5] Implémenter la séquence de désinstallation ordonnée dans `src/NetworkLimiter.Installer/Uninstall.wxs` : arrêt du service → fermeture des handles → suppression du service pilote → suppression des fichiers → suppression de `%ProgramData%`

**Checkpoint**: les cinq user stories sont fonctionnelles et indépendantes.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: portes de release 6 à 8 de la constitution, durcissement et documentation.

- [ ] T124 [P] Implémenter le banc de mesure à deux VM dans `tools/lab/` : commutateur virtuel interne, machine distante en **`203.0.113.0/24` (TEST-NET-3)** — indispensable pour que la mesure porte sur du trafic que le classificateur considère comme internet (R-012)
- [ ] T125 [P] Implémenter la matrice de mesure dans `tests/NetworkLimiter.Throughput.Tests/ThroughputMatrixTests.cs` : {montant, descendant} × {TCP, UDP} × **{IPv4, IPv6}** × {100 Ko/s, 1 Mo/s, 10 Mo/s, 100 Mo/s}, transfert de 30 s dont les 5 premières écartées, fenêtre glissante de 10 s, tolérance **10 %** — et **20 %** en descendant sans contrôle de congestion (FR-003a, FR-008)
- [ ] T126 [P] Implémenter le contrôle FR-003c dans `tests/NetworkLimiter.Throughput.Tests/NoDropWhenDelayableTests.cs` : compteur de rejets à **zéro** pour tout cas temporisable, échec bloquant sinon
- [ ] T127 [P] Implémenter la comparaison de référence ETW dans `tests/NetworkLimiter.Throughput.Tests/EtwReferenceTests.cs` via `Microsoft-Windows-Kernel-Network`, écart < 10 % (SC-005)
- [ ] T128 [P] Implémenter la matrice d'environnement dans `tests/NetworkLimiter.Integration.Tests/Environment/EnvironmentMatrixTests.cs` : exécution du scénario de limitation avec Hyper-V, WSL et Docker actifs, sur réseau marqué comme mesuré, et après bascule d'adaptateur — chacun des cas dégradés que le principe II élève au rang d'exigence (FR-036)
- [ ] T129 [P] Implémenter le test d'absence de sortie réseau dans `tests/NetworkLimiter.Integration.Tests/Security/NoEgressTests.cs` : capture sur une session complète, **zéro octet émis** par l'outil (SC-012)
- [ ] T130 [P] Implémenter le test de propreté des journaux dans `tests/NetworkLimiter.Integration.Tests/Security/LogScrubbingTests.cs` : aucune adresse IP, aucun nom d'hôte, aucune URL dans les logs produits
- [ ] T131 [P] Implémenter le garde-fou de déterminisme dans `tests/NetworkLimiter.Core.Tests/Guards/DeterminismGuardTests.cs` (catégorie `Determinism`) : échec si `NetworkLimiter.Core` référence un assembly Windows ou si un test appelle `Thread.Sleep` ou l'horloge réelle
- [ ] T132 Implémenter la mesure des budgets de ressources dans `tests/NetworkLimiter.Throughput.Tests/ResourceBudgetTests.cs` (SC-007) : < 2 % de processeur en moyenne, < 150 Mo de mémoire interface ouverte
- [ ] T133 Implémenter la mesure de dégradation à vide dans `tests/NetworkLimiter.Throughput.Tests/IdleOverheadTests.cs` (SC-008) : < 2 % de perte de débit et de latence quand aucune limite n'est active
- [ ] T134 [P] Configurer la signature Authenticode des binaires de release dans `.github/workflows/release.yml`, certificat en **secret de dépôt**, jamais dans l'arborescence
- [ ] T135 [P] Rédiger `SECURITY.md` (politique de divulgation) et compléter `THIRD-PARTY-NOTICES.md`
- [ ] T136 [P] Compléter `README.md` : matrice de compatibilité, limites connues (UDP descendant, VPN, ARM64 non supporté), dépannage
- [ ] T137 Exécuter les neuf scénarios de [quickstart.md](./quickstart.md) et consigner les résultats

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)** : aucune dépendance.
- **Foundational (Phase 2)** : dépend de Phase 1. **Bloque toutes les user stories.**
- **US1 (Phase 3)** : dépend de Phase 2. Aucune dépendance sur une autre story.
- **US2 (Phase 4)** : dépend de Phase 2. Consomme les métriques du pipeline mais reste testable seule.
- **US3 (Phase 5)** : dépend de Phase 2 et du `TokenBucket` de US1 (T063), qu'elle étend par un parent.
- **US4 (Phase 6)** : dépend de Phase 2, squelette d'installeur T056 inclus. **Ne dépend plus de US5.**
- **US5 (Phase 7)** : dépend de Phase 2. T122 et T123 complètent l'installeur créé en T056.
- **Polish (Phase 8)** : dépend des stories souhaitées.

### Within Each User Story

- Les tests sont écrits **et échouent** avant l'implémentation (principe III).
- Primitives pures avant services ; services avant gestionnaires IPC ; gestionnaires avant interface.

### Parallel Opportunities

- Phase 1 : T002 à T013 en parallèle après T001.
- Phase 2 : cinq blocs indépendants — primitives Core (T014–T021), contrat (T022–T025),
  interop WinDivert (T038–T041), résilience réseau (T047–T048), identité de processus
  (T049–T050).
- Phase 3+ : tous les tests marqués [P] d'une story en parallèle ; US2, US3 et US4 en parallèle
  si l'équipe le permet, une fois US1 livrée.

---

## Parallel Example: User Story 1

```bash
# Tests d'abord, tous en parallèle — ils doivent échouer :
Task: "T058 Tests d'invariants du seau à jetons"
Task: "T059 Tests de file de retard et de compteur de rejets"
Task: "T060 Tests de résolution de règle et de matchMode"
Task: "T061 Tests de contrat des messages de règle"
Task: "T062 Test d'intégration d'application de règle"

# Puis les primitives pures, en parallèle :
Task: "T063 TokenBucket"
Task: "T064 DelayQueue"
Task: "T065 Modèles Rule et AppIdentity"
```

---

## Implementation Strategy

### MVP First (US1 seule)

1. Phase 1 — Setup.
2. Phase 2 — Foundational. **Critique** : bloque tout le reste, et représente à elle seule la
   chaîne d'interception. Ce produit n'a pas de raccourci possible ici.
3. Phase 3 — US1.
4. **ARRÊT ET VALIDATION** : scénario 5 de quickstart.md, puis scénario 9 (fail-open). Ne pas
   passer à la suite si une limite survit à l'arrêt du service.
5. Démonstration possible.

### Incremental Delivery

1. Setup + Foundational → socle prêt.
2. + US1 → limitation fonctionnelle (**MVP**).
3. + US2 → l'outil devient utilisable sans deviner quoi limiter.
4. + US3 → plafond global.
5. + US4 → réglages durables et profils.
6. + US5 → confiance : suspension, diagnostic, désinstallation propre.
7. + Polish → portes de release.

**Recommandation** : US5 avant toute diffusion, même à un tiers. Les portes 6 à 8 de la
constitution en dépendent, et un utilisateur qui craint de perdre sa connexion n'installera pas
l'outil.

### Parallel Team Strategy

Setup et Foundational ensemble, puis US1 en priorité par le développeur le plus à l'aise sur le
réseau ; US2 et US4 en parallèle par d'autres ; US3 après T063 ; US5 dès que US1 est stable.

---

## Écarts connus, laissés ouverts

Relevés par `/speckit-analyze` et volontairement non traités à ce stade :

| Réf. | Exigence | Écart |
|------|----------|-------|
| G3 | FR-039b | Le remappage d'une règle vers un nouveau chemin n'a pas de tâche d'interface. À traiter en US2 |
| G4 | FR-038 | Les applications packagées sont déclarées supportées par R-007 mais sans tâche dédiée |
| G5 | SC-014 | Aucune mesure ne prouve qu'un transfert LAN garde son débit sous plafond internet actif |
| G6 | SC-001 | Le critère d'utilisabilité (30 s, 3 interactions) n'est pas vérifié automatiquement |

---

## Notes

- `[P]` = fichiers distincts, aucune dépendance.
- Les tests marqués ⚠️ doivent **échouer** avant implémentation. Un test qui passe du premier
  coup ne prouve rien.
- Commit après chaque tâche ou groupe logique.
- Chaque point de contrôle permet de s'arrêter et de valider une story seule.
- Toute modification du service, du pilote ou de l'IPC référence sa spécification et exige une
  seconde relecture (constitution, règles de contribution).
