# Exigences de l'icône de la barre système — ⭐ Exigence de plus haute priorité

> L'icône de la barre système (system tray) est **l'unique surface d'interface
> permanente** de l'application et sa **raison d'être principale** : elle doit
> afficher, d'un coup d'œil et en temps réel, l'état de santé du pire cas
> parmi toutes les sessions de synchronisation surveillées. Toute autre
> fonctionnalité (boîtes de dialogue, notifications, menus) est secondaire et
> accessible *depuis* l'icône de la barre système. Dans la réécriture WPF,
> reproduire fidèlement ce composant — visuels, timing, comportement au clic
> et auto-réparation — est la priorité absolue.

Voir le wireframe : [wireframes/tray-icon-states.svg](wireframes/tray-icon-states.svg),
[wireframes/tray-context-menu.svg](wireframes/tray-context-menu.svg).

Les bitmaps réels de l'icône sont fournis dans [icons/](icons) (autonomes
dans ce dossier `requirements/` — voir §3.1 ci-dessous), en deux formats :
`.png` (image source, conservée à des fins de documentation/aperçu car elle
s'affiche en ligne dans les visualiseurs Markdown) et `.ico` (le format que
l'implémentation .NET DOIT réellement charger à l'exécution — voir §3.1
pour la raison). L'implémentation .NET NE DOIT PAS avoir besoin de lire quoi
que ce soit dans `python/img/` ; chaque ressource référencée par ce document
est déjà copiée (ou, pour les trois qui n'ont jamais existé, placée sous
forme de placeholder étiqueté) dans `requirements/icons/`.

## 1. Boucle de mise à jour

- TIC-1 : Un timer côté UI DOIT se déclencher toutes les **1000 ms**
  (configurable dans l'esprit, codé en dur aujourd'hui) et, à chaque
  déclenchement :
  1. réévaluer si le profil/l'archive de synchronisation sur disque a
     changé (voir FR-12),
  2. recalculer le code d'état agrégé (« pire cas ») sur l'ensemble des
     sessions (FR-4),
  3. définir le bitmap de l'icône et le texte de l'infobulle en conséquence
     (§3),
  4. vider et afficher toute notification en file d'attente (FR-11),
  5. vérifier l'apparition de nouveaux conflits et notifier si nécessaire
     (FR-11.1).
- TIC-2 : Ce timer UI lit l'état publié par un poller (sondeur) en arrière-plan
  indépendant (FR-2) — il NE DOIT PAS lui-même appeler le moteur de
  synchronisation de manière synchrone, afin de garantir que l'icône ne se
  fige jamais, même si le moteur de synchronisation est lent ou bloqué.
- TIC-3 : La toute première icône affichée au démarrage, avant qu'un poll
  (sondage) n'ait été complété, DOIT être un état distinct « en attente de
  statut » (§3, code `0`).

## 2. Règle d'agrégation

- TIC-4 : L'icône représente toujours la **pire session unique** parmi
  toutes les sessions configurées (code numérique minimum, FR-4). Elle
  n'affiche jamais directement une répartition par session — le détail par
  session est accessible en un clic (FR-8).

## 3. Table de décision complète des états de l'icône

L'état est fonction de quatre entrées : le code agrégé (FR-3/FR-4), le fait
que la surveillance soit actuellement **activée** (l'utilisateur ne l'a pas
mise en pause), le fait que le profil de synchronisation vienne d'être
**mis à jour** sur disque (FR-12), et le **palier d'obsolescence** (staleness
tier) du dernier sondage réussi (calculé à partir de
`now − last_poll_time` par rapport aux seuils `STATUS_MAX_LAG`,
`Info < Warning < Error`, par défaut
`{"Info": 4, "Warning": 15, "Error": 50, "Restart": 90}` secondes — voir
[06-configuration-reference.md](06-configuration-reference.md)).

Les paliers d'obsolescence s'appliquent de manière uniforme par-dessus les
cinq lignes « prêt / conflits / problèmes / synchronisation / analyse »
ci-dessous — c'est-à-dire que chacun de ces cinq états peut être affiché
sous sa forme normale, ou dégradé en Info-obsolète / Warning-obsolète /
Error-obsolète si les données sous-jacentes n'ont pas été rafraîchies
récemment. Au-delà du seuil `Restart`, l'application redémarre
automatiquement au lieu d'afficher une icône quelconque (TIC-9).

| Code | État | Activé | Mis à jour | Palier d'obsolescence | Suffixe de l'infobulle | Ressource icône — aperçu / exécution ([icons/](icons)) |
|---|---|---|---|---|---|---|
| `0` | En attente du premier statut | — | — | — | "waiting for status..." | [`lightgray-init.png`](icons/lightgray-init.png) / [`.ico`](icons/lightgray-init.ico) |
| `100` | Prêt / surveille les changements | oui | non | aucun | "mutagen is watching for changes" | [`green.png`](icons/green.png) / [`.ico`](icons/green.ico) |
| `100` | Prêt, tout juste mis à jour | oui | **oui** | aucun | "mutagen is watching for changes (updated)" | [`green-success.png`](icons/green-success.png) / [`.ico`](icons/green-success.ico) *(voir particularité §6.2 — actuellement inatteignable)* |
| `100` | Prêt, mais la surveillance est en cours d'arrêt | **non** | — | quelconque | "mutagen is stopping" | [`green-stop.png`](icons/green-stop.png) / [`.ico`](icons/green-stop.ico) |
| `60` | Conflits détectés | oui | — | aucun | "conflicts" | [`green-conflict.png`](icons/green-conflict.png) / [`.ico`](icons/green-conflict.ico) |
| `50` | Problèmes détectés | oui | — | aucun | "problems" | [`green-error.png`](icons/green-error.png) / [`.ico`](icons/green-error.ico) |
| `40` | Synchronisation en cours | oui | non | aucun | "mutagen is syncing" | [`green-sync.png`](icons/green-sync.png) / [`.ico`](icons/green-sync.ico) |
| `40` | Synchronisation en cours, tout juste mis à jour | oui | **oui** | aucun | "mutagen is syncing (updated)" | [`green-success.png`](icons/green-success.png) / [`.ico`](icons/green-success.ico) |
| `30` | Analyse en cours | oui | non | aucun | "mutagen is scanning" | [`green-scan.png`](icons/green-scan.png) / [`.ico`](icons/green-scan.ico) *(⚠ placeholder — la ressource n'a jamais existé, §6.1)* |
| `30` | Analyse en cours, tout juste mis à jour | oui | **oui** | aucun | "mutagen is scanning (updated)" | [`green-success.png`](icons/green-success.png) / [`.ico`](icons/green-success.ico) |
| `100/60/50/40/30` | N'importe lequel des états ci-dessus, obsolète (palier Info) | oui | — | **Info** | "mutagen is watching for changes (stale)" | [`green-timeout-white.png`](icons/green-timeout-white.png) / [`.ico`](icons/green-timeout-white.ico) *(⚠ placeholder — la ressource n'a jamais existé, §6.1)* |
| `100/60/50/40/30` | N'importe lequel des états ci-dessus, obsolète (palier Warning) | oui | — | **Warning** | "mutagen is watching for changes (stale)" | [`green-timeout.png`](icons/green-timeout.png) / [`.ico`](icons/green-timeout.ico) |
| `100/60/50/40/30` | N'importe lequel des états ci-dessus, obsolète (palier Error) | oui | — | **Error** | "mutagen is watching for changes (stale)" | [`green-timeout-red.png`](icons/green-timeout-red.png) / [`.ico`](icons/green-timeout-red.ico) *(⚠ placeholder — la ressource n'a jamais existé, §6.1)* |
| `-1` | Aucune session trouvée, en cours de récupération | **oui** | — | — | "mutagen is not running (starting)" | [`darkgray-restart.png`](icons/darkgray-restart.png) / [`.ico`](icons/darkgray-restart.ico) |
| `-1` | Aucune session trouvée, mise en pause par l'utilisateur | **non** | — | — | "mutagen is not running (disabled)" | [`darkgray.png`](icons/darkgray.png) / [`.ico`](icons/darkgray.ico) |
| `-2` | Connexion impossible, en cours de récupération | **oui** | — | — | "error (starting)" | [`orange-restart.png`](icons/orange-restart.png) / [`.ico`](icons/orange-restart.ico) |
| `-2` | Connexion impossible, mise en pause par l'utilisateur | **non** | — | — | "error (disabled)" | [`orange.png`](icons/orange.png) / [`.ico`](icons/orange.ico) |

### 3.1 Inventaire des ressources d'icônes

Les 15 bitmaps référencés ci-dessus sont fournis dans deux formats dans
[`requirements/icons/`](icons), afin que l'implémentation .NET dispose de
tout ce dont elle a besoin sans avoir à lire `python/img/` :

- **`.png`** — l'image source, conservée uniquement pour que le tableau
  ci-dessus et les aperçus GitHub s'affichent en ligne (la plupart des
  visualiseurs Markdown ne rendent pas les `.ico`). **Ce n'est pas** le
  format chargé par l'application .NET.
- **`.ico`** — mis en forme carrée avec remplissage transparent (padding)
  jusqu'à la plus grande des deux dimensions largeur/hauteur, puisque les
  PNG source ne sont pas carrés (par exemple 512×390), puis converti en une
  icône multi-résolution (16/32/48/256 px) avec
  `magick convert -define icon:auto-resize=16,32,48,256`. C'est le format
  que l'implémentation .NET **DOIT** charger à l'exécution, via
  `TaskbarIcon.Icon` (un `System.Drawing.Icon`), **et non**
  `TaskbarIcon.IconSource` (un `ImageSource` WPF). Raison : la barre système
  Windows (`Shell_NotifyIcon`) nécessite un `HICON` natif quelle que soit
  la propriété utilisée ; le setter de `IconSource` résout cela en
  convertissant l'`ImageSource` en `Icon` en interne (la méthode
  `StreamExtensions.ToSmallIcon` de H.NotifyIcon.Wpf), et cette conversion
  GDI+ lève une exception `ArgumentException: Argument 'picture' must be a
  picture that can be used as a Icon.` pour certains PNG (observé en
  pratique lors de la vérification manuelle de la Phase 1). Charger
  directement un véritable `.ico` via la propriété `Icon` contourne
  entièrement cette conversion.
- **11 images source réelles et vérifiées** copiées sans modification
  depuis le dossier legacy `python/img/` : `green`,
  `green-success`, `green-stop`, `green-conflict`, `green-error`,
  `green-sync`, `green-timeout`, `darkgray`, `darkgray-restart`, `orange`,
  `orange-restart`.
- **1 ressource spécifique à WPF, dérivée d'une image legacy** :
  `lightgray-init` (code `0`, « en attente du premier statut ») reprend le
  `lightgray.png` legacy avec un badge ajouté — le même badge en cercle
  bleu déjà utilisé pour `darkgray-restart`/`orange-restart`, mais avec
  trois points au lieu de la flèche de redémarrage — afin que la toute
  première icône affichée au démarrage du processus (avant même le
  chargement de la config/session et la construction du conteneur DI, voir
  `App.xaml.cs`) se lise visiblement comme « en cours d'initialisation »
  plutôt que comme une icône figée/défectueuse. Renommée depuis le
  `lightgray` legacy car ce nom ne décrivait plus ce que montre l'icône.
  C'est une amélioration délibérée propre à la réécriture, pas un
  comportement legacy à préserver — `python/` continue d'utiliser son
  `lightgray.png` non modifié.
- **3 placeholders générés** pour des ressources référencées par `icon.py`
  mais qui **n'ont jamais été présentes** dans `python/img/`, même dans
  l'application legacy (un bug réel et préexistant — §6.1) :
  `green-scan`, `green-timeout-white`, `green-timeout-red`. Ce sont de
  simples substituts en forme de cercle coloré (vert avec une bande de
  balayage pour l'analyse ; vert pâle/blanc ; rouge) suffisants pour
  débloquer la construction de la machine à états complète, mais ce
  **ne sont pas** des ressources de design finales — il faudra les
  remplacer par des icônes correctement conçues (idéalement une source
  vectorielle/SVG mise à l'échelle par DPI, voir §8) avant la livraison.
  Régénérer leur `.ico` en même temps que toute refonte.
- Les fichiers legacy inutilisés dans `python/img/` (`blue.png`,
  `cyan.png`, `folder.png`, `gray.png`, `remote-connection.png`,
  `resolve.png`, `status.png`, `yellow.png`, ainsi que les variantes
  numérotées d'images d'animation `green-stop2..5.png`,
  `green-sync2.png`, `green-timeout2/3/5.png`) ne sont **référencés nulle
  part dans le code source** et n'ont intentionnellement **pas** été
  copiés — ce sont des scories legacy, hors périmètre de cette exigence.

Chaque infobulle est préfixée par le nom d'application configuré
(`TRAY_TOOLTIP`, valeur par défaut "MutagenMon") suivi de `: `, par exemple
`"MutagenMon: mutagen is watching for changes"`.

Notez l'ordre de priorité délibéré lorsque plusieurs conditions pourraient
s'appliquer simultanément : **l'obsolescence prime sur « mis à jour »**
(une session ne peut pas être à la fois « tout juste rafraîchie » et « pas
rafraîchie depuis un moment »), et **les conflits/problèmes priment sur
« prêt »** même si le profil a également été mis à jour — un utilisateur ne
doit jamais voir une icône verte « prêt/mis à jour » faussement rassurante
alors que des conflits sont en attente.

## 4. Exigences relatives à l'infobulle

- TIC-5 : L'infobulle DOIT toujours être une phrase courte, sur une seule
  ligne, lisible par un humain (pas de codes bruts/JSON) — voir la colonne
  ci-dessus pour la formulation exacte par état.
- TIC-6 : L'infobulle DOIT se mettre à jour en parfaite synchronisation avec
  l'icône (même tick, sans jamais afficher une icône obsolète avec une
  infobulle fraîche, ou l'inverse).

## 5. Exigences d'interaction

- TIC-7 (clic principal / clic gauche) : DOIT ouvrir la vue de statut
  détaillée (FR-8) — une lecture synchrone, à la demande, du texte de
  statut complet, non mise en cache depuis le dernier tick.
  - S'il existe des conflits non résolus, cette vue DOIT proposer de
    lancer le workflow de résolution de conflits (FR-9) comme action
    principale, avec une action secondaire clairement identifiée
    « ignorer » (dismiss).
  - S'il n'y a pas de conflits non résolus, ce DOIT être une simple vue
    informative avec une unique action d'ignorer.
- TIC-8 (clic secondaire / clic droit) : DOIT ouvrir un menu contextuel
  (FR-7) contenant, dans l'ordre : recharger/redémarrer, bascule
  démarrer/arrêter (libellée selon l'état d'activation courant),
  séparateur, afficher le statut, séparateur, quitter — se réduisant à
  simplement « Restarting… » (désactivé) + « Exit » pendant qu'un
  redémarrage est en cours.

## 6. Exigences de robustesse

- TIC-9 : Si l'OS échoue à (ré)installer l'icône de la barre système après
  un appel `set icon`, l'application DOIT traiter cela comme fatal pour le
  processus courant et déclencher un auto-redémarrage complet (nouveau
  processus lancé, l'actuel se termine) plutôt que de continuer avec une
  icône manquante ou défectueuse.
- TIC-10 : Si la dernière mise à jour réussie du poller (sondeur) en
  arrière-plan dépasse le seuil d'obsolescence `Restart` (90 s par défaut),
  l'application DOIT s'auto-redémarrer même si l'icône de la barre système
  elle-même semble toujours correcte — une icône qui ne se met plus à jour
  mais affiche toujours un ancien état « prêt » est pire qu'un redémarrage
  visible.

## 7. Lacunes connues à combler dans la réécriture (à ne pas reproduire silencieusement)

Il s'agit de bugs/incohérences constatés dans l'implémentation actuelle. La
réécriture WPF DOIT prendre une décision explicite sur chacun d'eux
(corriger par défaut, sauf s'il existe une raison de préserver
intentionnellement le comportement legacy pour assurer une parité pendant
une période de transition).

1. **Ressources d'icônes manquantes référencées dans le code** :
   `green-scan.png`, `green-timeout-white.png` et `green-timeout-red.png`
   sont référencées par la logique de statut mais n'existent pas dans
   `python/img/`. L'état de scan et deux des trois paliers d'obsolescence
   n'ont actuellement aucun bitmap réel. [`requirements/icons/`](icons)
   fournit des placeholders générés pour ces trois ressources (§3.1),
   uniquement pour débloquer la réécriture sans toucher à `python/` — ce
   ne sont **pas** des ressources de design approuvées. La réécriture DOIT
   livrer une ressource complète et correctement conçue pour chaque ligne
   du tableau du §3 (par exemple sous forme d'icônes SVG/vectorielles mises
   à l'échelle par DPI en .NET, plutôt que des PNG de taille fixe) avant la
   livraison.
2. **L'état « prêt + mis à jour » est inatteignable** : en raison de la
   structure de la condition legacy (`if worst_code == 100 and not
   updated_profile` sans `elif` ultérieur retestant le code `100`), le
   flash « tout juste synchronisé, tout est à jour » ne s'affiche en
   réalité jamais lorsque l'état agrégé est « prêt », alors même que c'est
   l'état qui bénéficierait intuitivement le plus d'un flash « tout juste
   mis à jour ». La réécriture DOIT rendre cet état atteignable pour chaque
   état de base du §3, et non uniquement pour synchronisation/analyse.
3. **L'infobulle générique d'obsolescence perd le contexte** : les trois
   paliers d'obsolescence utilisent tous la même formulation « watching for
   changes (stale) », que le dernier état connu ait été conflits,
   problèmes, synchronisation ou analyse. La réécriture DEVRAIT conserver
   le nom du dernier état connu dans l'infobulle d'obsolescence, par
   exemple `"mutagen has conflicts (stale, no update for 32s)"`.
4. **Aucune protection de déduplication sur `SetIcon`** : le code legacy
   redéfinit le bitmap de l'icône à chaque tick, indépendamment du fait que
   l'état ait réellement changé (une vérification de déduplication existe
   dans le code source mais est commentée). La réécriture DEVRAIT
   n'appeler l'API de la barre système du système d'exploitation que
   lorsque l'état visuel change réellement, afin de minimiser le
   scintillement et la charge au niveau de l'OS.

## 8. Implication pour WPF (voir aussi 05-wpf-migration-notes.md)

- **WPF n'a pas d'API native pour l'icône de la barre système** —
  l'équivalent de `wx.adv.TaskBarIcon` doit provenir d'une intégration
  plateforme telle que `NotifyIcon` (interopérabilité WinForms/WPF) ou
  d'une bibliothèque dédiée à l'icône de la barre système
  (`H.NotifyIcon.Wpf`). C'est une **décision architecturale déterminante**
  pour la réécriture et elle doit être validée tôt (voir le document des
  notes de migration), car tout ce document dépend du fait que cet hôte
  prenne en charge : un remplacement du bitmap de l'icône par DPI à une
  cadence ≤ 1 s, une infobulle, un événement de clic gauche distinct de
  l'ouverture du menu contextuel, et un menu contextuel natif (ou un
  équivalent en popup rapide) sans nécessiter qu'une fenêtre d'application
  complète soit visible.
- La ou les fenêtres utilisées pour la vue de statut détaillée (FR-8) et
  la résolution de conflits (FR-9) peuvent être affichées dans une petite
  fenêtre popup/hôte ouverte à la demande depuis l'icône de la barre
  système, reflétant le modèle legacy « pas de fenêtre principale,
  boîtes de dialogue à la demande » (NFR-7).
