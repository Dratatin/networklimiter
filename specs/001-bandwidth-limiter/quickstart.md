# Guide de validation — Limiteur de bande passante

**Feature**: `001-bandwidth-limiter` | **Date**: 2026-09-18 | **Plan**: [plan.md](./plan.md)

Comment vérifier que la fonctionnalité marche vraiment. Ce document décrit des scénarios
**exécutables** et leurs résultats attendus ; il ne contient pas de code d'implémentation.

Les scénarios 1 à 4 tournent sur une machine de développement. Les scénarios 5 à 9 exigent le
banc de mesure ou une VM propre — ce sont les portes de release 6 à 8 de la constitution.

---

## Prérequis

**Développement**

- SDK .NET 10
- Windows 11 x64 ou Windows 10 22H2 x64 (**pas ARM64** : aucun pilote WinDivert n'existe)
- WiX v5 en outil global, pour l'installeur
- `tools/restore-windivert.ps1` — télécharge WinDivert 2.2.2 et **vérifie son empreinte et sa
  signature Authenticode** avant de le placer dans l'arborescence de build. Un binaire noyau dont
  on n'a pas vérifié la provenance n'a rien à faire dans un build.

**Banc de mesure** (scénarios 6 et 7)

- Deux VM Hyper-V sur un commutateur virtuel **interne**
- VM sous test : l'application installée
- VM distante : générateur de charge, adresse dans **`203.0.113.0/24`** (TEST-NET-3)

Le choix de cette plage n'est pas cosmétique. Le classificateur exclut les plages privées des
plafonds ([R-006](./research.md#r-006--distinction-internet--réseau-local-fr-040)) : un banc
monté en `192.168.x.x` mesurerait un trafic que le produit a décidé par conception de ne pas
limiter, et tous les tests passeraient sans rien prouver. TEST-NET-3 est vu comme « internet »
par le classificateur tout en restant confiné au laboratoire.

---

## Scénario 1 — Tests unitaires (aucun privilège requis)

```powershell
dotnet test tests/NetworkLimiter.Core.Tests
```

**Attendu** : tout au vert, en moins de 10 secondes.

**Ce que ça prouve** : la logique de mise en forme tient le débit cible à 10 % près en temps
simulé, les bornes sont respectées, la classification internet/local est exacte sur les bornes de
chaque plage, la normalisation de chemin est idempotente.

**Contrôle de conformité (principe III)** :

```powershell
dotnet test tests/NetworkLimiter.Core.Tests --filter Category=Determinism
```

Échoue si `NetworkLimiter.Core` référence un assembly Windows ou si un test appelle
`Thread.Sleep` ou l'horloge réelle. Ce garde-fou empêche la dérive lente vers des tests lents
puis instables puis désactivés.

---

## Scénario 2 — Contrat IPC

```powershell
dotnet test tests/NetworkLimiter.Contracts.Tests
```

**Attendu** : les 12 tests de contrat de [ipc-protocol.md](./contracts/ipc-protocol.md) passent.

**Ce que ça prouve** : le protocole rejette les messages malformés, surdimensionnés, hors bornes
ou porteurs de champs inconnus ; les écritures sans élévation sont refusées **et ne laissent
aucune trace persistée** ; aucune adresse distante ne fuit dans les messages de métriques.

---

## Scénario 3 — Service en console

```powershell
dotnet run --project src/NetworkLimiter.Service -- --console
```

À lancer depuis une invite **administrateur** : l'ouverture des handles WinDivert l'exige.

**Attendu** : le journal affiche la vérification de compatibilité, le chargement de la
configuration, l'ouverture des deux handles, puis le passage en `Shaping`.

**Contrôle fail-open immédiat (principe IV)** : `Ctrl+C`, puis relancer un téléchargement.
Le débit doit revenir au nominal. Si une limite persiste après l'arrêt du service, c'est un
défaut **bloquant** — pas une anomalie cosmétique.

---

## Scénario 4 — Interface, mode lecture seule

```powershell
dotnet run --project src/NetworkLimiter.App
```

À lancer depuis une invite **non élevée**.

**Attendu** :

- la liste des applications actives se remplit, rafraîchie à 1 Hz ;
- les commandes de modification sont **visibles mais désactivées**, avec la raison affichée
  (FR-034b) ;
- un clic sur « Modifier » déclenche **une** invite UAC ; l'instance élevée prend le relais et
  les commandes deviennent actives (FR-034a, FR-034c) ;
- annuler l'invite UAC laisse l'interface en lecture seule, sans état à moitié modifié.

---

## Scénario 5 — Limitation de bout en bout (US1)

1. Lancer un téléchargement volumineux dans une application.
2. La repérer dans la liste, appliquer 1 Mo/s en descente.
3. Observer.

**Attendu** : le débit passe sous 1 Mo/s en moins de 5 secondes, **sans que le téléchargement se
coupe**. Les autres applications gardent leur débit. Retirer la règle rend le débit nominal en
moins de 5 secondes.

---

## Scénario 6 — Mesure de débit (FR-003, SC-002) — porte de release 6

```powershell
dotnet test tests/NetworkLimiter.Throughput.Tests -- --lab-remote 203.0.113.10
```

**Protocole** : transfert de 30 s, 5 premières secondes écartées (convergence TCP), débit relevé
par fenêtre glissante de 10 s.

**Matrice** : {montant, descendant} × {TCP, UDP} × {100 Ko/s, 1 Mo/s, 10 Mo/s, 100 Mo/s}, plus la
même chose sous plafond global avec trois transferts simultanés (SC-004).

**Attendu** : moyenne dans les 10 % du plafond.

**Cas UDP descendant** — seuil distinct, **bloquant lui aussi**. Le paquet est déjà arrivé sur la
machine ; le retenir ne libère pas la ligne, et UDP n'a aucune boucle de rétroaction pour faire
refluer l'émetteur. Le plafond s'y obtient par rejet : la tolérance est de **20 %** (FR-003a) et
le test vérifie en plus que la proportion de rejets est bien remontée jusqu'à l'interface
(FR-003b).

**Contrôle FR-003c** : pour chaque cas *temporisable* de la matrice — tout le montant, et le
descendant TCP — le compteur de rejets doit rester à **zéro**. Un rejet observé là où la
temporisation suffisait est un échec bloquant : cela signifierait que le produit dégrade le
trafic alors qu'il pouvait simplement le ralentir.

---

## Scénario 7 — Mesure de référence indépendante (SC-005)

Comparer les débits affichés par l'interface aux compteurs du fournisseur ETW
`Microsoft-Windows-Kernel-Network`, sur la même période.

**Attendu** : écart inférieur à 10 %.

**Pourquoi ETW** : Windows n'expose aucun compteur de performance d'octets par processus. Sans
source indépendante, on vérifierait le monitoring avec lui-même — un test qui ne peut pas échouer.

---

## Scénario 8 — Cycle de vie complet sur machine propre — porte de release 7

Sur une VM sans l'outil, à partir d'un instantané propre :

1. Installer le MSI. **Attendu** : le service démarre, le pilote est enregistré à
   l'installation — pas silencieusement au premier usage
   ([R-011](./research.md#r-011--installation-du-pilote-et-désinstallation-fr-027)).
2. Créer deux profils avec des plafonds différents.
3. Redémarrer. **Attendu** : le profil actif est appliqué en moins de 60 s après l'ouverture de
   session, sans ouvrir l'interface (FR-020, SC-006).
4. Désinstaller. **Attendu** : zéro limite active, service pilote supprimé, `%ProgramData%\
   NetworkLimiter` supprimé, réseau identique à l'état d'origine (FR-027, SC-010).

**Contrôle d'architecture** : lancer le même MSI sur une VM **ARM64**. Attendu : refus à
l'installation, avec un message nommant l'absence de pilote WinDivert ARM64 — pas une
installation qui aboutit sur un outil incapable de limiter.

---

## Scénario 9 — Fail-open sous contrainte — porte de release 8

| Épreuve | Attendu |
|---------|---------|
| `Stop-Service NetworkLimiter` | Trafic non limité en < 5 s |
| `Stop-Process -Force` sur le service | Trafic non limité, interface signale l'interruption |
| Pilote absent au démarrage | Démarrage en mode dégradé, cause affichée, aucune limite |
| Configuration corrompue | Aucune limite, avertissement, restauration proposée (FR-022) |
| Saturation de la file de paquets | Handle fermé, trafic libéré, pression consignée (R-004) |
| Suspension globale | Toutes limites levées en < 5 s, règles conservées (FR-024) |

**Attendu global** : dans chacun de ces états, un téléchargement atteint le débit nominal de la
ligne. Aucune épreuve ne doit laisser du trafic étranglé sans processus vivant pour le libérer.

---

## Contrôles de sécurité

| Contrôle | Commande / méthode | Attendu |
|----------|--------------------|---------|
| ACL de la configuration (SC-013) | Écriture tentée sous un compte standard | Accès refusé |
| Refus réseau sur le tuyau | Connexion à `\\<machine>\pipe\NetworkLimiter.v1` depuis une autre machine | Refusée |
| Aucune sortie réseau (SC-012) | Capture Wireshark sur une session complète | Zéro octet émis par l'outil |
| Journaux sans donnée personnelle | Recherche d'adresses IP et de noms d'hôtes dans les logs | Aucune occurrence |
| Dépendances vulnérables | `dotnet list package --vulnerable --include-transitive` | Aucune vulnérabilité haute ou critique |
| Binaires signés | `Get-AuthenticodeSignature` sur les binaires de release | Signature valide |

---

## Correspondance scénarios → exigences

| Scénario | Couvre |
|----------|--------|
| 1 | FR-003 (logique), FR-039, FR-040, principe III |
| 2 | FR-031, FR-034, FR-018, principe I |
| 3 | FR-025, FR-035, principe IV |
| 4 | FR-013, FR-029, FR-034a-c |
| 5 | US1 — FR-001 à FR-004, FR-006 |
| 6 | FR-003, FR-009, FR-010, SC-002, SC-004 |
| 7 | FR-013, SC-005 |
| 8 | FR-019 à FR-021, FR-027, FR-035, SC-006, SC-010, SC-011 |
| 9 | FR-022, FR-024, FR-025, FR-026, SC-009 |
| Sécurité | FR-030 à FR-034, SC-012, SC-013 |
