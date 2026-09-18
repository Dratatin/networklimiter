# Composants tiers

## WinDivert 2.2.2

- **Projet** : https://github.com/basil00/WinDivert
- **Auteur** : Basil Fierz et contributeurs
- **Licence** : double licence, au choix — **LGPL v3** ou **GPL v2**
- **Mode d'utilisation** : liaison **dynamique** avec `WinDivert.dll`, sans modification du code
  source, ce qui permet la distribution binaire sous LGPL v3.

### Empreintes épinglées

Vérifiées à chaque restauration par [`tools/restore-windivert.ps1`](tools/restore-windivert.ps1)
contre [`tools/windivert.lock.json`](tools/windivert.lock.json). Toute divergence est une erreur
bloquante : aucun binaire n'est installé.

| Fichier | SHA-256 |
|---------|---------|
| `WinDivert-2.2.2-A.zip` | `63cb41763bb4b20f600b6de04e991a9c2be73279e317d4d82f237b150c5f3f15` |
| `x64/WinDivert.dll` | `c1e060ee19444a259b2162f8af0f3fe8c4428a1c6f694dce20de194ac8d7d9a2` |
| `x64/WinDivert64.sys` | `8da085332782708d8767bcace5327a6ec7283c17cfb85e40b03cd2323a90ddc2` |

### Signature du pilote noyau — à lire

Le principe II de la constitution du projet impose de documenter la signature et l'éditeur de
toute dépendance à un pilote noyau. Voici ce que la vérification a relevé le 2026-09-18 :

**`WinDivert64.sys`** — signature Authenticode **valide**, mais le signataire **n'est pas
l'auteur de WinDivert** :

```
Sujet      : CN=成都密思听科技有限公司 (Chengdu Misiting Technology Co., Ltd)
             O=成都密思听科技有限公司, S=四川省, C=CN
             Organisation : Private Organization, n° 91510107MA7E8Y2876
Empreinte  : 043589F75FCE2795E7F2CC3E526D46784D5DDAB3
```

Ce n'est pas une anomalie du téléchargement : c'est la conséquence directe des règles de
Microsoft. Depuis Windows 10, tout pilote noyau doit être signé par un certificat EV **et**
contresigné via le portail d'attestation Microsoft. Beaucoup de projets libres n'ont pas les
moyens d'un tel certificat et font contresigner leur pilote par une entité tierce qui en
détient un.

**Ce que cela implique concrètement** : le code qui s'exécutera en anneau 0 sur la machine de
l'utilisateur porte la signature d'une société sans lien avec le projet WinDivert. La chaîne de
confiance passe donc par ce tiers. WinDivert est largement déployé — clumsy, GoodbyeDPI et
d'autres outils s'en servent — et aucun incident public n'est connu à ce jour, mais le fait doit
être connu de l'utilisateur avant l'installation, pas découvert après.

C'est précisément pourquoi :

- l'empreinte du certificat est **épinglée** : un changement d'éditeur du pilote fait échouer la
  restauration et impose une revue humaine ;
- le pilote est enregistré **par l'installeur**, à un moment où l'utilisateur consent
  explicitement, et non installé silencieusement au premier usage ;
- la désinstallation supprime le service pilote, ce qu'un test vérifie.

**`WinDivert.dll`** n'est **pas signée du tout**. Seule son empreinte SHA-256 atteste sa
provenance. C'est acceptable pour une bibliothèque en mode utilisateur chargée depuis le
répertoire d'installation protégé, mais cela justifie à soi seul l'épinglage.

### Obligations LGPL v3 respectées

- Le texte intégral de la licence est redistribué avec l'application.
- La liaison est dynamique : l'utilisateur peut remplacer `WinDivert.dll` par sa propre version.
- Aucune modification n'est apportée au code source de WinDivert.

**Avertissement pour un usage commercial** : une distribution en source fermée reste possible
sous LGPL v3 avec liaison dynamique, mais si le mode de distribution devait changer — édition
statique, modification du code WinDivert, ou exigence d'un distributeur — la licence commerciale
de WinDivert deviendrait nécessaire. À vérifier **avant** de vendre, pas après.

---

## Paquets NuGet

Versions épinglées dans [`Directory.Packages.props`](Directory.Packages.props).

| Paquet | Licence |
|--------|---------|
| Microsoft.Extensions.Hosting[.WindowsServices] | MIT |
| Serilog et ses sinks | Apache-2.0 |
| CommunityToolkit.Mvvm | MIT |
| xunit, xunit.runner.visualstudio | Apache-2.0 |
| FluentAssertions **7.x** | Apache-2.0 |
| coverlet.collector | MIT |

**FluentAssertions est volontairement figé en 7.x.** À partir de la version 8.0, le paquet est
passé sous licence Xceed, qui exige une licence payante pour tout usage commercial. La branche
7.x reste sous Apache-2.0. Une montée en version majeure doit donc être une décision explicite,
pas une conséquence d'une mise à jour de routine.
