# NetworkLimiter

Limiteur de bande passante par application pour Windows.

Définir un plafond de débit montant et descendant sur une application, ou sur l'ensemble de la
machine, voir en temps réel qui consomme la connexion, et retrouver ses réglages après un
redémarrage.

> **État : en développement.** Rien n'est utilisable pour l'instant. La spécification, le plan et
> le découpage en tâches sont complets ; l'implémentation commence.

---

## Matrice de compatibilité

| Cible | Statut |
|-------|--------|
| Windows 11 21H2 (build 22000) et ultérieur, **x64** | Supporté, testé |
| Windows 10 22H2 (build 19045), **x64** | Supporté, testé — candidat à l'abandon |
| Windows 10 antérieur à 22H2 | Non supporté, bloqué à l'installation |
| **ARM64** | **Non supporté**, bloqué à l'installation |
| x86 32 bits | Non supporté, bloqué à l'installation |
| Windows Server | Hors périmètre |

**Pourquoi ARM64 est exclu** : la limitation repose sur WinDivert, qui ne publie aucun pilote
ARM64. Un pilote noyau ne peut pas s'exécuter sous l'émulation x64 de Windows ARM64 : l'interface
démarrerait, mais aucune limite ne s'appliquerait jamais. Plutôt que de livrer un outil
silencieusement inopérant, l'installeur refuse de s'installer et dit pourquoi.

L'application refuse de démarrer hors de cette matrice, avec un message nommant la cause, plutôt
que de fonctionner partiellement.

---

## Limites connues

Elles sont documentées ici parce qu'un limiteur de bande passante qui surprend son utilisateur
est un limiteur qu'on désinstalle.

- **Trafic descendant sans contrôle de congestion (UDP)** : un paquet entrant est déjà arrivé sur
  la machine ; le retarder ne libère pas la ligne. Pour ces flux — jeux en ligne,
  visioconférence, certains flux vidéo — le plafond ne peut être tenu qu'en rejetant des données.
  L'application le signale explicitement et affiche la proportion rejetée. La tolérance y est de
  20 % au lieu de 10 %.
- **VPN actif** : selon l'endroit où le client VPN chiffre, l'outil peut ne voir que le tunnel.
  La ventilation par application et la distinction internet/local deviennent alors inopérantes.
  L'application détecte la situation et avertit ; elle ne promet pas de la contourner.
- **Réseau local exclu** : les transferts vers un NAS, un partage ou une autre machine du foyer
  ne sont **jamais** comptés dans les plafonds, qui ne visent que le trafic internet.
- **Pas de partage équitable** : sous plafond global, seul le total est garanti. La répartition
  entre applications n'est ni équitable ni priorisée.
- **Pas de blocage** : l'outil limite un débit, il ne coupe pas une application du réseau. C'est
  un choix de périmètre inscrit dans la constitution du projet.

---

## Architecture en deux mots

Un **service Windows** privilégié fait tout le travail réseau. Une **interface** qui tourne en
utilisateur standard s'y connecte par un tuyau nommé local.

Conséquences voulues :

- les limites s'appliquent même quand la fenêtre est fermée ;
- l'interface ne demande jamais l'élévation pour consulter ;
- modifier une limite exige une élévation ponctuelle, pour qu'un compte standard ne puisse pas
  lever les limites posées par l'administrateur ;
- en cas de défaillance — crash, arrêt, pilote absent — le trafic redevient **non limité**.
  Jamais l'inverse.

---

## Développement

**Prérequis** : SDK .NET 10, Windows 11 ou Windows 10 22H2 en x64, WiX v5 pour l'installeur.

```powershell
# Restaurer WinDivert (empreinte SHA-256 et signature Authenticode vérifiées)
./tools/restore-windivert.ps1

# Compiler et tester
dotnet build
dotnet test tests/NetworkLimiter.Core.Tests
```

La documentation de conception vit dans [`specs/001-bandwidth-limiter/`](specs/001-bandwidth-limiter/) :
[spec.md](specs/001-bandwidth-limiter/spec.md) (le quoi),
[plan.md](specs/001-bandwidth-limiter/plan.md) (le comment),
[research.md](specs/001-bandwidth-limiter/research.md) (les arbitrages et leurs sources),
[tasks.md](specs/001-bandwidth-limiter/tasks.md) (le découpage),
[quickstart.md](specs/001-bandwidth-limiter/quickstart.md) (comment vérifier que ça marche).

Les règles non négociables du projet sont dans
[`.specify/memory/constitution.md`](.specify/memory/constitution.md). Elles priment sur toute
autre habitude de code.

---

## Vie privée

L'application fonctionne **intégralement hors ligne**. Elle n'émet aucun octet vers l'extérieur,
ne collecte aucune télémétrie, et ne journalise ni adresse distante, ni nom d'hôte, ni URL, ni
contenu échangé. Seules les métadonnées de flux — processus, volume, débit — sont utilisées, et
elles ne sont jamais persistées.

---

## Licences

Le projet redistribue **WinDivert**, sous double licence LGPL v3 ou GPL v2. Voir
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), qui documente aussi **l'éditeur du pilote
noyau** — une information à lire avant d'installer quoi que ce soit qui s'exécute en mode noyau.
