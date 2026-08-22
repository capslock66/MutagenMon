# Tests d'acceptation utilisateur — réécriture .NET WPF

Script de tests manuels, clic par clic, pour vérifier que chaque exigence
de ce dossier `requirements/` fonctionne réellement dans l'application
`dotNet/` en cours d'exécution. Chaque test est une courte liste
alternant **actions** (ce que vous faites) et **résultats attendus** (ce
que vous devez observer) — suivez-les dans l'ordre et comparez ce que
vous voyez avec les lignes de résultat attendu.

- **Windows uniquement** : l'icône de la barre d'état système et chaque
  fenêtre décrite ici ne fonctionnent que sous Windows (WPF). Démarrez
  l'application avec `dotnet run --project src/MutagenMon.App` depuis
  `dotNet/`, ou ouvrez `MutagenMon.sln` dans Visual Studio, définissez
  **MutagenMon.App** comme projet de démarrage, puis appuyez sur F5 —
  voir [dotNet/README.md](../dotNet/README.md).
- Chaque test référence l'identifiant d'exigence qu'il vérifie (par ex.
  `FR-9.2`, `TIC-7`) afin qu'un échec puisse être retracé jusqu'à
  [01-functional-requirements.fr.md](01-functional-requirements.fr.md) ou
  [03-tray-icon-requirements.fr.md](03-tray-icon-requirements.fr.md).
- ✅ = implémenté aujourd'hui, exécutez ce test. ⏳ = **pas encore
  implémenté** — ne l'exécutez pas, ne le signalez pas comme un bug ; il
  n'est listé ici que pour que ce document reste la référence complète et
  unique au fur et à mesure que les phases suivantes avancent (voir
  [05-wpf-migration-notes.fr.md §6](05-wpf-migration-notes.fr.md#6-livraison-par-phases-proposée)
  pour ce qui est prévu ensuite).

## 0. Préparation de l'environnement de test

* Installer une version de [mutagen.io](https://github.com/mutagen-io/mutagen)
  et placer le binaire à `src/MutagenMon.App/mutagen/mutagen.exe` (ou
  faire pointer `MUTAGEN_PATH` dans `config/config_mutagenmon.json` vers
  celui-ci).
* Créer deux dossiers locaux pour la session d'exemple, par ex.
  `C:\MutagenMonTest\alpha` et `C:\MutagenMonTest\beta`, et modifier
  `src/MutagenMon.App/mutagen/mutagen-create.bat` pour y faire référence
  (l'exemple fourni utilise déjà ces deux chemins).
* Ouvrir `src/MutagenMon.App/config/config_mutagenmon.json` dans un
  éditeur de texte — plusieurs tests ci-dessous demandent de modifier une
  clé précise à cet endroit, puis de redémarrer l'application.
* Garder `log/mutagenMon.log` (ou le dossier nommé par `LOG_PATH`) ouvert
  dans un visualiseur qui se rafraîchit automatiquement (par ex.
  PowerShell : `Get-Content mutagenMon.log -Wait -Tail 20`) afin de
  pouvoir vérifier les entrées de journal sans arrêter l'application.
* Démarrer l'application. L'icône de la barre d'état système doit
  apparaître en une seconde ou deux.

## FR-1 — Chargement de la configuration des sessions

**UT-1.1 — Les sessions sont chargées depuis `mutagen-create.bat`
(FR-1.1)** ✅

* Ouvrir `mutagen/mutagen-create.bat` et vérifier qu'il contient une
  ligne `mutagen sync create ... --name=<name> ...` non préfixée par
  `rem ` par session à surveiller, plus au moins une ligne préfixée par
  `rem `.
* Démarrer MutagenMon.
* Clic droit sur l'icône de la barre d'état système.
* Cliquer sur « Show status ».
* Une fenêtre s'affiche avec un bloc « Name: / Status: / Alpha: / Beta: »
  par ligne de session active (non `rem`), et aucun bloc pour la ligne
  préfixée par `rem`.

**UT-1.2 — Les noms de session en double sont détectés et une seule
définition est conservée (FR-1.2)** ✅ *(uniquement journalisé dans cette
réécriture — voir la remarque ci-dessous)*

* Modifier `mutagen-create.bat` et dupliquer une ligne `--name=<name>` de
  sorte que le même nom apparaisse deux fois.
* Démarrer MutagenMon.
* Ouvrir `log/mutagenMon.log`.
* Une ligne d'avertissement « Duplicate session name in ...: `<name>` »
  est présente.
* Clic droit sur l'icône de la barre d'état système.
* Cliquer sur « Show status ».
* `<name>` apparaît exactement une fois dans la vue de statut, pas deux.
* Remarque : l'ancienne application Python affiche une boîte de dialogue
  modale d'avertissement au démarrage dans ce cas ; la réécriture .NET
  actuelle se contente de journaliser l'avertissement — aucune fenêtre
  contextuelle n'est attendue pour l'instant.

**UT-1.3 — Le fichier de configuration tolère les lignes de commentaire
`#` (FR-1.3)** ✅

* Ouvrir `config/config_mutagenmon.json` et vérifier que plusieurs lignes
  commencent par `#`.
* Démarrer MutagenMon.
* L'icône de la barre d'état système apparaît normalement.
* Aucune boîte de dialogue d'erreur de démarrage n'est affichée.

## FR-2/FR-3/FR-4 — Sondage, classification des sessions, statut agrégé

**UT-2.1 — Le sondage en arrière-plan tourne à intervalle fixe
(FR-2.1)** ✅

* Démarrer MutagenMon avec au moins une session configurée.
* Ouvrir `log/mutagenMon.log` et observer sa mise à jour.
* Un nouveau bloc de sortie brute `mutagen sync list` est ajouté environ
  une fois par seconde (`MUTAGEN_POLL_PERIOD`, par défaut 1000 ms).

**UT-2.2 — Le statut agrégé est le pire de toutes les sessions
(FR-4.1)** ✅

* Configurer deux sessions, synchronisant normalement toutes les deux.
* Provoquer un conflit sur une seule d'entre elles (voir la procédure de
  l'UT-9 ci-dessous) pendant que l'autre reste « Watching for changes ».
* Clic gauche sur l'icône de la barre d'état système.
* Une fenêtre s'affiche montrant une session en conflit et l'autre saine.
* L'icône de la barre d'état système elle-même (avant d'ouvrir cette
  fenêtre) affiche l'icône Conflicts, pas l'icône Ready — la pire session
  l'emporte même si l'autre va bien.

## Icône de la barre d'état système (FR-5, TIC-1..10 — [03-tray-icon-requirements.fr.md](03-tray-icon-requirements.fr.md))

**UT-T.1 — Icône initiale « en attente de statut » (TIC-3)** ✅

* Démarrer MutagenMon.
* Observer l'icône de la barre d'état système immédiatement, avant que le
  premier sondage ne soit terminé.
* L'icône est gris clair (`lightgray-init`).
* Survoler l'icône avec la souris.
* L'infobulle affiche « MutagenMon: waiting for status... ».

**UT-T.2 — État Ready (prêt)** ✅

* Attendre que chaque session configurée atteigne « Watching for
  changes » dans mutagen.
* Survoler l'icône de la barre d'état système.
* L'icône est vert uni.
* L'infobulle affiche « MutagenMon: mutagen is watching for changes ».

**UT-T.3 — Ready + flash « vient d'être mis à jour » (icône de barre
d'état système §3, lacune n°2 corrigée)** ✅

* Toutes les sessions étant Ready, modifier un fichier dans le dossier de
  test alpha (ou beta) pour que mutagen synchronise le changement.
* Observer l'icône pendant et juste après la synchronisation.
* L'icône passe brièvement à `green-success` avec l'infobulle
  « MutagenMon: mutagen is watching for changes (updated) ».
* Quelques secondes plus tard, l'icône revient au vert uni (« Ready »).

**UT-T.4 — État Syncing (synchronisation)** ✅

* Copier un fichier assez volumineux dans le dossier de test alpha pour
  que le staging/l'application prenne quelques secondes.
* Observer l'icône pendant le transfert.
* L'icône affiche `green-sync`.
* L'infobulle affiche « MutagenMon: mutagen is syncing ».

**UT-T.5 — État Scanning (analyse)** ✅

* Clic droit sur l'icône de la barre d'état système.
* Cliquer sur « Reload config & restart mutagen » pour que les sessions
  refassent une analyse complète.
* Observer l'icône pendant que mutagen affiche « Scanning files ».
* L'icône affiche le placeholder `green-scan` (un substitut généré, pas
  une ressource de design finale — voir
  [03-tray-icon-requirements.fr.md §3.1](03-tray-icon-requirements.fr.md)).
* L'infobulle affiche « MutagenMon: mutagen is scanning ».

**UT-T.6 — État Conflicts (conflits)** ✅

* Provoquer un conflit (voir la procédure de l'UT-9 ci-dessous).
* Survoler l'icône de la barre d'état système.
* L'icône affiche `green-conflict`.
* L'infobulle affiche « MutagenMon: conflicts ».

**UT-T.7 — État Problems (problèmes)** ✅

* Empêcher mutagen d'appliquer un changement d'un côté (par ex. rendre le
  fichier cible en lecture seule sur un point d'accès, puis le modifier
  de l'autre côté).
* Survoler l'icône de la barre d'état système.
* L'icône affiche `green-error`.
* L'infobulle affiche « MutagenMon: problems ».

**UT-T.8 — Arrêter le monitoring change l'icône (interaction avec
FR-7.2)** ✅

* Une session étant Ready, clic droit sur l'icône de la barre d'état
  système.
* Cliquer sur « Stop Mutagen sessions ».
* L'icône affiche `green-stop`.
* L'infobulle affiche « MutagenMon: mutagen is stopping ».
* Clic droit sur l'icône de la barre d'état système.
* Cliquer sur « Start Mutagen sessions ».
* L'icône finit par refléter à nouveau l'état réel de la session
  (remarque : la session arrêtée n'est pas relancée par ce simple bouton
  bascule — seul « Reload config & restart mutagen », ou un redémarrage
  manuel de l'application, la ramène ; la récupération automatique est la
  FR-13, pas encore implémentée).

**UT-T.9 — État « impossible de se connecter » / erreur** ✅

* Modifier `mutagen-create.bat` et faire pointer le point d'accès distant
  d'une session vers un hôte injoignable.
* Clic droit sur l'icône de la barre d'état système.
* Cliquer sur « Reload config & restart mutagen » pour appliquer le
  changement.
* Attendre que mutagen affiche « Connecting to ... » (ou « Waiting to
  connect ») pendant quelques sondages consécutifs.
* L'icône affiche `orange-restart`.
* L'infobulle affiche « MutagenMon: error (starting) ».
* Clic droit sur l'icône de la barre d'état système.
* Cliquer sur « Stop Mutagen sessions ».
* L'icône passe à `orange`.
* L'infobulle affiche « MutagenMon: error (disabled) ».
* Restaurer ensuite le point d'accès fonctionnel dans
  `mutagen-create.bat` et recharger à nouveau.

**UT-T.10 — La péremption dégrade l'icône (FR-6.2)** ✅

* Renommer `mutagen.exe` (ou ce que pointe `MUTAGEN_PATH`) pour que le
  sondage commence à échouer.
* Observer l'icône de la barre d'état système pendant les deux minutes
  suivantes sans toucher à rien d'autre.
* Pendant les ~4 premières secondes (`STATUS_MAX_LAG.Info`), l'icône ne
  change pas encore.
* Entre ~4s et ~15s, l'icône affiche `green-timeout-white` (un
  placeholder généré — voir §3.1) avec un suffixe d'infobulle
  « (stale) ».
* Entre ~15s et ~50s, l'icône affiche `green-timeout`.
* Entre ~50s et ~90s, l'icône affiche `green-timeout-red` (également un
  placeholder).
* L'infobulle conserve le libellé du dernier état connu, par ex.
  « MutagenMon: mutagen is watching for changes (stale) », pas un message
  générique.

**UT-T.11 — Le seuil de redémarrage déclenche un auto-redémarrage complet
(FR-6.3)** ✅

* Poursuivre directement depuis l'UT-T.10, sans restaurer `mutagen.exe`.
* Attendre que l'ancienneté de péremption dépasse 90 secondes
  (`STATUS_MAX_LAG.Restart`).
* Le processus MutagenMon se redémarre lui-même : l'icône de la barre
  d'état système disparaît puis réapparaît, repartant de l'état
  « en attente de statut » (UT-T.1).
* `log/mutagenMon.log` contient une entrée de journal de redémarrage.
* Restaurer ensuite `mutagen.exe`/`MUTAGEN_PATH`.

**UT-T.12 — Le clic gauche ouvre la vue de statut (TIC-7)** ✅

* Clic gauche sur l'icône de la barre d'état système.
* La vue de statut détaillée s'ouvre (voir les tests FR-8 ci-dessous).

**UT-T.13 — Le clic droit ouvre le menu contextuel (TIC-8)** ✅

* Clic droit sur l'icône de la barre d'état système.
* Un menu s'affiche avec ces options, de haut en bas : « Reload config &
  restart mutagen », « Stop Mutagen sessions » (ou « Start Mutagen
  sessions » si le monitoring est actuellement désactivé), un séparateur,
  « Show status », un séparateur, « Exit MutagenMon ».

## FR-6 — Détection de péremption & auto-redémarrage

Couvert ci-dessus par l'UT-T.10 et l'UT-T.11.

## FR-7 — Menu contextuel de la barre d'état système & contrôle des sessions

**UT-7.1 — « Reload config & restart mutagen » (FR-7.1)** ✅

* Clic droit sur l'icône de la barre d'état système.
* Cliquer sur « Reload config & restart mutagen ».
* Clic droit sur l'icône de la barre d'état système immédiatement après.
* Un menu s'affiche avec un seul élément désactivé « Restarting... » et
  « Exit MutagenMon » uniquement — les autres éléments ont disparu.
* Attendre quelques secondes.
* Le processus redémarre : l'icône de la barre d'état système disparaît
  puis réapparaît.
* `log/mutagenMon.log` enregistre le redémarrage.
* Clic droit sur l'icône de la barre d'état système une fois qu'elle est
  revenue.
* Le menu complet (Reload / Stop-Start / Show status / Exit) est à
  nouveau affiché.

**UT-7.2 — « Stop Mutagen sessions » / « Start Mutagen sessions »
(FR-7.2)** ✅

* Clic droit sur l'icône de la barre d'état système.
* Cliquer sur « Stop Mutagen sessions ».
* Toutes les sessions en cours sont terminées (vérifier avec
  `mutagen sync list` dans un terminal, ou via « Show status »).
* Clic droit sur l'icône de la barre d'état système.
* L'élément affiche désormais « Start Mutagen sessions ».
* Cliquer sur « Start Mutagen sessions ».
* Clic droit sur l'icône de la barre d'état système.
* L'élément affiche à nouveau « Stop Mutagen sessions » (remarque : la
  session précédemment arrêtée n'est pas relancée par cette seule
  action — voir UT-T.8).

**UT-7.3 — « Show status » (FR-7.3)** ✅

* Clic droit sur l'icône de la barre d'état système.
* Cliquer sur « Show status ».
* La même vue de statut détaillée qu'un clic gauche (FR-8) s'affiche.

**UT-7.4 — « Exit MutagenMon » (FR-7.4)** ✅

* Clic droit sur l'icône de la barre d'état système.
* Cliquer sur « Exit MutagenMon ».
* L'icône de la barre d'état système disparaît immédiatement.
* Ouvrir le Gestionnaire des tâches.
* Le processus MutagenMon ne tourne plus, et aucune nouvelle instance ne
  démarre d'elle-même.

**UT-7.5 — Le menu se réduit à « Restarting... » pendant un redémarrage
(FR-7.5)**

Couvert ci-dessus par l'UT-7.1.

## FR-8 — Vue de statut détaillée

**UT-8.1 — Vue de statut sans conflit (FR-8.1/FR-8.3)** ✅

* Toutes les sessions étant saines et sans conflit, clic gauche sur
  l'icône de la barre d'état système.
* Une fenêtre s'affiche, avec pour titre l'infobulle actuelle de la barre
  d'état système (par ex. « MutagenMon: mutagen is watching for
  changes »).
* Le contenu de la fenêtre affiche un bloc « Name: / Status: / Alpha: /
  Beta: » par session configurée.
* Seul un bouton « OK » est affiché — pas de « Cancel », pas de
  « Resolve conflicts ».
* Cliquer sur « OK ».
* La fenêtre se ferme.

**UT-8.2 — Vue de statut avec conflits non résolus (FR-8.1/FR-8.2)** ✅

* Provoquer au moins un conflit non résolu (voir la procédure de l'UT-9
  ci-dessous).
* Clic gauche sur l'icône de la barre d'état système.
* Le contenu de la fenêtre affiche également une section
  « ==================== CONFLICTS ==================== » listant
  « `<session>: <fichier>` » pour le fichier en conflit (avec un suffixe
  « `[autoresolving]` » à la place si une règle `AUTORESOLVE` le
  concerne — voir FR-10 ci-dessous).
* Un bouton « Cancel » et un bouton « Resolve conflicts » sont tous deux
  affichés — pas de simple « OK ».
* Cliquer sur « Cancel ».
* La fenêtre se ferme sans démarrer la résolution de conflit.

## FR-9 — Résolution manuelle des conflits

**Procédure utilisée par chaque test ci-dessous** : pour provoquer un
véritable conflit, clic droit sur l'icône de la barre d'état système puis
cliquer sur « Stop Mutagen sessions » ; modifier le *même* fichier avec un
contenu différent directement dans le dossier alpha et dans le dossier
beta (ou son équivalent distant) ; puis cliquer à nouveau sur
« Start Mutagen sessions ». Mutagen détecte cela comme une modification à
deux côtés et signale un conflit à son prochain sondage.

**UT-9.1 — Entrée dans le lot de conflits et comparaison A/B (FR-9.1)** ✅

* Provoquer un conflit (voir la procédure ci-dessus).
* Clic gauche sur l'icône de la barre d'état système.
* Cliquer sur « Resolve conflicts ».
* Une fenêtre s'affiche avec le titre « MutagenMon: resolve file conflict
  1 of 1 » (ou « N of total » si plusieurs conflits sont en attente).
* Le contenu affiche le nom du fichier en conflit, et pour chacun de A et
  B : l'URL du point d'accès, la taille du fichier en octets, et
  l'horodatage de dernière modification.

**UT-9.2 — Le choix par défaut suit le côté modifié le plus récemment
(FR-9.3)** ✅

* Dans la fenêtre de l'UT-9.1, noter quel bouton radio, « A wins » ou
  « B wins », est présélectionné.
* Comparer les deux horodatages affichés au-dessus.
* L'option présélectionnée correspond au côté (A ou B) dont l'horodatage
  est le plus récent.

**UT-9.3 — Résolution « A wins » (FR-9.2)** ✅

* La boîte de dialogue de conflit étant ouverte, cliquer sur le bouton
  radio « A wins ».
* Cliquer sur « OK ».
* La copie du fichier côté B contient désormais le contenu de A
  (comparer directement les deux fichiers).
* Ouvrir `log/resolve.log`.
* Une nouvelle entrée est présente avec le nom de session, les deux URL,
  le nom de fichier, la méthode « A wins », et aucun tag « [AUTO] ».

**UT-9.4 — Résolution « B wins » (FR-9.2)** ✅

* Provoquer un nouveau conflit (voir la procédure ci-dessus).
* Ouvrir la boîte de dialogue de résolution et cliquer sur le bouton
  radio « B wins ».
* Cliquer sur « OK ».
* La copie du fichier côté A contient désormais le contenu de B.
* `log/resolve.log` contient une nouvelle entrée avec la méthode
  « B wins ».

**UT-9.5 — Résolution par fusion visuelle (FR-9.2)** ✅

* Définir `MERGE_PATH` dans `config_mutagenmon.json` vers un véritable
  outil de fusion (par ex. WinMerge) et redémarrer MutagenMon.
* Provoquer un conflit et ouvrir la boîte de dialogue de résolution.
* Cliquer sur le bouton radio « Visual merge ».
* Cliquer sur « OK ».
* L'outil de fusion configuré s'ouvre avec des copies locales de A et de
  B.
* Modifier et enregistrer le panneau de gauche (A) dans l'outil de
  fusion.
* Fermer l'outil de fusion.
* Une fenêtre de confirmation s'affiche, avec pour titre « MutagenMon:
  resolved file conflict », et pour contenu « Merged file copied to both
  sides: » suivi du nom du fichier.
* Cliquer sur « OK ».
* A et B contiennent désormais tous les deux le contenu fusionné.

**UT-9.6 — La fusion visuelle re-présente le conflit si rien n'a changé
(FR-9.2)** ✅

* Répéter l'UT-9.5, mais fermer l'outil de fusion sans modifier aucun des
  deux panneaux.
* Aucune fenêtre de confirmation ne s'affiche.
* Le même conflit est présenté à nouveau immédiatement, au lieu de passer
  silencieusement au suivant.

**UT-9.7 — Annuler interrompt tout le lot (FR-9.4)** ✅

* Provoquer deux conflits distincts.
* Clic gauche sur l'icône de la barre d'état système.
* Cliquer sur « Resolve conflicts ».
* Sur le premier conflit présenté, cliquer sur « Cancel ».
* Aucune autre fenêtre de conflit ne s'affiche.
* Aucun des deux fichiers n'a été modifié.

**UT-9.8 — Garde-fou du nombre maximal de conflits (FR-9.5)** ✅
*(nécessite 100+ conflits — facultatif si vous ne pouvez pas en produire
autant)*

* Provoquer plus de 100 conflits non résolus.
* Clic gauche sur l'icône de la barre d'état système.
* Cliquer sur « Resolve conflicts ».
* Une fenêtre s'affiche avec le titre « MutagenMon: resolve file
  conflict » et le contenu « Too many conflicts. You can restart
  resolving or resolve manually. » au lieu de la comparaison A/B
  habituelle.

**UT-9.9 — Indicateur « Connecting... » pour les points d'accès distants
(FR-9.6)** ✅

* Provoquer un conflit dont au moins un côté est un point d'accès SSH.
* Clic gauche sur l'icône de la barre d'état système.
* Cliquer sur « Resolve conflicts ».
* Une petite fenêtre sans bordure avec le texte « Remote connection... »
  s'affiche brièvement pendant la récupération des tailles/horodatages
  de fichiers.
* La fenêtre disparaît d'elle-même dès que la fenêtre de comparaison
  (UT-9.1) apparaît — elle n'est jamais fermée par l'utilisateur.

## FR-10 — Résolution automatique des conflits

**UT-10.1 — Une règle correspondante résout automatiquement sans aucune
invite (FR-10.1/FR-10.2)** ✅

* Arrêter MutagenMon.
* Modifier `config/config_mutagenmon.json` et définir :
  `"AUTORESOLVE": [{"filepath": "auto-resolve-test", "resolve": "A wins"}]`
* Démarrer MutagenMon.
* Provoquer un conflit (voir la procédure FR-9) sur un fichier dont le
  nom contient `auto-resolve-test`.
* Attendre un cycle de sondage (~1 seconde).
* Aucune fenêtre de résolution de conflit ne s'affiche.
* La copie du fichier côté B est automatiquement écrasée par le contenu
  de A.
* Ouvrir `log/resolve.log`.
* Une nouvelle entrée est présente avec la méthode « A wins » et le tag
  « [AUTO] ».

**UT-10.2 — La première règle correspondante l'emporte (FR-10.1)** ✅

* Arrêter MutagenMon.
* Modifier `config/config_mutagenmon.json` et définir :
  `"AUTORESOLVE": [{"filepath": "auto-resolve-test", "resolve": "A wins"}, {"filepath": "auto-resolve-test", "resolve": "B wins"}]`
* Démarrer MutagenMon et provoquer le même conflit que l'UT-10.1.
* La copie côté B est écrasée par le contenu de A (la première règle
  l'emporte), même si la seconde règle correspond également au nom du
  fichier.

**UT-10.3 — Le conflit est exclu du workflow manuel une fois auto-résolu
(FR-10.2)** ✅

* La règle de l'UT-10.1 étant active, provoquer un conflit correspondant.
* Clic gauche sur l'icône de la barre d'état système.
* Si la section CONFLICTS est encore visible, l'entrée porte l'annotation
  « `[autoresolving]` » ; le plus souvent, elle a déjà disparu
  entièrement de la liste.
* Cliquer à nouveau sur « Show status » (ou la rouvrir) si un second
  conflit sans rapport est encore en attente.
* Le fichier auto-résolu n'apparaît jamais dans le lot manuel
  « Resolve conflicts » (UT-9.1).

**UT-10.4 — La période de grâce empêche de retraiter le même conflit
(FR-10.3)** ✅

* La règle de l'UT-10.1 étant active et `AUTORESOLVE_HISTORY_AGE` à sa
  valeur par défaut (30 secondes), laisser l'UT-10.1 s'exécuter une fois.
* Noter l'horodatage de l'entrée `resolve.log` résultante.
* Sans modifier le fichier à nouveau, attendre moins de 30 secondes puis
  revérifier `resolve.log`.
* Aucune seconde entrée n'est apparue pour la même session/le même
  fichier — la période de grâce empêche le retraitement.
* Attendre que plus de 30 secondes se soient écoulées depuis la première
  entrée, puis modifier à nouveau le fichier en conflit de façon
  identique des deux côtés.
* Une nouvelle entrée `resolve.log` apparaît pour cette session/ce
  fichier une fois la période de grâce écoulée.

## FR-11 — Notifications de bureau ⏳ PAS ENCORE IMPLÉMENTÉ

Aucune étape de test — cette exigence n'a pas encore été construite (voir
[05-wpf-migration-notes.fr.md §6, Phase 4](05-wpf-migration-notes.fr.md#6-livraison-par-phases-proposée)).
Revenir à cette section une fois la FR-11 livrée.

## FR-12 — Détection de changement de profil de session

**UT-12.1 — Flash « vient d'être mis à jour » de l'icône au changement
d'archive (FR-12.1/FR-12.3)** ✅

Couvert ci-dessus par l'UT-T.3 — le flash « updated » de l'icône est le
seul effet visible pour l'utilisateur de cette exigence aujourd'hui.

**UT-12.2 — Notification de mise à jour de profil avec debounce
(FR-12.2, conditionne FR-11.4)** ⏳ PAS ENCORE IMPLÉMENTÉ

Aucune étape de test — la période de grâce/debounce
(`MUTAGEN_PROFILE_GRACE`) et la notification de bureau qu'elle
conditionnerait ne sont pas encore construites ; seul le signal brut, non
débouncé, derrière l'UT-12.1 existe aujourd'hui.

## FR-13 — Récupération automatique de session ⏳ PAS ENCORE IMPLÉMENTÉ

Aucune étape de test — voir
[05-wpf-migration-notes.fr.md §6, Phase 5](05-wpf-migration-notes.fr.md#6-livraison-par-phases-proposée).
Comme déjà observé aux UT-T.8/UT-7.2 : aujourd'hui, une session arrêtée
ou manquante n'est relancée que par « Reload config & restart mutagen »
ou un redémarrage manuel de l'application — jamais automatiquement.

## FR-14 — Journalisation & diagnostics

**UT-14.1 — Une exception non gérée est journalisée et affichée à
l'utilisateur (FR-14.1)** ✅

* Clic gauche sur l'icône de la barre d'état système pour ouvrir la vue
  de statut.
* Cliquer sur le bouton « Boum » (un bouton de test délibéré inclus
  spécifiquement pour re-vérifier ce chemin sans avoir besoin d'un
  véritable plantage).
* Une fenêtre bloquante s'affiche, avec pour titre « MutagenMon —
  error », et pour contenu « MutagenMon hit an unexpected error and will
  close: » suivi des détails de l'exception.
* Cliquer sur « OK ».
* L'application se ferme.
* `log/mutagenMon.log` contient une entrée de niveau Critical avec la
  même exception.

**UT-14.2 — Un échec de démarrage est journalisé et affiché (FR-14.1)** ✅

* Arrêter MutagenMon.
* Renommer `config/config_mutagenmon.json` (ou le modifier pour rendre le
  JSON invalide) afin que le chargement de la configuration échoue.
* Démarrer MutagenMon.
* Une fenêtre s'affiche, avec pour titre « MutagenMon — startup error »,
  et pour contenu « MutagenMon failed to start: » suivi des détails de
  l'exception.
* `log/mutagenMon.log` contient une entrée Critical correspondante.
* Restaurer ensuite le fichier de configuration.

**UT-14.3 — Le journal de résolution est un fichier distinct du journal
principal (FR-14.3, partiel)** ✅

Couvert ci-dessus par les UT-9.3/UT-9.4/UT-10.1 — `resolve.log` est
indépendant de `mutagenMon.log`.

**UT-14.4 — Journal de redémarrage dédié (FR-14.3, moitié
redémarrage)** ⏳ PAS ENCORE IMPLÉMENTÉ

Aucun `restart.log` dédié n'existe encore. L'unique mécanisme
d'auto-redémarrage implémenté aujourd'hui (chien de garde de péremption,
FR-6 — voir UT-T.11) journalise dans le même `mutagenMon.log` unique que
tout le reste.

**UT-14.5 — Filtre de verbosité (FR-14.2)** ⏳ PAS IMPLÉMENTÉ
*(délibérément — voir [05-wpf-migration-notes.fr.md §7](05-wpf-migration-notes.fr.md#7-journalisation))*

Aucune étape de test — `DEBUG_LEVEL` n'a aucun effet dans la réécriture,
par conception ; tous les niveaux de journal sont toujours écrits.

## FR-15 — Fonctionnement unique et permanent en arrière-plan

**UT-15.1 — Aucune fenêtre principale n'est jamais affichée (FR-15.1)** ✅

* Démarrer MutagenMon normalement.
* Ne pas cliquer sur l'icône de la barre d'état système.
* Vérifier la barre des tâches et Alt-Tab.
* Aucune fenêtre d'application ne s'affiche nulle part — seule l'icône de
  la barre d'état système est visible.

**UT-15.2 — Arrêt propre via Exit (FR-15.2)** ✅

Couvert ci-dessus par l'UT-7.4.

## Annexe — lacunes connues et acceptées (ne pas signaler comme des bugs)

Ce sont des limitations documentées et délibérées de la phase actuelle,
pas des défauts :

* Les trois icônes de barre d'état système « placeholder généré »
  (`green-scan`, `green-timeout-white`, `green-timeout-red`, vues aux
  UT-T.5/UT-T.10) sont de simples cercles colorés de substitution, pas
  des ressources de design finales — voir
  [03-tray-icon-requirements.fr.md §3.1/§7.1](03-tray-icon-requirements.fr.md).
* Les noms de session en double (UT-1.2) sont uniquement journalisés, pas
  affichés dans une fenêtre contextuelle — une déviation délibérée et
  actuellement acceptée par rapport à l'ancienne application.
* La FR-11 (notifications), la FR-12.2 (signal de mise à jour de profil
  avec debounce), la FR-13 (récupération automatique de session), la
  FR-14.2 (filtre de verbosité), et la moitié « redémarrage » de la
  FR-14.3 ne sont pas implémentées — voir les sections ⏳ ci-dessus et
  [05-wpf-migration-notes.fr.md §6](05-wpf-migration-notes.fr.md#6-livraison-par-phases-proposée)
  pour le plan.
