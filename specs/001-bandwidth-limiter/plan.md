# Implementation Plan: Limiteur de bande passante par application

**Branch**: `001-bandwidth-limiter` | **Date**: 2026-09-18 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/001-bandwidth-limiter/spec.md`

## Summary

Limiter le débit montant et descendant des applications Windows, par application et globalement,
avec monitoring temps réel et règles persistantes.

L'approche technique : un **service Windows** en `LocalSystem` détient deux handles WinDivert —
l'un sur la couche FLOW pour attribuer chaque flux réseau à un processus, l'autre sur la couche
NETWORK pour retenir et réinjecter les paquets selon un **seau à jetons hiérarchique** (règle
d'application enfant du plafond global). Une **interface WPF non élevée** dialogue avec lui par
un tuyau nommé dont l'ACL refuse explicitement l'accès réseau, et n'obtient le droit d'écrire
qu'en se relançant élevée. La configuration vit dans `%ProgramData%` sous ACL restrictives, qui
constituent le véritable verrou de FR-034 — pas l'interface.

Toute la logique de calcul est isolée dans une bibliothèque sans dépendance à Windows, pilotée
par une horloge injectée, conformément au principe III.

Détail des arbitrages et des sources : [research.md](./research.md).

## Technical Context

**Language/Version**: C# 13 / .NET 10 LTS (support jusqu'au 14/11/2028).
**Ce n'est pas .NET 8** : voir [R-001](./research.md#r-001--version-du-runtime-net), .NET 8 sort
de support le 10/11/2026. Constitution amendée en conséquence (1.0.1).

**Primary Dependencies**:

| Dépendance | Version | Rôle | Licence |
|------------|---------|------|---------|
| WinDivert | 2.2.2 (épinglée) | Interception et réinjection de paquets | LGPL v3 / GPL v2 |
| Microsoft.Extensions.Hosting.WindowsServices | 10.x | Hébergement du service | MIT |
| System.Text.Json (générateur de source) | intégré | Protocole IPC et configuration | MIT |
| Serilog + sink fichier rotatif | 4.x | Journalisation structurée (principe VI) | Apache-2.0 |
| CommunityToolkit.Mvvm | 8.x | MVVM de l'interface, testable sans WPF | MIT |
| WiX Toolset | v5 | Installeur MSI | MS-RL |
| xUnit + FluentAssertions | 2.x / 7.x | Tests | Apache-2.0 / Apache-2.0 |

Aucune dépendance réseau, aucun service tiers, aucune télémétrie.

**Storage**: fichier `%ProgramData%\NetworkLimiter\config.json`, écriture atomique,
`schemaVersion` explicite. ACL : `SYSTEM` + `Administrators` en contrôle total, `Users` en
lecture seule. Pas de base de données.

**Testing**: xUnit. Trois niveaux — unitaires (logique pure, horloge injectée, aucun
`Thread.Sleep`), contrat (protocole IPC vérifié des deux côtés), intégration (cycle de vie
complet sur VM). Plus un banc de mesure de débit à deux VM, décrit en
[R-012](./research.md#r-012--méthode-de-mesure-de-débit-fr-003-sc-002-sc-005).

**Target Platform**:

| Cible | Statut |
|-------|--------|
| Windows 11 21H2 (22000) et ultérieur, x64 | Supporté, testé |
| Windows 10 22H2 (19045), x64 | Supporté, testé — candidat à l'abandon (fin de servicing 14/10/2025) |
| ARM64 | **Non supporté**, bloqué à l'installation : WinDivert ne publie aucun pilote ARM64 |
| x86 32 bits | Non supporté, bloqué à l'installation |
| Windows Server | Hors périmètre |

**Project Type**: application de bureau Windows — service privilégié + interface graphique +
bibliothèques partagées.

**Performance Goals**: écart ≤ 10 % du plafond sur fenêtre glissante de 10 s (FR-003), ≤ 20 %
en descendant sans contrôle de congestion, où le plafond est tenu par rejet compté (FR-003a) ;
règle
appliquée en < 5 s (FR-003, SC-003) ; rafraîchissement du monitoring ≥ 1 Hz (FR-013) ;
< 2 % de processeur et < 150 Mo de mémoire en usage normal (SC-007) ; < 2 % de dégradation de
débit et de latence quand aucune limite n'est active (SC-008).

**Constraints**: hors ligne intégral ; interface jamais élevée au démarrage ; fail-open absolu ;
aucune donnée de destination journalisée ; désinstallation sans résidu.

**Scale/Scope**: poste personnel. Dimensionnement retenu : jusqu'à 200 règles, 5 profils,
2 000 flux simultanés, 50 applications actives affichées.

## Constitution Check

*Vérifié contre la constitution v1.0.1. Gate Phase 0 : **PASSÉ**. Re-vérifié après Phase 1 :
**PASSÉ**.*

| Principe | Comment le plan s'y conforme | Vérifié par |
|----------|------------------------------|-------------|
| **I. Moindre privilège** | Interface sans manifeste d'élévation, en lecture seule ; seul le service détient les handles WinDivert ; ACL du tuyau avec refus explicite du SID `NETWORK` (S-1-5-2) ; autorisation par impersonation de l'appelant ; validation de chaque message contre une liste blanche de types, bornes et taille max ; ACL restrictives sur `%ProgramData%` | Tests de contrat IPC, SC-013 |
| **II. Compatibilité** | Matrice déclarée ci-dessus et vérifiée au démarrage du service ; ARM64 et x86 bloqués à l'installation avec message nommant la cause ; VPN, adaptateurs virtuels, IPv6, veille/reprise traités comme exigences | Tests d'intégration sur VM, SC-011 |
| **III. Test-First** | `NetworkLimiter.Core` sans référence à Windows, `ISystemClock` injectée, aucun `Thread.Sleep` ; contrat IPC testé des deux côtés ; banc de débit reproductible avant chaque release | Gates 2, 3 et 6 de la constitution |
| **IV. Fail-open** | Fermeture du handle NETWORK sur défaillance, arrêt, crash ou pression de file excessive ; flux inconnu réinjecté sans limitation ; suspension globale ; séquence de désinstallation ordonnée et testée ; blocage total absent du code | SC-009, SC-010, gate 8 |
| **V. Simplicité** | Quatre projets de production, pas de couche d'abstraction sans second implémenteur, dépendances justifiées dans le tableau ci-dessus, fonctionnalités hors v1 absentes du modèle | Revue |
| **VI. Observabilité** | Serilog vers fichier rotatif borné + journal d'événements Windows pour le critique ; état de santé exposé par l'IPC avec cause d'inactivité par règle ; aucune adresse ni nom d'hôte journalisé ; aucune sortie réseau | SC-012, revue des messages de log |

**Aucune violation à justifier.** La section Complexity Tracking reste donc vide.

Les deux points relevés par la recherche ont été tranchés le 18/09/2026 :

1. **FR-003 en descendant sans contrôle de congestion** : la spécification a été qualifiée
   (FR-003 à FR-003c, SC-002a). Le plafond y est tenu par rejet, avec une tolérance de 20 %, et
   le rejet est **signalé et compté** dans l'interface. FR-003c interdit le rejet quand la
   temporisation suffit, ce qui devient un invariant testable
   ([R-005](./research.md#r-005--algorithme-de-mise-en-forme)).
2. **Ventilation par application sous VPN** : selon le point d'interception du client VPN, elle
   peut devenir inopérante. FR-037 et FR-040c sont satisfaits par la **détection et
   l'avertissement**, pas par la garantie
   ([R-006](./research.md#r-006--distinction-internet--réseau-local-fr-040)).

## Project Structure

### Documentation (this feature)

```text
specs/001-bandwidth-limiter/
├── plan.md              # Ce fichier
├── spec.md              # Spécification fonctionnelle
├── research.md          # Phase 0 — arbitrages techniques
├── data-model.md        # Phase 1 — entités, invariants, transitions
├── quickstart.md        # Phase 1 — guide de validation exécutable
├── contracts/
│   ├── ipc-protocol.md  # Contrat du tuyau nommé
│   └── config-schema.md # Contrat du fichier de configuration
├── checklists/
│   └── requirements.md  # Qualité de la spec
└── tasks.md             # Phase 2 — produit par /speckit-tasks
```

### Source Code (repository root)

```text
src/
├── NetworkLimiter.Core/              # Logique pure — AUCUNE référence à Windows
│   ├── Shaping/                      # Seau à jetons, hiérarchie, file de retard
│   ├── Rules/                        # Résolution règle↔application, plafond effectif
│   ├── Classification/               # Internet vs local, normalisation de chemin
│   ├── Metrics/                      # Agrégation de débit, fenêtre glissante
│   └── Time/                         # ISystemClock et implémentations
│
├── NetworkLimiter.Contracts/         # Messages IPC et modèle de configuration
│   ├── Messages/                     # Types de requête et de réponse
│   └── Serialization/                # Contexte de générateur de source JSON
│
├── NetworkLimiter.Service/           # Service Windows, LocalSystem
│   ├── Interception/                 # P/Invoke WinDivert, handles FLOW et NETWORK
│   ├── FlowTable/                    # Quintuplet → PID, purge, garde anti-réutilisation
│   ├── ProcessIdentity/              # PID → chemin, cache (PID, heure de démarrage)
│   ├── Ipc/                          # Serveur de tuyau, ACL, autorisation
│   ├── Persistence/                  # Lecture/écriture atomique, ACL, corruption
│   ├── Health/                       # État de santé, pression de file, détection VPN
│   └── Safety/                       # Chien de garde, arrêt fail-open
│
├── NetworkLimiter.App/               # Interface WPF, NON élevée
│   ├── Views/
│   ├── ViewModels/                   # Testables sans WPF
│   ├── Ipc/                          # Client de tuyau
│   └── Elevation/                    # Relance élevée via runas
│
└── NetworkLimiter.Installer/         # WiX v5 — MSI, pilote, conditions d'architecture

tests/
├── NetworkLimiter.Core.Tests/        # Unitaires, déterministes, horloge injectée
├── NetworkLimiter.Contracts.Tests/   # Contrat IPC des deux côtés
├── NetworkLimiter.Service.Tests/     # Service, avec WinDivert simulé
├── NetworkLimiter.App.Tests/         # ViewModels
├── NetworkLimiter.Integration.Tests/ # Cycle de vie complet, exécution sur VM
└── NetworkLimiter.Throughput.Tests/  # Banc de mesure à deux VM (R-012)

tools/
├── restore-windivert.ps1             # Téléchargement + vérification d'empreinte et de signature
└── lab/                              # Provisionnement des VM du banc de mesure
```

**Structure Decision**: quatre projets de production, séparés par **frontière de privilège** et
non par couche technique. `Core` ne référence pas Windows, ce qui rend le principe III
mécaniquement vérifiable : si un `using` Windows y apparaît, la compilation d'un test le
détecte. `Contracts` est partagé entre le service et l'interface, ce qui garantit qu'un
changement de protocole casse la compilation des deux côtés plutôt que de produire une
incompatibilité à l'exécution. `Service` concentre tout le code privilégié et tout le P/Invoke.
`App` ne contient aucun code capable de modifier l'état du système autrement qu'en envoyant un
message au service.

## Phases

- **Phase 0 — Recherche** : terminée. Voir [research.md](./research.md), 12 entrées, toutes les
  inconnues résolues.
- **Phase 1 — Conception** : terminée. Voir [data-model.md](./data-model.md),
  [contracts/](./contracts/), [quickstart.md](./quickstart.md).
- **Phase 2 — Tâches** : produite par `/speckit-tasks`, non couverte par ce document.

## Complexity Tracking

> Aucune violation de la constitution à justifier. Section laissée vide intentionnellement.
