# TB Recherche

Moteur de recherche instantané pour les mails de Thunderbird **et le contenu de leurs pièces jointes**. Il lit directement les fichiers du profil (mbox ou maildir) en lecture seule, sans les copier, et tient son propre index plein texte (SQLite FTS5).

## Utilisation

Lancer `dist\TBRecherche.exe`. Aucune installation n'est nécessaire (le moteur d'affichage Edge WebView2, présent sur Windows, sert au rendu des mails).

- Le profil Thunderbird est détecté automatiquement (`profiles.ini`), y compris les comptes rangés hors du profil (ex. `D:\Mails\Compte`).
- L'index se met à jour au démarrage puis toutes les 5 minutes, en ne relisant que les dossiers modifiés.
- Pour forcer un profil : `TBRecherche.exe --profile "D:\Thunderbird\xxxxxxxx.default-release"`

## Ce que fait la V2

- Interface repensée : comptes avec compteurs et période dans la colonne de gauche, résultats en cartes (avatar, extrait, compte, dossier), volet de lecture.
- Mails affichés en HTML comme dans Thunderbird. Les scripts sont bloqués, ainsi que les images distantes par défaut (pixels de suivi) ; le bouton « Afficher les images » les autorise pour le message.
- Les mots cherchés sont surlignés dans le mail, et les pièces jointes qui les contiennent sont entourées.
- Texte des pièces jointes indexé : PDF, Word, Excel, PowerPoint, OpenDocument, texte, CSV, HTML, RTF (jusqu'à 25 Mo par fichier, 80 pages par PDF).

## Syntaxe de recherche

| Saisie | Effet |
|---|---|
| `facture dupont` | les deux mots (début de mot, sans accents ni majuscules), dans le mail ou ses PJ |
| `"bon de commande"` | expression exacte |
| `facture -relance` | exclut un mot |
| `devis OU facture` | l'un ou l'autre |
| `de:dupont` / `a:martin` | expéditeur / destinataires (À et Cc) |
| `objet:commande` / `corps:palette` | objet / texte du message |
| `pj:pdf` | nom des pièces jointes |
| `contenu:siret` | uniquement dans le texte des pièces jointes |
| `dossier:douanes` | limite à un dossier |

Raccourcis : Ctrl+F pour la recherche, flèches haut et bas pour parcourir les résultats depuis la recherche, Entrée ou double-clic pour ouvrir dans Thunderbird, Échap pour effacer.

## Où sont les données

- Index : `%LOCALAPPDATA%\TBRecherche\index.db` (environ 110 Mo pour 37 000 messages avec le texte des PJ). On peut le supprimer : il se reconstruit, en une quinzaine de minutes avec l'extraction des PJ.
- Copies temporaires (affichage, `.eml` ouverts dans Thunderbird, PJ ouvertes) : `%TEMP%\TBRecherche`.

## Limites connues

- Seuls les dossiers IMAP **synchronisés hors ligne** sont visibles.
- Les PDF scannés (images sans couche texte) ne sont pas lisibles : pas d'OCR.

## Extension Thunderbird

`tb-recherche.xpi` ajoute un bouton **Recherche** (et le raccourci Ctrl+Alt+R) qui ouvre un onglet de recherche dans Thunderbird. Un clic sur « Ouvrir » affiche le vrai message dans Thunderbird (réponse, transfert, classement possibles).

Installation sur un poste :
1. Lancer une fois `TBRecherche.exe` : il s'inscrit auprès de Thunderbird comme moteur de l'extension (clé `HKCU\Software\Mozilla\NativeMessagingHosts\tbrecherche`). À refaire si l'exe est déplacé.
2. Dans Thunderbird : Outils > Modules complémentaires et thèmes > roue dentée > Installer un module depuis un fichier > `tb-recherche.xpi`.

L'extension et l'application partagent le même index ; un verrou empêche deux indexations simultanées. En cas de souci, le journal du moteur est dans `%LOCALAPPDATA%\TBRecherche\host.log`.

## Développement

Prérequis : SDK .NET 8 (`winget install Microsoft.DotNet.SDK.8`), sous Windows 64 bits.

```
src\TBRecherche.Core   moteur : profil, découpage mbox, extraction PJ, indexation, recherche, rendu HTML, hôte de l'extension
src\TBRecherche        interface WPF (Theme.xaml, MainWindow) ; l'exe sert aussi d'hôte quand Thunderbird le lance
src\TBRecherche.Cli    outil de test : tbr accounts | index [--full] | search <texte> | show <id> | verify | host | hosttest
extension\             extension Thunderbird (manifest.json, search.html/css/js, background.js)
```

Recompiler l'exe (la fenêtre doit être fermée) :

```
dotnet publish src\TBRecherche -c Release -o dist
```

Empaqueter l'extension : zipper le **contenu** du dossier `extension` (manifest.json à la racine de l'archive) et renommer en `.xpi`.

`tbr verify` compare, fichier par fichier, le nombre de messages indexés au nombre de séparateurs `From ` du mbox. À relancer après toute modification du découpage.

La recherche ouvre l'index en lecture seule (`IndexDb.OpenRead`) : en mode WAL elle n'attend jamais l'indexation. Ne pas revenir à une connexion en écriture pour la recherche, sinon la saisie se fige pendant les indexations.
