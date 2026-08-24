# Notes de migration WPF

Ces notes transposent l'architecture wxPython historique en concepts pour
une réécriture **full WPF**. Il s'agit d'un guide de conception de
solution, pas d'une spécification finalisée — les documents d'exigences
fonctionnelles/non fonctionnelles restent la source de vérité pour le
*comportement* ; ce document porte sur la *façon* d'héberger ce
comportement sur la pile WPF.

> **Note de révision** : ce document spécifiait initialement un hébergeur
> Blazor Hybrid (WPF + `BlazorWebView` pour les boîtes de dialogue/pages
> de statut). Cette approche a été abandonnée après que la Phase 1 a
> rencontré deux véritables échecs d'exécution spécifiques à
> Blazor/WebView2 sous Windows (une `FileNotFoundException` sur
> `WebView2CompositionControl` liée à `Microsoft.Windows.SDK.NET`, suivie
> en amont comme une régression .NET 10 :
> https://github.com/MicrosoftEdge/WebView2Feedback/issues/5436), en plus
> d'une dépendance d'exécution supplémentaire (le runtime WebView2) et
> d'une surcharge mémoire/démarrage allant à l'encontre de la NFR-3. La
> surface d'interface utilisateur réelle de l'application — une liste de
> statuts et quelques boîtes de dialogue simples — n'a aucun besoin de
> HTML/CSS, et le multiplateforme (l'autre raison habituelle de recourir à
> Blazor Hybrid via MAUI) était déjà explicitement hors périmètre (voir
> §1). Le WPF pur élimine toute cette catégorie de défaillance au lieu de
> la contourner.

## 1. Choix de l'hébergeur : cette décision conditionne tout le reste

Étant donné que :

- la cible principale du projet aujourd'hui est Windows (NFR-4),
- l'exigence la plus importante est une **icône de zone de notification**
  (system tray) en temps réel (03-tray-icon-requirements.md), et
- le desktop multiplateforme n'est explicitement *pas* un objectif de
  lancement (NFR-4) — l'application historique elle-même n'était « testée
  que sous Windows » malgré la même ambition, et rien ici ne change cela,

l'hébergeur retenu est une simple **application WPF** associée à une
bibliothèque dédiée d'icône de zone de notification :

- WPF dispose d'un support mature et de premier ordre pour l'icône de zone
  de notification via des bibliothèques communautaires (par ex.
  `H.NotifyIcon.Wpf`), directement utilisable sans couche d'abstraction
  supplémentaire.
- Aucune dépendance au runtime Blazor/WebView2 : une chose de moins à
  installer sur la machine cible, une surface d'interopérabilité
  native/managée de moins susceptible de régresser sous les pieds de
  l'application lors d'une mise à jour .NET ou WebView2.
- Chaque boîte de dialogue/écran de statut de cette application
  (04-ui-screens-inventory.md) est une simple mise en page
  liste/texte/boutons — exactement dans le cœur de compétence natif de
  WPF, sans besoin de rendu ou de style qui justifierait un moteur de
  navigateur embarqué.

Si le desktop multiplateforme (zone de notification macOS/Linux) devient
un objectif réel à court terme, revoir cette décision — il vaudrait alors
la peine d'évaluer une pile d'interface utilisateur multiplateforme avec
support natif de la zone de notification (par ex. Avalonia). **Ne pas
construire pour le multiplateforme de façon spéculative.**

## 2. Correspondance des composants

| Legacy (wxPython) | Équivalent WPF |
|---|---|
| `wx.adv.TaskBarIcon` (`icon.py`) | `NotifyIcon` (via `H.NotifyIcon.Wpf`) hébergé par l'application WPF ; permutation du bitmap de l'icône + mise à jour du texte d'infobulle depuis un `DispatcherTimer` (tic de 1 s) |
| Tic d'interface utilisateur `wx.Timer` (`TaskBarIcon.update`) | `DispatcherTimer` sur le thread d'interface utilisateur WPF |
| `Monitor`, `threading.Thread` d'arrière-plan (`monitor.py`) | Un service d'arrière-plan hébergé (`IHostedService` / `BackgroundService` dans un hôte générique) interrogeant le moteur de synchronisation à sa propre cadence, exposant l'état via un service singleton injecté (instantané par référence volatile, sans verrou), reproduisant le motif get/set-avec-verrou historique |
| `queue.Queue` de messages de notification | `System.Threading.Channels.Channel<T>` (borné, un seul écrivain dans le service d'arrière-plan, un seul lecteur dans le timer d'interface utilisateur) |
| `wx.MessageDialog` / `wx.SingleChoiceDialog` (`resolve.py`, `wx.py`) | `Window`/`UserControl` WPF simples, ouvertes à la demande depuis l'icône de zone de notification (pas de fenêtre principale persistante, conformément à la NFR-7) |
| `wx.adv.NotificationMessage` / `ShowBalloon` | Notifications toast Windows (`Microsoft.Toolkit.Uwp.Notifications` / notifications du `Windows App SDK`), ou `NotifyIcon.ShowBalloonTip` comme repli plus léger reproduisant exactement le comportement historique |
| Configuration JSON avec commentaires `#` (`file.py: load_config`) | Suppression artisanale des lignes de commentaire `#` entières + `System.Text.Json` (voir `ConfigLoader` dans `MutagenMon.Core`) — garder le fichier éditable par un humain, sans étape de build |
| Analyse des sessions dans `mutagen/mutagen-create.bat` | Continuer à analyser la même source `.bat`/arguments CLI pour une compatibilité immédiate pendant la transition, mais prévoir une configuration de session structurée (par ex. un tableau `sessions.json`) comme source de vérité à long terme |
| Appels `subprocess` vers `mutagen`, `ssh`, `scp` | `System.Diagnostics.Process` encapsulé dans une unique abstraction `IMutagenCliClient` (reflète l'exigence NFR-10 de « frontière d'analyse unique ») |
| Invocation externe de WinMerge | Même approche : `Process.Start` avec un chemin d'exécutable configurable ; aucun changement de comportement attendu |

## 3. Modèle de processus/threading

Conserver le modèle historique à deux boucles, transposé 1:1 :

```
Application WPF (thread UI)                  Service hébergé en arrière-plan
 ├─ NotifyIcon (zone de notification)         ├─ interroge le CLI mutagen toutes
 ├─ DispatcherTimer (1 s) ──lit l'état────────┤  les MUTAGEN_POLL_PERIOD
 │    - recalcule le code le plus défavorable ├─ maintient le statut/code/
 │    - permute le bitmap d'icône + infobulle │  conflits par session
 │    - vide le canal de notification         ├─ exécute les règles de
 │    - détecte l'obsolescence → auto-        │  résolution automatique
 │      redémarrage                           ├─ décide des redémarrages
 └─ ouvre une fenêtre WPF au clic ─────────────┴─ publie via un
      (fenêtre de statut / résolution de           conteneur d'état thread-safe
      conflit)                                     session
```

Cela préserve la propriété de fiabilité clé issue des NFR-1/NFR-2 : le
timer d'interface utilisateur de l'icône de zone de notification ne doit
jamais être bloqué par un appel CLI `mutagen` lent ou bloqué, car il ne
fait jamais que lire un état déjà publié.

## 4. Fenêtres/contrôles WPF à construire

Directement dérivés de
[04-ui-screens-inventory.md](04-ui-screens-inventory.md) :

1. `StatusWindow.xaml` — liste des sessions + section conflits optionnelle
   (remplace les écrans 3 et 4 par une seule fenêtre paramétrée par
   « has conflicts »).
2. `GenericMessageDialog.xaml` — fenêtre titre/corps/OK[/Annuler]
   réutilisable (remplace le motif partagé des écrans 3/4/7/8/9, selon la
   note de l'inventaire des écrans).
3. `ConflictResolutionDialog.xaml` — la comparaison A/B + choix par bouton
   radio + actions Fusion visuelle/A gagne/B gagne (écran 5).
4. Un indicateur léger « connexion... » (écran 6) — une petite `Window`
   sans décoration, fermée par programmation par l'appelant.
5. L'affichage d'erreur (écran 10) peut réutiliser
   `GenericMessageDialog.xaml`.

## 5. Configuration et modèle de session

Préserver la *forme* de `config_mutagenmon.json` (mêmes clés, mêmes
valeurs par défaut) afin que les opérateurs migrant depuis l'application
historique n'aient pas à réapprendre les paramètres de réglage
(`STATUS_MAX_LAG`, `MUTAGEN_POLL_PERIOD`, `SESSION_MAX_ERRORS`, etc.) —
voir [06-configuration-reference.md](06-configuration-reference.md) pour
la liste complète des clés, leurs types, valeurs par défaut et unités
(autonome ; pas besoin d'ouvrir le fichier de configuration historique).
Ajouts fortement recommandés pour la réécriture :

- Valider la configuration au chargement (échec rapide avec un message
  clair) plutôt que la confiance implicite de l'ancienne version.
- Rendre l'ensemble des ressources d'icônes piloté par les données (un
  manifeste unique associant état → chemin de ressource) afin que les
  lacunes identifiées dans
  [03-tray-icon-requirements.md](03-tray-icon-requirements.md) §7.1 ne
  puissent pas se reproduire silencieusement — une ressource manquante
  doit échouer à la validation au démarrage, pas à la première occurrence
  de cet état en production.

## 6. Livraison par phases proposée

1. **Phase 1 (critique pour la parité)** : service de polling en
   arrière-plan, classification du statut de session (FR-2/FR-3/FR-4),
   icône de zone de notification avec la matrice d'états complète et le
   comportement au clic (FR-5, tout 03-tray-icon-requirements.md),
   obsolescence/auto-redémarrage (FR-6). Cela seul livre « l'exigence la
   plus importante » énoncée par le métier. **Fait** — voir
   `dotNet/README.md`.
2. **Phase 2** : vue de statut + actions du menu contextuel (FR-7, FR-8),
   désormais sous forme de `StatusWindow` native selon le §4 ci-dessus au
   lieu d'une page Blazor. **Fait** — voir `dotNet/README.md`. L'action
   "Resolve conflicts" de la vue de statut (FR-8.2) est câblée vers un
   message provisoire ; le véritable workflow est la Phase 3.
3. **Phase 3** : flux de résolution manuelle de conflit (FR-9) et
   intégration de la fusion visuelle. **Fait** — voir `dotNet/README.md`.
4. **Phase 4** : résolution automatique de conflit (FR-10), notifications
   (FR-11), détection de changement de profil (FR-12).
   - [x] FR-10 — résolution automatique de conflit (règles regex ordonnées,
     période de grâce `AUTORESOLVE_HISTORY_AGE`). **Fait** — voir
     `dotNet/README.md`.
   - [x] FR-11.1/FR-11.2/FR-11.4 — notifications de bureau pour les nouveaux
     conflits, la résolution automatique et la mise à jour de profil
     confirmée. **Fait** — voir `dotNet/README.md`. FR-11.3 (notification de
     redémarrage suite à connexion bloquée) déplacée en Phase 5
     ci-dessous — elle dépend du déclencheur de redémarrage par session de
     FR-13, qui n'existe pas encore.
   - [x] FR-12 — détection de changement de profil de session (surveillance
     de la date de modification de l'archive, avec debounce via
     `MUTAGEN_PROFILE_GRACE`). **Fait** — voir `dotNet/README.md`.
5. **Phase 5** : récupération automatique de session (FR-13) et
   finalisation de la journalisation/du diagnostic (FR-14).
   - [ ] FR-13 — récupération automatique de session (redémarrage sur
     `SESSION_MAX_NOSESSION`), ainsi que la notification de redémarrage
     suite à connexion bloquée (FR-11.3) qu'elle conditionne.
   - [ ] FR-14 — finalisation de la journalisation/du diagnostic
     (journalisation de base déjà **Fait** en Phase 1, voir §7 ci-dessous ;
     cet élément couvre les points FR-14 restants non encore traités, par
     ex. le croisement du journal de résolution en FR-14.3).

Chaque ligne `[ ]`/`[x]` est une unité de suivi autonome : cocher la case
et ajouter une courte note de statut (**Fait** — voir `dotNet/README.md`,
ou **Ignoré** — avec la raison) dès que cette FR précise est terminée ou
consciemment reportée — ne pas attendre que toutes les FR de la phase
soient livrées avant de mettre à jour cette liste. Cela permet de traiter
une phase FR par FR (par ex. « ne faire que la FR-10 de la Phase 4 »)
sans perdre le fil de ce qui reste à faire.

Chaque phase doit se clôturer par un examen explicite (triage) des lacunes
correspondantes de 03-tray-icon-requirements.md §7 (corrigées ou
consciemment reportées), et non silencieusement reconduites.

## 7. Journalisation

Implémentée en Phase 1 (`MutagenMon.App/App.xaml.cs`,
`MutagenMon.App/FileLoggerProvider.cs`), et délibérément plus simple que
la conception historique décrite dans
[01-functional-requirements.md FR-14](01-functional-requirements.md#fr-14--logging--diagnostics)
— voir cette section pour la correspondance FR-14.1/14.2/14.3 et sa
justification. Les décisions concrètes :

- **Aucune bibliothèque de journalisation tierce** : la journalisation est
  un petit `Microsoft.Extensions.Logging.ILoggerProvider`/`ILogger`
  artisanal (`FileLoggerProvider`) écrivant des lignes en texte brut dans
  un fichier — rien au-delà de
  `Microsoft.Extensions.Logging.Abstractions`, dont l'application dépend
  déjà pour le motif DI standard `ILogger<T>` utilisé partout dans
  `MutagenMon.Core`. Une bibliothèque tierce (Serilog) a été utilisée
  initialement puis retirée — elle ajoutait une dépendance pour quelque
  chose d'aussi restreint qu'un fournisseur artisanal couvre directement,
  avec un contrôle total sur le comportement en cas d'échec (voir le
  dernier point ci-dessous).
- **Chaque appel est autonome** : chaque appel de journalisation ouvre,
  ajoute puis referme le fichier cible — aucun descripteur de fichier
  persistant n'est conservé entre les appels. Rien n'a besoin d'être vidé
  ou libéré à la reconfiguration ou à l'arrêt, et le fichier n'est jamais
  maintenu ouvert pendant une boîte de dialogue `MessageBox` bloquante.
- **Un seul récepteur (sink) principal, toujours actif** : un fichier
  (`log/mutagenMon.log`, ou `<LOG_PATH>/mutagenMon.log` si `LOG_PATH` est
  configuré) capture inconditionnellement tous les niveaux à partir de
  Debug. Pas de fichier de debug séparé, pas de filtre de verbosité.
  `DEBUG_LEVEL` reste dans `config_mutagenmon.json` pour la compatibilité
  des clés avec la configuration historique (selon le §5 « préserver la
  forme de la configuration ») mais n'a actuellement aucun effet.
- **Configuration en deux étapes** : `FileLoggerProvider` est construit
  avec un chemin par défaut (`"log"`) *avant même* que
  `config_mutagenmon.json` ne soit lu, puis redirigé via
  `SetPrimaryLogPath` une fois le véritable `LOG_PATH` connu. Cela
  garantit qu'un échec survenant pendant le chargement de la configuration
  produit malgré tout une entrée de journal, au lieu d'échouer avant même
  que la journalisation n'existe.
- **Résolution de `LOG_PATH`** : résolu relativement au répertoire de
  l'exécutable, *sauf* s'il s'agit d'un chemin absolu/enraciné
  (`Path.IsPathRooted`), auquel cas il est utilisé tel quel — ce qui
  permet à un opérateur de rediriger les journaux vers un emplacement fixe
  (par ex. un dossier synchronisé, un lecteur de journaux centralisé),
  indépendamment de l'endroit où l'application est installée.
- **Le démarrage est intégralement tracé** : chaque étape de `OnStartup`
  (chargement de la configuration, analyse du fichier de sessions,
  construction/démarrage de l'hôte, acquisition de l'icône de zone de
  notification, démarrage du contrôleur de zone de notification) journalise
  au niveau Information, et le corps entier de la méthode est enveloppé
  dans un unique try/catch qui journalise toute exception au niveau
  Critical et l'affiche dans une `MessageBox` bloquante — voir FR-14.1.
  Les gestionnaires globaux (`Dispatcher.UnhandledExceptionFilter`,
  `DispatcherUnhandledException`, `AppDomain.UnhandledException`,
  `TaskScheduler.UnobservedTaskException`) interceptent de la même manière
  tout ce qui est levé plus tard, n'importe où ailleurs dans
  l'application — y compris les exceptions levées à l'intérieur d'une
  trame de dispatcher imbriquée (ouverture d'un `Popup`/`ContextMenu`),
  que `DispatcherUnhandledException` seul peut manquer.
- **Pourquoi plus simple que l'historique** : lors de la vérification
  manuelle de la Phase 1, une exception au démarrage (un
  `config_mutagenmon.json` malformé) n'a produit *aucune* sortie de
  journal et un processus mort silencieusement, parce que la conception
  équivalente historique (ne configurer la journalisation qu'après le
  chargement réussi de la configuration, filtrer la sortie détaillée
  derrière un indicateur désactivé par défaut) faisait qu'il n'y avait
  encore personne à l'écoute au moment précis où cela comptait le plus.
  Une journalisation toujours active, dans un seul fichier, élimine
  entièrement ce mode de défaillance et est suffisamment simple pour ne
  pas nécessiter sa propre configuration.
- **Une exception interceptée peut malgré tout ne pas atteindre le
  disque — atténué par conception, pas par un ajout de diagnostic** :
  `LOG_PATH` peut pointer à l'intérieur d'un dossier que mutagen
  lui-même synchronise (comme c'est le cas en développement — la copie de
  travail de ce dépôt est elle-même synchronisée entre la machine de
  développement Linux et la machine de test Windows), de sorte qu'une
  écriture survenant exactement au moment où le moteur de synchronisation
  (ou un lecteur distant) touche le même fichier constitue une façon
  réaliste pour une écriture de journal d'échouer (observé pendant la
  Phase 1 avec une bibliothèque de journalisation tierce : une
  `ObjectDisposedException` provenant de la réutilisation de
  `TaskbarIcon.Icon` déclenchait la boîte de dialogue d'erreur mais ne
  laissait silencieusement aucune entrée de journal, car cette
  bibliothèque avale par défaut les échecs d'écriture au niveau du sink).
  `FileLoggerProvider` traite ce problème directement plutôt que de le
  masquer avec un journal d'auto-diagnostic : chaque écriture est
  enveloppée dans son propre try/catch, un échec est signalé
  immédiatement à `System.Diagnostics.Debug` (visible dans la fenêtre de
  sortie de Visual Studio), et — point critique — un second fichier à
  emplacement fixe (`mutagenMon.fatal.log`, à côté de l'exécutable,
  délibérément *hors* de `LOG_PATH`) reçoit toujours chaque entrée de
  niveau Critical, de sorte qu'un plantage n'est jamais perdu à cause
  d'un échec transitoire du sink principal.

## 8. Pièges d'exécution découverts lors de la vérification Windows de la Phase 1

Conservés ici afin de ne pas être redécouverts à partir de zéro sur une
future machine ou une future mise à jour .NET :

- **`TaskbarIcon.IconSource` n'est pas exempt de conversion** : bien
  qu'exposant une propriété `ImageSource`, le setter `IconSource` de
  `H.NotifyIcon.Wpf` effectue en interne une conversion vers un
  `System.Drawing.Icon` (`StreamExtensions.ToSmallIcon`), ce qui lève une
  exception pour certains PNG (`ArgumentException: Argument 'picture'
  must be a picture that can be used as a Icon.`). Correctif : générer de
  véritables fichiers `.ico` (rembourrés en carré, multi-résolution) et
  les charger via `TaskbarIcon.Icon` directement — voir
  [03-tray-icon-requirements.md](03-tray-icon-requirements.md) §3.1.
- **Un `TaskbarIcon` sans fenêtre nécessite `ForceCreate()`** : sans
  fenêtre principale, l'icône native de zone de notification n'est jamais
  créée implicitement (cela se produit normalement lors de `Loaded`, qui
  ne se déclenche jamais pour une ressource référencée uniquement depuis
  le code). Appeler `trayIcon.ForceCreate()` immédiatement après sa
  résolution — c'est le motif utilisé par l'exemple d'application « sans
  fenêtre » de H.NotifyIcon lui-même.
- **`InvariantGlobalization` casse les modèles de contrôle par défaut de
  WPF** : `<InvariantGlobalization>true</InvariantGlobalization>` (un
  réglage par défaut raisonnable pour `MutagenMon.Core`/`.Tests`, qui n'ont
  aucune dépendance WPF) doit être remplacé par `false` dans le projet
  d'application WPF. Le modèle `ContextMenu` par défaut de WPF résout une
  culture spécifique via `XmlLanguage.GetSpecificCulture()`, ce qui lève
  une exception (`XamlParseException: Cannot find non-neutral culture
  related to 'en-us'`) en mode de globalisation invariante.
- **Les exceptions à l'intérieur du callback WndProc natif de
  H.NotifyIcon plantent silencieusement** : `TaskbarIcon.ShowContextMenu()`
  n'a pas de try/catch et s'exécute de façon synchrone à l'intérieur du
  propre callback natif de message de fenêtre de la bibliothèque (une
  frontière de P/Invoke inversé). .NET fait toujours échouer
  immédiatement (fail-fast) l'ensemble du processus en cas d'exception
  s'échappant d'un tel callback, avant que le moindre gestionnaire
  d'exception managé ne s'exécute — de sorte qu'un bug à cet endroit (par
  ex. celui d'`InvariantGlobalization` ci-dessus, avant sa correction)
  restait totalement non journalisé. Correctif implémenté dans
  `TrayIconController` : intercepter
  `TaskbarIcon.PreviewTrayContextMenuOpen`, annuler la tentative
  synchrone (`e.Handled = true`), puis la réémettre via
  `Dispatcher.BeginInvoke` — une pile d'appels managée normale que les
  gestionnaires d'exception habituels peuvent intercepter.
