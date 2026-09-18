# Contrat — Fichier de configuration

**Feature**: `001-bandwidth-limiter` | **schemaVersion**: 1 | **Date**: 2026-09-18

Emplacement : `%ProgramData%\NetworkLimiter\config.json`

Ce fichier est un **contrôle de sécurité**, pas un simple fichier de réglages. Ses ACL sont le
verrou réel de FR-034 : si un utilisateur standard pouvait l'écrire, il lèverait ses propres
limites avec un éditeur de texte et l'autorisation IPC ne serait qu'un verrou d'interface.

---

## Permissions

| Entité | Droits |
|--------|--------|
| `NT AUTHORITY\SYSTEM` | Contrôle total |
| `BUILTIN\Administrators` | Contrôle total |
| `BUILTIN\Users` | **Lecture seule** |
| Héritage | **Désactivé** — les ACE sont explicites |

Les ACL sont posées par le MSI à l'installation **et revérifiées par le service à chaque
démarrage**. Une ACL trouvée trop permissive est corrigée et l'événement est journalisé : une
permission élargie par un tiers ne doit pas passer inaperçue.

**[TEST]** SC-013 : un processus s'exécutant sous un compte standard ne peut pas écrire ce
fichier, ni le remplacer, ni le renommer, ni modifier son ACL.

---

## Schéma

```json
{
  "schemaVersion": 1,
  "activeProfileId": "3f2a…",
  "profiles": [
    {
      "id": "3f2a…",
      "name": "Télétravail",
      "globalLimit": {
        "enabled": true,
        "downloadBytesPerSecond": 5242880,
        "uploadBytesPerSecond": 1048576
      },
      "rules": [
        {
          "id": "9c11…",
          "target": {
            "executablePath": "c:\\program files\\steam\\steam.exe",
            "executableName": "steam.exe",
            "displayName": "Steam"
          },
          "downloadBytesPerSecond": 1048576,
          "uploadBytesPerSecond": null,
          "enabled": true,
          "exemptFromGlobal": false
        }
      ]
    }
  ]
}
```

### Règles de champ

| Champ | Type | Contrainte |
|-------|------|------------|
| `schemaVersion` | entier | Exactement `1`. Toute autre valeur → traitée comme corrompue |
| `activeProfileId` | GUID | MUST exister dans `profiles` |
| `profiles` | tableau | 1 à 5 éléments |
| `name` | chaîne | 1–64 caractères, unique sans distinction de casse, sans caractère de contrôle |
| `rules` | tableau | 0 à 200 éléments |
| `executablePath` | chaîne | Chemin normalisé, ≤ 32 767 caractères |
| `*BytesPerSecond` | entier long ou `null` | `null` = illimité ; sinon 10 240 à 1 073 741 824 |

Toutes les unités sont en **octets par seconde**. Le fichier ne stocke jamais de Ko/s ni de
Mo/s : la conversion appartient à l'affichage. Une unité ambiguë dans un fichier persisté est une
source de bug silencieux à chaque évolution.

---

## Écriture

Séquence obligatoire, dans cet ordre :

1. Sérialiser dans `config.json.tmp`, dans le même répertoire — donc le même volume.
2. Vider les tampons sur le disque.
3. `File.Replace` vers `config.json`, avec fichier de sauvegarde.
4. Supprimer la sauvegarde après succès.

**[TEST]** Après une interruption simulée à chaque étape, la relecture donne soit l'ancien
contenu intégral, soit le nouveau — jamais un fichier tronqué. C'est le cas « coupure de courant
pendant la sauvegarde », qui sinon produit une configuration corrompue au pire moment.

---

## Lecture et corruption (FR-022)

```text
Lecture ──JSON invalide / schemaVersion inconnue / invariant violé──> Corrompue
                                                                          │
   config.json → config.corrupt.<horodatage>.json                         │
   démarrage SANS aucune limite                                    <──────┘
   HealthState.configWarning renseigné
   restauration proposée dans l'interface
```

**Invariants**

- **[TEST]** Une configuration corrompue n'applique **aucune** limite. Jamais d'application
  partielle des règles lisibles : un jeu de règles à moitié appliqué est plus trompeur
  qu'aucune règle.
- **[TEST]** Le fichier corrompu est conservé, jamais supprimé — c'est la seule chance de
  récupérer les réglages de l'utilisateur.
- **[TEST]** Aucune réparation automatique silencieuse.

---

## Export et import (FR-023)

Même schéma, même validation. Un fichier importé est **revalidé intégralement comme une saisie
utilisateur**, bornes comprises, avant d'être appliqué. Les identifiants sont régénérés à
l'import pour éviter toute collision avec la configuration existante.

L'export ne contient aucune donnée de la machine d'origine : ni nom de machine, ni nom
d'utilisateur, ni mesure de trafic. Un fichier d'export est destiné à être partagé ; il ne doit
rien révéler de plus que des chemins d'exécutables et des plafonds.
