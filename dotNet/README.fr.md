# MutagenMon — réécriture .NET WPF

Ceci est la réécriture .NET créée selon
[requirements/05-wpf-migration-notes.md](../requirements/05-wpf-migration-notes.md).
Périmètre actuel : **Phase 0 (scaffolding) + Phase 1 (cœur de l'icône de
barre d'état système en temps réel) + Phase 2 (menu contextuel & vue de
statut) + Phase 3 (résolution manuelle des conflits)** — sondage en
arrière-plan, classification/agrégation du statut des sessions, la
machine à états complète de l'icône de la barre d'état système, le menu
contextuel complet (recharger/arrêter-démarrer/afficher le
statut/quitter, FR-7), la vue de statut avec sa section de conflits
(FR-8), et le workflow de résolution manuelle des conflits avec
intégration de la fusion visuelle (FR-9). La résolution automatique des
conflits, les notifications, et l'exécution du redémarrage automatique
des sessions ne sont **pas encore implémentés** — voir la section "Out of
scope" du plan à partir duquel ceci a été construit.

> Ceci a commencé comme une application Blazor Hybrid (WPF +
> `BlazorWebView`) et a été réorienté vers du WPF pur après de véritables
> échecs d'exécution Blazor/WebView2 sur .NET 10 — voir la note de révision
> des notes de migration pour en savoir plus.

## Structure de la solution

```
dotNet/
├── MutagenMon.sln
├── Directory.Build.props
└── src/
    ├── MutagenMon.Core/            # net10.0 — no WPF deps, fully testable on any OS
    ├── MutagenMon.Core.Tests/      # xunit v3 — runs on Linux/macOS/Windows
    └── MutagenMon.App/             # net10.0-windows, WPF host — Windows-only at runtime
```

## Compilation (fonctionne sous Linux, macOS ou Windows)

`MutagenMon.App` cible `net10.0-windows` avec
`EnableWindowsTargeting=true`, ce qui récupère les assemblies de référence
Windows Desktop depuis NuGet — ainsi, **toute la solution se compile sur
n'importe quel OS**, sans avoir besoin d'une véritable machine Windows :

```bash
dotnet build MutagenMon.sln
```

```bash
dotnet test src/MutagenMon.Core.Tests/MutagenMon.Core.Tests.csproj
```

La logique de classification/péremption/état de l'icône de barre d'état
système de `MutagenMon.Core` ne dépend aucunement d'un véritable binaire
`mutagen` ni d'une véritable icône de barre d'état système (NFR-11) —
`SessionMonitorServiceTests` fait passer tout le pipeline à travers un
client CLI factice, et `TrayIconStateResolverTests` est un test paramétré
couvrant chaque ligne du tableau de décision de la §3 de
[requirements/03-tray-icon-requirements.md](../requirements/03-tray-icon-requirements.md).
Les deux s'exécutent et réussissent sous Linux.

## Exécution / vérification sous Windows (obligatoire — WPF/l'icône de barre d'état système sont réservés à Windows)

`MutagenMon.App` ne peut pas être exécuté ni vérifié visuellement sous
Linux — WPF et l'icône de barre d'état système nécessitent une véritable
session de bureau Windows. Sur une machine Windows :

1. Installer une version de [mutagen.io](https://github.com/mutagen-io/mutagen)
   et placer le binaire à `src/MutagenMon.App/mutagen/mutagen.exe`
   (ou modifier `MUTAGEN_PATH` dans `src/MutagenMon.App/config/config_mutagenmon.json`).
2. Modifier `src/MutagenMon.App/mutagen/mutagen-create.bat` pour pointer
   vers deux véritables dossiers locaux (l'exemple fourni utilise
   `C:\MutagenMonTest\alpha`/`beta` — les créer, ou changer les chemins).
3. Exécuter :
   ```bash
   dotnet run --project src/MutagenMon.App
   ```
   Ou dans Visual Studio : ouvrir `MutagenMon.sln`, définir **MutagenMon.App**
   comme projet de démarrage (Explorateur de solutions → clic droit dessus →
   *Définir comme projet de démarrage*), puis F5/Ctrl+F5. Le `AssemblyName`
   du projet est `MutagenMon`, donc l'exécutable compilé est
   `MutagenMon.exe`, mais le *projet* à lancer est `MutagenMon.App`.
4. **Liste de vérification :**
   - Une icône de barre d'état système apparaît immédiatement dans l'état
     "en attente de statut" (gris clair) — correspond à
     `SessionStatusCode.Unknown` dans
     [03-tray-icon-requirements.md](../requirements/03-tray-icon-requirements.md) §3.
   - Au fur et à mesure que mutagen scanne/synchronise/se stabilise,
     l'icône et son infobulle passent par Scanning → Syncing → Ready, en
     correspondant exactement à ce même tableau.
   - Clic gauche, ou clic droit → **Show status**, ouvre la vue de statut :
     un bloc Name/Status/Alpha/Beta par session configurée, et — si une
     session a un conflit non résolu — une section CONFLICTS avec un
     bouton "Resolve conflicts" qui démarre le workflow de résolution
     manuelle (FR-9) : un conflit à la fois, numéroté « N sur total »,
     avec une comparaison A/B (URL, taille, dernière modification) et un
     choix Visual merge / A wins / B wins présélectionné selon le côté
     modifié le plus récemment. Annuler interrompt tout le lot. Chaque
     résolution est ajoutée à `log/resolve.log`.
   - Clic droit → **Reload config & restart mutagen** termine toutes les
     sessions en cours, puis redémarre tout le processus une fois qu'elles
     sont toutes arrêtées (observer le menu contextuel se réduire à un
     élément désactivé "Restarting..." pendant ce temps, et vérifier
     `log/mutagenMon.log` pour l'entrée de redémarrage).
   - Clic droit → **Stop Mutagen sessions** termine toutes les sessions en
     cours et bascule l'élément vers **Start Mutagen sessions** ; l'icône
     de la barre d'état système doit refléter la variante "désactivée" de
     son état actuel (voir `Enabled` dans le tableau de décision de
     l'icône). Remarque : réactiver ne relance pas automatiquement une
     session terminée — c'est la logique de récupération automatique,
     FR-13, Phase 5 — donc les sessions restent arrêtées jusqu'au prochain
     "Reload config & restart mutagen" ou un redémarrage manuel de
     l'application.
   - Clic droit → **Exit MutagenMon** retire l'icône de la barre d'état
     système et ferme le processus proprement.
   - Les ressources de l'icône de barre d'état système sont chargées
     depuis `Assets/Icons/*.ico` via `TaskbarIcon.Icon` (un
     `System.Drawing.Icon`), et **non** `TaskbarIcon.IconSource` — voir
     [03-tray-icon-requirements.md](../requirements/03-tray-icon-requirements.md)
     §3.1 pour comprendre pourquoi (la conversion interne PNG→Icon de
     `IconSource` est fragile et a levé
     `ArgumentException: Argument 'picture' must be a picture
     that can be used as a Icon.` lors de la vérification manuelle).
   - **Vérification de la péremption :** renommer/déplacer temporairement
     `mutagen.exe` pour que le sondage commence à échouer, et observer
     l'icône se dégrader
     Info (pâle) → Warning → Error (icônes "périmées" teintées de rouge)
     selon le calendrier défini dans `STATUS_MAX_LAG`. Au-delà du seuil de
     `Restart` (90s par défaut), l'application doit se redémarrer
     elle-même (générer un nouveau processus, l'icône de barre d'état
     système réapparaît) — vérifier `log/mutagenMon.log` pour l'entrée de
     redémarrage.

## Journalisation

La journalisation repose sur un petit `ILoggerProvider` écrit à la main
(`FileLoggerProvider.cs`) — sans bibliothèque de journalisation tierce —
configuré dans `App.xaml.cs`. Chaque appel de journalisation ouvre, ajoute
à, puis ferme son fichier cible (pas de handle persistant, donc rien à
vider/disposer). Le journal principal est écrit dans le répertoire nommé
par `LOG_PATH` dans `config_mutagenmon.json` (par défaut `"log"`, résolu
par rapport au dossier de `MutagenMon.exe`) — ou, si `LOG_PATH` est un
chemin absolu (par ex. `"c:\\logs\\mutagenmon"`), c'est exactement ce
répertoire qui est utilisé tel quel à la place :

- **`log/mutagenMon.log`** — un seul fichier capturant **tous les
  niveaux** (Debug et au-dessus), toujours — pas de fichier de debug
  séparé, pas de filtrage via `DEBUG_LEVEL` (`DEBUG_LEVEL` dans
  `config_mutagenmon.json` est actuellement inutilisé). C'est également
  là que les échecs de démarrage atterrissent : chaque étape de
  `OnStartup` (chargement de la config, analyse des sessions,
  construction/démarrage de l'hôte, création de l'icône de barre d'état
  système) est enveloppée dans un unique try/catch qui journalise toute
  exception ici au niveau Critical *et* affiche une `MessageBox` avec
  l'exception complète — ajouté spécifiquement pour qu'un lancement
  échoué (par ex. un `config_mutagenmon.json` ou un
  `mutagen-create.bat` manquant) ne soit jamais silencieux. Les
  exceptions non gérées ailleurs dans l'application (thread UI, threads
  en arrière-plan, exceptions de tâches non observées) sont également
  capturées globalement et journalisées ici.
- **`mutagenMon.fatal.log`** (à côté de `MutagenMon.exe`, c'est-à-dire
  *pas* sous `LOG_PATH`) — une copie redondante des entrées de niveau
  Critical uniquement. Existe car `LOG_PATH` peut pointer à l'intérieur
  d'un dossier que mutagen lui-même synchronise (une chose raisonnable à
  faire, mais cela signifie que le fichier journal peut occasionnellement
  être brièvement verrouillé/touché par le moteur de synchronisation ou
  un lecteur distant précisément au moment d'une écriture) ; ce
  mécanisme de repli se trouve toujours à un emplacement fixe, de sorte
  qu'un crash n'est jamais perdu à cause d'un échec d'écriture transitoire
  sur le puits primaire. Tout échec d'écriture sur le puits primaire
  (quel que soit le niveau) est également signalé dans la fenêtre de
  sortie de Visual Studio (`System.Diagnostics.Debug`) et, sur une base
  de meilleur effort, dans ce même fichier de repli.

Si rien n'apparaît dans la barre d'état système et que l'application
semble ne rien faire : vérifier d'abord `log/mutagenMon.log` — une
exception de démarrage y est désormais garantie d'être journalisée et
affichée dans une boîte de message au lieu de tuer silencieusement le
processus (la cause la plus fréquente étant un
`config/config_mutagenmon.json` ou un `mutagen/mutagen-create.bat`
manquant/mal configuré, ou un `MUTAGEN_PATH` invalide). Si même ce fichier
est vide, vérifier `mutagenMon.fatal.log`.

## Limitations connues de cette phase

- La résolution manuelle des conflits (FR-9) nécessite de véritables
  binaires `ssh`/`scp`/outil de fusion et ne peut pas être exercée de
  bout en bout sous Linux — seule la logique pure
  (`ConflictBatchPlanner`, `ConflictResolutionService` face à un
  `IConflictFileClient` factice, `ResolveLogWriter`) y est testée
  unitairement ; l'invocation réelle SSH/copie/outil de fusion
  (`MutagenMon.Core/Resolution/ConflictFileClient.cs`) nécessite une
  vérification sous Windows, comme le reste de la couche WPF.
- Pas de notifications de bureau (FR-11), pas d'exécution automatique du
  redémarrage des sessions (FR-13) — "Start Mutagen sessions" réactive le
  monitoring mais ne relance pas lui-même une session que "Stop Mutagen
  sessions" (ou un échec de sondage) a terminée ; cette relance est la
  logique de récupération automatique, Phase 5.
- Trois ressources d'icônes (`green-scan`, `green-timeout-white`,
  `green-timeout-red`) sont des placeholders générés, et non des
  ressources de design finales — voir
  [requirements/03-tray-icon-requirements.md](../requirements/03-tray-icon-requirements.md) §3.1/§7.1.
