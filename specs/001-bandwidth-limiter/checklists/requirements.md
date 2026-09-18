# Specification Quality Checklist: Limiteur de bande passante par application

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-09-18
**Feature**: [spec.md](../spec.md)

## Content Quality

- [x] No implementation details (languages, frameworks, APIs)
- [x] Focused on user value and business needs
- [x] Written for non-technical stakeholders
- [x] All mandatory sections completed

## Requirement Completeness

- [x] No [NEEDS CLARIFICATION] markers remain
- [x] Requirements are testable and unambiguous
- [x] Success criteria are measurable
- [x] Success criteria are technology-agnostic (no implementation details)
- [x] All acceptance scenarios are defined
- [x] Edge cases are identified
- [x] Scope is clearly bounded
- [x] Dependencies and assumptions identified

## Feature Readiness

- [x] All functional requirements have clear acceptance criteria
- [x] User scenarios cover primary flows
- [x] Feature meets measurable outcomes defined in Success Criteria
- [x] No implementation details leak into specification

## Notes

**Validation : 16/16.** Spec prête pour `/speckit-plan`.

**Clarifications résolues avec le porteur du projet (2026-09-18)** :

| Réf. | Question | Décision |
|------|----------|----------|
| FR-039 | Critère d'identification d'une application | Chemin complet normalisé, avec repli sur le nom de l'exécutable, signalement visible du repli et proposition de remappage |
| FR-040 | Périmètre du trafic comptabilisé | Trafic internet uniquement ; LAN, boucle locale, lien-local, multicast et diffusion exclus |
| FR-034 | Droits d'un utilisateur standard | Consultation libre (monitoring, règles, santé) ; toute modification exige une élévation UAC ponctuelle |

Chaque décision a été propagée en exigences dérivées (FR-034a→c, FR-039a→c, FR-040a→c), en cas
limites, en hypothèses documentées et en critères de succès vérifiables (SC-013 à SC-015).

**Points de vigilance conservés pour `/speckit-plan`** :

- L'architecture « composant privilégié permanent + interface non élevée » figure dans les
  hypothèses, pas dans les exigences : c'est une contrainte décidée en amont par le porteur du
  projet, volontairement conservée car elle conditionne FR-029 à FR-031 et FR-034a. Les
  exigences restent formulées en termes observables.
- FR-035 à FR-038 imposent de *déclarer* un comportement (matrice de compatibilité, VPN,
  applications packagées) sans en fixer la valeur. L'obligation de trancher et de documenter est
  testable ; les valeurs concrètes relèvent du plan.
- FR-040c (fiabilité de la distinction internet/local sous VPN) et FR-037 se recoupent : le plan
  doit traiter le VPN une seule fois, de façon cohérente.
- FR-003 (écart ≤ 10 %) et SC-002 supposent une méthode de mesure de référence : **résolu** par
  le plan, entrée R-012 (banc à deux VM en TEST-NET-3, référence ETW).

**Amendement du 2026-09-18, après `/speckit-plan`** : FR-003 promettait 10 % d'écart sans
distinguer sens ni protocole, objectif intenable en descendant sans contrôle de congestion — le
paquet est déjà arrivé, seul le rejet peut tenir le plafond. L'exigence a été qualifiée
(FR-003 à FR-003c, SC-002 et SC-002a) : 10 % là où la temporisation suffit, 20 % par rejet
ailleurs, rejet signalé et compté dans l'interface, et interdiction de rejeter quand la
temporisation suffisait. La spec promet désormais ce que le produit peut tenir.
