# Contrat — Protocole IPC service ↔ interface

**Feature**: `001-bandwidth-limiter` | **Version du protocole**: 1 | **Date**: 2026-09-18

Contrat entre `NetworkLimiter.Service` (serveur, `LocalSystem`) et `NetworkLimiter.App` (client,
session utilisateur). Il est la **frontière de privilège** du produit : tout ce qui le franchit
est hostile jusqu'à preuve du contraire. Les tests de contrat de
`NetworkLimiter.Contracts.Tests` vérifient chaque règle énoncée ici, des deux côtés.

---

## Transport

| Propriété | Valeur |
|-----------|--------|
| Type | Tuyau nommé Windows, message-mode |
| Nom | `\\.\pipe\NetworkLimiter.v1` |
| Instances simultanées | 4 maximum |
| Cadrage | Préfixe de longueur sur 4 octets, entier non signé, petit-boutiste |
| Charge utile | JSON UTF-8, sans BOM |
| Taille maximale d'un message | **65 536 octets**. Au-delà : connexion fermée sans lecture |
| Délai d'inactivité | 30 s côté serveur |

**Interdits** : tout socket TCP, y compris sur la boucle locale (constitution, section
contraintes techniques).

---

## Contrôle d'accès

Trois couches indépendantes. Chacune doit tenir seule ; leur cumul est délibéré.

### 1. ACL du tuyau

| Entité | Droits |
|--------|--------|
| `NT AUTHORITY\SYSTEM` | Contrôle total |
| `BUILTIN\Administrators` | Lecture + écriture |
| `BUILTIN\Users` | Lecture + écriture (le filtrage se fait à la couche 2) |
| **`NT AUTHORITY\NETWORK` (S-1-5-2)** | **REFUS EXPLICITE** |
| `ANONYMOUS LOGON` | **REFUS EXPLICITE** |

L'ACE de refus sur `NETWORK` n'est pas décorative : un tuyau nommé est joignable à distance par
SMB via `\\machine\pipe\nom`. Sans elle, le canal de contrôle d'un service `LocalSystem` serait
exposé au réseau. Les ACE de refus précèdent les ACE d'autorisation dans la DACL.

### 2. Autorisation par message

Le serveur impersonne l'appelant (`NamedPipeServerStream.RunAsClient`) et évalue
`WindowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator)`.

| Catégorie | Élévation requise | Messages |
|-----------|-------------------|----------|
| Lecture | Non | `Hello`, `GetState`, `SubscribeMetrics`, `GetHealth` |
| Écriture | **Oui** | `UpsertRule`, `DeleteRule`, `SetRuleEnabled`, `SetGlobalLimit`, `SetActiveProfile`, `UpsertProfile`, `DeleteProfile`, `SetSuspended`, `ImportConfig` |

Sous UAC, le jeton filtré d'un administrateur **non élevé** ne porte pas le SID Administrateurs
activé : `IsInRole` renvoie `false`. Le comportement exigé par FR-034 découle donc du modèle de
sécurité de Windows, sans logique d'autorisation maison. Le serveur ne mémorise **jamais** qu'un
appelant a été autorisé : chaque message est évalué seul.

### 3. Validation du contenu

- Discriminant `type` obligatoire, comparé à une **liste blanche** de constantes.
- Désérialisation par **générateur de source** `System.Text.Json`. La désérialisation
  polymorphe et les convertisseurs par réflexion sont interdits.
- Chaque champ numérique est borné avant usage ; chaque chaîne a une longueur maximale.
- Aucun champ reçu n'est utilisé pour construire un chemin de fichier, une commande ou une
  requête sans validation préalable.
- Champ inconnu → **rejet du message**, pas ignoré silencieusement.

---

## Cadre des messages

```json
{ "type": "<Discriminant>", "id": "<GUID de corrélation>", "payload": { } }
```

Réponse :

```json
{ "type": "<Discriminant>Result", "id": "<même GUID>", "ok": true, "payload": { } }
```

Erreur :

```json
{ "type": "Error", "id": "<même GUID>", "ok": false,
  "error": { "code": "<CodeÉnuméré>", "message": "<texte affichable>" } }
```

### Codes d'erreur

| Code | Sens | Réaction attendue de l'interface |
|------|------|----------------------------------|
| `ElevationRequired` | Commande d'écriture sans élévation | Proposer la relance élevée |
| `ProtocolVersionMismatch` | Versions incompatibles | Message de mise à jour, déconnexion |
| `ValidationFailed` | Champ hors bornes ou inconnu | Signaler le champ fautif |
| `NotFound` | Identifiant inexistant | Rafraîchir l'état |
| `ConflictingRule` | Règle déjà définie pour cette cible | Proposer d'éditer l'existante |
| `InterceptionUnavailable` | Pilote absent ou handle fermé | Afficher l'état de santé |
| `ConfigCorrupt` | Configuration illisible | Proposer la restauration |
| `InternalError` | Défaillance inattendue | Inviter à consulter le journal |

---

## Poignée de main

**Premier message obligatoire.** Toute autre requête avant `Hello` ferme la connexion.

```json
{ "type": "Hello", "id": "…", "payload": { "protocolVersion": 1, "clientVersion": "1.0.0" } }
```

```json
{ "type": "HelloResult", "id": "…", "ok": true,
  "payload": { "protocolVersion": 1, "serviceVersion": "1.0.0", "elevated": false } }
```

**Invariants** — chacun est un test de contrat :

- `protocolVersion` différent → `ProtocolVersionMismatch` et fermeture. Le service **refuse
  proprement** une interface incompatible avec un message actionnable, jamais un échec silencieux
  (constitution, règles de contribution).
- `elevated` reflète le jeton **réel** de l'appelant, jamais une valeur qu'il a déclarée.
- Le client ne peut pas se prétendre élevé : le champ est calculé côté serveur.

---

## Messages de lecture

### `GetState`

Retourne le profil actif, la liste complète des profils, leurs règles, le plafond global et, pour
chaque règle, son état d'application et sa raison d'inactivité.

```json
{ "type": "GetStateResult", "ok": true, "payload": {
  "activeProfileId": "…",
  "profiles": [ { "id": "…", "name": "Télétravail",
    "globalLimit": { "enabled": true, "downloadBytesPerSecond": 5242880,
                     "uploadBytesPerSecond": null },
    "rules": [ { "id": "…",
      "target": { "executablePath": "c:\\program files\\steam\\steam.exe",
                  "executableName": "steam.exe", "displayName": "Steam" },
      "downloadBytesPerSecond": 1048576, "uploadBytesPerSecond": null,
      "enabled": true, "exemptFromGlobal": false,
      "status": "Active", "inactiveReason": null,
      "matchMode": "ExactPath", "matchedProcessCount": 3 } ] } ] } }
```

- `status` : `Active` | `Defined`
- `inactiveReason` : `null` si `Active`, sinon **obligatoirement** l'une des valeurs de
  `HealthState` (voir [data-model.md](../data-model.md)). **[TEST]** une règle `Defined` sans
  raison est un échec de contrat.
- `matchMode` : `ExactPath` | `FallbackName` — FR-039c interdit un appariement par repli
  silencieux.

### `GetHealth`

```json
{ "type": "GetHealthResult", "ok": true, "payload": {
  "interceptionActive": true, "driverLoaded": true, "suspended": false,
  "activeRuleCount": 3, "queuePressure": 0.02,
  "vpnDetected": false, "configWarning": null,
  "compatibility": { "supported": true, "osBuild": 26100, "architecture": "x64" } } }
```

### `SubscribeMetrics`

Ouvre un flux de messages `MetricsTick` non sollicités, à 1 Hz (FR-013).

```json
{ "type": "MetricsTick", "payload": {
  "timestamp": "2026-09-18T14:03:01Z",
  "totalDownloadBytesPerSecond": 8388608, "totalUploadBytesPerSecond": 524288,
  "apps": [ { "executablePath": "c:\\…\\steam.exe", "displayName": "Steam",
              "downloadBytesPerSecond": 1048576, "uploadBytesPerSecond": 12288,
              "effectiveDownloadLimit": 1048576, "effectiveUploadLimit": null,
              "processCount": 3, "droppedPackets": 0 } ] } }
```

**Invariants**

- **[TEST]** Aucun champ ne porte d'adresse distante, de nom d'hôte, d'URL ou de port distant.
  FR-018 et le principe VI sont vérifiés par un test qui inspecte le JSON sérialisé.
- **[TEST]** `effectiveDownloadLimit` est le plafond **résolu** (`min` règle/global), pas celui
  saisi : FR-010 exige que l'utilisateur voie la contrainte réellement appliquée.
- **[TEST]** Les débits reportés excluent le trafic local et la boucle locale, en cohérence avec
  les plafonds (FR-040b).

---

## Messages d'écriture

Tous exigent l'élévation. Tous sont **idempotents sur l'identifiant** : rejouer le même message
ne produit pas d'effet supplémentaire.

| Message | Charge utile | Validation |
|---------|--------------|------------|
| `UpsertRule` | `ruleId?`, `target`, plafonds, `enabled`, `exemptFromGlobal` | Bornes 10 240 – 1 073 741 824 ou `null` ; chemin normalisable ; pas de doublon de cible |
| `DeleteRule` | `ruleId` | Existence |
| `SetRuleEnabled` | `ruleId`, `enabled` | Existence |
| `SetGlobalLimit` | `enabled`, plafonds | Mêmes bornes |
| `UpsertProfile` | `profileId?`, `name` | Nom 1–64 caractères, unique |
| `DeleteProfile` | `profileId` | Refus si c'est le dernier profil |
| `SetActiveProfile` | `profileId` | Existence |
| `SetSuspended` | `suspended` | — |
| `ImportConfig` | `config` complet | Schéma complet revalidé comme une saisie utilisateur |

**Invariants d'écriture**

- **[TEST]** Toute écriture est **transactionnelle du point de vue de l'appelant** : validation
  complète, puis application en mémoire, puis persistance atomique. Un échec à n'importe quelle
  étape laisse l'état exactement tel qu'avant — ce qui couvre le cas limite « élévation refusée
  en milieu de modification » de la spec.
- **[TEST]** Une écriture réussie est suivie d'une diffusion `StateChanged` à **tous** les
  clients connectés, y compris non élevés, pour que les vues en lecture seule restent à jour.
- **[TEST]** `ImportConfig` ne fait confiance à rien : un fichier importé est validé exactement
  comme une saisie, bornes comprises. C'est un vecteur d'entrée au même titre que le reste.

---

## Compatibilité de version

| Situation | Comportement |
|-----------|--------------|
| Même `protocolVersion` | Connexion acceptée |
| Versions différentes | `ProtocolVersionMismatch`, connexion fermée, interface affiche une action corrective |
| Champ inconnu reçu | Message rejeté (`ValidationFailed`) |

Le protocole ne pratique **pas** la tolérance aux champs inconnus. Sur une frontière de privilège,
ignorer ce qu'on ne comprend pas est un risque, pas une souplesse : un champ ignoré est un champ
dont on ne sait pas s'il devait changer le comportement.

Un changement de contrat impose d'incrémenter `protocolVersion` **et** de modifier
`NetworkLimiter.Contracts`, ce qui casse la compilation des deux côtés — l'incompatibilité est
détectée à la compilation plutôt qu'à l'exécution.

---

## Tests de contrat obligatoires

1. Requête avant `Hello` → connexion fermée.
2. `protocolVersion` incompatible → `ProtocolVersionMismatch`.
3. Chaque message d'écriture émis sans élévation → `ElevationRequired`, **et aucun changement
   d'état persisté**.
4. Chaque borne numérique testée à `min-1`, `min`, `max`, `max+1`.
5. Message de 65 537 octets → connexion fermée sans désérialisation.
6. JSON malformé, tronqué, à profondeur excessive, avec clés dupliquées → `ValidationFailed`,
   jamais d'exception non gérée.
7. Champ inconnu → `ValidationFailed`.
8. Client se déclarant `elevated: true` sans l'être → valeur ignorée, écritures refusées.
9. Connexion depuis le SID `NETWORK` → refusée par l'ACL, vérifié par un test d'ACL.
10. Sérialisation de `MetricsTick` et `GetStateResult` → aucune adresse, aucun nom d'hôte.
11. Règle `Defined` → `inactiveReason` toujours renseignée.
12. Écriture en échec à mi-parcours → état inchangé, aucun fichier partiellement écrit.
