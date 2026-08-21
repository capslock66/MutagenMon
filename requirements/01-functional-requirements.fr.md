# Exigences fonctionnelles

Chaque exigence est dérivée directement du comportement de l'implémentation
wxPython actuelle, de sorte qu'elle puisse servir de spécification
exécutable pour la réécriture WPF. Les identifiants d'exigence sont des
identifiants stables destinés à être référencés depuis le code, les tests
et les PR.

## FR-1 — Chargement de la configuration des sessions

- FR-1.1 : L'application DOIT charger une liste de sessions de
  synchronisation à partir d'une source de définition de sessions
  (actuellement : les lignes correspondant à
  `mutagen sync create ... --name=<name> ...` dans
  `mutagen/mutagen-create.bat`, en ignorant les lignes commençant par
  `rem `).
- FR-1.2 : Les noms de session DOIVENT être uniques. Si un nom en double
  est détecté, l'application DOIT avertir l'utilisateur (actuellement :
  une boîte de dialogue modale au démarrage) et DOIT ne conserver qu'une
  seule définition pour ce nom.
- FR-1.3 : L'application DOIT charger un fichier de configuration JSON qui
  contrôle l'ensemble du comportement à l'exécution décrit dans ce
  document (période d'interrogation, seuils de retard, activation des
  notifications, chemins des outils externes, règles de résolution
  automatique). Le format DOIT tolérer les lignes de commentaire préfixées
  par `#`.

## FR-2 — Interrogation continue du statut des sessions

- FR-2.1 : Un processus/tâche en arrière-plan DOIT interroger le statut du
  moteur de synchronisation pour toutes les sessions configurées à
  intervalle fixe (`MUTAGEN_POLL_PERIOD`, par défaut 1000 ms),
  indépendamment du thread d'interface utilisateur.
- FR-2.2 : Pour chaque session, le résultat de l'interrogation DOIT être
  analysé pour en extraire au minimum : le texte de statut (par ex.
  « Watching for changes », « Scanning files », « Reconciling changes »,
  « Staging files on ... », « Applying changes », « Saving archive »,
  « Connecting to ... », « Waiting to connect », « Unknown »), un code de
  session numérique (voir FR-3), un indicateur de nom en double, un
  indicateur de problèmes, un indicateur de conflits, l'identifiant de
  session, et l'URL/le transport de chaque point de terminaison (local ou
  SSH).
- FR-2.3 : Le texte de statut brut complet et l'horodatage de la dernière
  interrogation réussie DOIVENT être conservés pour l'affichage et pour la
  détection d'obsolescence (FR-6).

## FR-3 — Classification du statut de session (code numérique)

Chaque session DOIT être classée dans exactement l'un des codes suivants à
chaque interrogation, selon cet ordre de priorité (une session restée trop
longtemps à l'état « connecting », ou dupliquée, est dégradée vers un code
d'erreur indépendamment des autres indicateurs) :

| Code | Signification | Déclencheur |
|---|---|---|
| `100` | Prêt / en attente de modifications | le statut commence par « Watching for changes » |
| `40` | Synchronisation en cours | le statut commence par « Waiting 5 seconds for rescan », « Reconciling changes », « Staging files on », « Applying changes », ou « Saving archive » |
| `30` | Analyse en cours | le statut commence par « Scanning files » |
| `-1` | Pas de session / non exécutée | aucun statut rapporté pendant 2 interrogations consécutives ou plus |
| `-2` | Connexion en cours / impossible de se connecter | le statut commence par « Connecting to », « Waiting to connect », ou « Unknown » pendant 2 interrogations consécutives ou plus, OU la session est dupliquée pendant 2 interrogations consécutives ou plus |
| `0` | Inconnu / en attente du premier statut | valeur initiale, avant que la première interrogation ne se termine |

Dégradations supplémentaires appliquées après le calcul du code de base :

- Si la session signale des **problèmes**, son code DOIT être plafonné à
  `50` (c'est-à-dire abaissé à 50 s'il était supérieur).
- Si la session signale des **conflits**, son code DOIT être plafonné à
  `60`.

## FR-4 — Statut agrégé (« le pire »)

- FR-4.1 : L'application DOIT calculer un statut agrégé unique égal au
  code numérique **minimum** parmi toutes les sessions configurées
  (« la pire session l'emporte »).
- FR-4.2 : Ce statut agrégé DOIT piloter l'icône de la zone de
  notification et son infobulle (voir
  [03-tray-icon-requirements.md](03-tray-icon-requirements.md) — l'exigence
  la plus importante de toute l'application).

## FR-5 — Icône de la zone de notification (voir document dédié)

La spécification complète se trouve dans
[03-tray-icon-requirements.md](03-tray-icon-requirements.md). Résumé :

- FR-5.1 : Une icône persistante dans la zone de notification/barre des
  tâches DOIT toujours être visible pendant l'exécution de l'application,
  reflétant le statut agrégé en temps réel (dans un délai d'un cycle
  d'interface utilisateur, soit environ 1 seconde).
- FR-5.2 : L'infobulle de l'icône DOIT contenir une description courte et
  lisible par un humain du statut agrégé actuel.
- FR-5.3 : Un clic gauche (action principale) sur l'icône DOIT ouvrir une
  vue de statut détaillée (FR-8).
- FR-5.4 : Un clic droit (action secondaire) sur l'icône DOIT ouvrir un
  menu contextuel (FR-7).
- FR-5.5 : Si le statut agrégé est obsolète depuis plus longtemps qu'un
  seuil configurable, l'icône DOIT communiquer visuellement une confiance
  dégradée dans le statut affiché (un état visuel « obsolète » distinct).

## FR-6 — Détection d'obsolescence et auto-redémarrage

- FR-6.1 : L'application DOIT suivre depuis combien de temps
  l'interrogation en arrière-plan a produit un résultat pour la dernière
  fois.
- FR-6.2 : Si cette durée dépasse des seuils configurables
  (`STATUS_MAX_LAG` : `Info`, `Warning`, `Error`, `Restart`, par défaut
  4/15/50/90 secondes), l'interface utilisateur DOIT dégrader
  progressivement l'état visuel de l'icône (Info → discret, Warning →
  visible, Error → marqué) même si les données sous-jacentes n'ont pas
  changé.
- FR-6.3 : Si la durée dépasse le seuil `Restart`, l'application DOIT
  journaliser la cause et se redémarrer entièrement (lancer une nouvelle
  instance de processus et quitter l'instance actuelle).
- FR-6.4 : Si l'icône de la zone de notification elle-même échoue à être
  installée/affichée par le système d'exploitation, l'application DOIT
  traiter cela de la même manière que FR-6.3 (auto-redémarrage).

## FR-7 — Menu contextuel de la zone de notification et contrôle des sessions

- FR-7.1 : Le menu contextuel DOIT proposer « Reload config & restart
  mutagen », qui arrête toutes les sessions, attend la confirmation de
  leur arrêt, puis déclenche un redémarrage complet de l'application
  (chemin FR-6.3).
- FR-7.2 : Le menu contextuel DOIT proposer une action bascule : « Stop
  Mutagen sessions » lorsque la surveillance est actuellement activée, ou
  « Start Mutagen sessions » lorsqu'elle est actuellement désactivée.
  - La désactivation DOIT arrêter (terminer) toutes les sessions en
    cours d'exécution.
  - L'activation DOIT reprendre la surveillance (les sessions manquantes
    ou arrêtées sont (re)démarrées par la logique de redémarrage normale,
    FR-9).
- FR-7.3 : Le menu contextuel DOIT proposer « Show status », équivalent à
  un clic gauche (FR-8).
- FR-7.4 : Le menu contextuel DOIT proposer « Exit MutagenMon », qui
  arrête le sondeur en arrière-plan et ferme l'application (sans
  auto-redémarrage).
- FR-7.5 : Pendant qu'un redémarrage est en cours, le menu DOIT remplacer
  les éléments démarrer/arrêter/recharger/afficher-le-statut par un seul
  élément désactivé « Restarting... », ne laissant disponible que
  « Exit ».

## FR-8 — Vue de statut détaillée

- FR-8.1 : Sur demande (clic gauche ou menu), l'application DOIT afficher
  une fenêtre/boîte de dialogue présentant le statut complet de chaque
  session configurée : le texte de statut brut par session, avec les
  identifiants et les URL de points de terminaison retirés pour plus de
  lisibilité, et, s'il existe des conflits non résolus automatiquement,
  une liste d'entrées `<session>: <file>` (annotées `[autoresolving]` pour
  les conflits qui seront résolus automatiquement) sous une section
  « CONFLICTS » clairement séparée.
- FR-8.2 : S'il existe des conflits non résolus, la vue DOIT proposer une
  action permettant de démarrer le flux de résolution de conflits (FR-9)
  en plus de la fermeture de la vue.
- FR-8.3 : S'il n'existe aucun conflit non résolu, la vue DOIT être
  purement informative (une seule action de fermeture).

## FR-9 — Résolution manuelle des conflits

- FR-9.1 : Pour chaque conflit non résolu, un par un (numéroté « N sur
  total »), l'application DOIT présenter : le chemin du fichier en
  conflit, et pour chaque côté (A/« alpha » et B/« beta ») l'URL du point
  de terminaison, la taille du fichier, et l'horodatage de dernière
  modification (récupéré localement ou via `stat` SSH pour les points de
  terminaison distants).
- FR-9.2 : L'utilisateur DOIT pouvoir choisir l'une des options
  suivantes : **Fusion visuelle** (ouvre un outil externe de diff/fusion
  sur des copies locales des deux fichiers, puis, si la copie locale « A »
  a été modifiée par l'outil, propage le résultat fusionné aux deux côtés
  et le confirme via un message), **A gagne** (copie la version de A sur
  B), ou **B gagne** (copie la version de B sur A).
- FR-9.3 : La boîte de dialogue DOIT présélectionner automatiquement
  « A gagne » ou « B gagne » selon le côté ayant l'horodatage de
  modification le plus récent, comme valeur par défaut suggérée que
  l'utilisateur peut modifier.
- FR-9.4 : L'annulation DOIT arrêter immédiatement l'ensemble du flux de
  résolution par lot (aucun autre conflit n'est présenté).
- FR-9.5 : Si plus de 100 conflits non résolus sont en attente,
  l'application DOIT refuser de démarrer le flux de résolution par lot et
  informer l'utilisateur qu'il doit résoudre les conflits manuellement ou
  redémarrer le processus.
- FR-9.6 : Pendant la copie/l'inspection d'un point de terminaison distant
  (SSH), l'application DOIT afficher un indicateur léger de « connexion en
  cours », qui disparaît automatiquement une fois l'opération terminée.
- FR-9.7 : Chaque résolution (manuelle ou automatique) DOIT être ajoutée à
  un journal de résolution comportant la session, les deux URL, le nom de
  fichier, la méthode, et si elle était automatique.

## FR-10 — Résolution automatique des conflits

- FR-10.1 : L'application DOIT prendre en charge une liste ordonnée et
  configurable de règles, chacune associant une expression régulière
  appliquée au chemin du fichier en conflit et une résolution
  (« A gagne » / « B gagne »).
- FR-10.2 : À chaque interrogation, chaque conflit nouvellement détecté
  DOIT être comparé aux règles dans l'ordre ; la première correspondance
  DOIT être appliquée automatiquement (sans interaction de l'utilisateur)
  et le conflit DOIT être marqué `autoresolved` afin d'être exclu du flux
  manuel (FR-9) et de la notification « nouveau conflit » (FR-11).
- FR-10.3 : Une fois qu'un conflit (identifié par session + nom de
  fichier) a été résolu automatiquement, il NE DOIT PAS être retraité
  pendant une période de grâce configurable
  (`AUTORESOLVE_HISTORY_AGE`, par défaut 30 s), afin d'éviter une boucle
  si l'outil sous-jacent signale de nouveau le même conflit avant que le
  moteur de synchronisation ne se mette à jour.
- FR-10.4 : Si les notifications pour la résolution automatique sont
  activées, chaque résolution automatique DOIT déclencher une
  notification (FR-11) nommant la règle appliquée et le fichier.

## FR-11 — Notifications de bureau

L'application DOIT être capable de déclencher des notifications au niveau
du système d'exploitation (bulle / toast, non modales, à disparition
automatique) pour, chacune activable indépendamment via la
configuration :

- FR-11.1 : **Nouveaux conflits** détectés depuis la dernière vérification
  (regroupés, une seule notification listant toutes les clés de conflit
  `session:file` nouvellement vues).
- FR-11.2 : **Résolution automatique de conflit** effectuée (FR-10.4).
- FR-11.3 : **Session redémarrée en raison d'une connexion bloquée**
  (c'est-à-dire que le seuil de compteur d'erreurs de l'état « connecting »
  a été atteint).
- FR-11.4 : **Profil/archive de session Mutagen mis à jour** sur le disque
  (c'est-à-dire que l'archive de synchronisation sous-jacente d'une
  session a changé), par nom de session.

## FR-12 — Détection des changements de profil de session

- FR-12.1 : À un intervalle configurable
  (`MUTAGEN_PROFILE_DIR_WATCH_PERIOD`), pour chaque session activée,
  l'application DOIT surveiller l'horodatage de modification du fichier
  d'archive sur disque du moteur de synchronisation pour cette session.
- FR-12.2 : Un changement DOIT être temporisé (debounced) par une période
  de grâce (`MUTAGEN_PROFILE_GRACE`) avant d'être signalé comme une mise à
  jour réelle, afin d'éviter de réagir à des écritures successives
  rapides.
- FR-12.3 : Une mise à jour confirmée DOIT être exposée à la fois à la
  logique de l'icône de la zone de notification (comme une variante
  visuelle « mis à jour », voir la spécification de l'icône) et au système
  de notification (FR-11.4).

## FR-13 — Récupération automatique des sessions

- FR-13.1 : Si une session ne renvoie aucun résultat pendant plus de
  `SESSION_MAX_NOSESSION` interrogations consécutives, elle DOIT être
  redémarrée (arrêt + recréation).
- FR-13.2 : Si une session est détectée comme dupliquée pendant plus de
  `SESSION_MAX_DUPLICATE` interrogations consécutives, elle DOIT être
  redémarrée.
- FR-13.3 : Si une session reste dans un état « connecting » pendant plus
  de `SESSION_MAX_ERRORS` interrogations consécutives, elle DOIT être
  redémarrée.
- FR-13.4 : Chaque redémarrage automatique DOIT être ajouté à un journal
  de redémarrage accompagné de l'instantané de statut brut qui l'a
  déclenché.

## FR-14 — Journalisation et diagnostics

- FR-14.1 : Les exceptions non gérées DOIVENT être journalisées avec la
  trace complète dans un fichier de journal d'erreurs, et, à moins qu'un
  indicateur de débogage « journaliser vers la console à la place » ne
  soit défini, DOIVENT être affichées à l'utilisateur dans une boîte de
  dialogue d'erreur bloquante.
- FR-14.2 : Un niveau de verbosité configurable DOIT conditionner un
  journal de débogage distinct capturant les transitions d'état internes
  (0 = désactivé, jusqu'à 100 = verbosité maximale).
- FR-14.3 : Les redémarrages (FR-13) et les résolutions de conflits
  (FR-9/FR-10) DOIVENT être journalisés dans leurs propres fichiers de
  journal dédiés, indépendants du journal de débogage.

### Note d'implémentation pour la réécriture (simplification délibérée)

Les FR-14.1–14.3 ci-dessus décrivent le comportement existant (4 fichiers
distincts : `error.log`, `debug.log` conditionné par `DEBUG_LEVEL`,
`restart.log`, `resolve.log`). La réécriture .NET simplifie délibérément
ce comportement plutôt que de le reproduire à l'identique — voir
[05-wpf-migration-notes.md §7](05-wpf-migration-notes.md#7-logging)
pour la justification :

- **FR-14.1 (implémenté, Phase 1)** : satisfait — chaque exception non
  gérée (démarrage, thread d'interface utilisateur, threads en
  arrière-plan, exceptions de tâches non observées) est journalisée avec
  le détail complet de l'exception et toujours affichée à l'utilisateur
  via une `MessageBox` bloquante. L'indicateur existant « journaliser
  vers la console à la place » (`DEBUG_EXCEPTIONS_TO_CONSOLE` dans la
  configuration) est conservé comme clé de configuration pour la
  compatibilité mais n'a pour l'instant aucun effet dans la réécriture.
- **FR-14.2 (délibérément non reproduit)** : la réécriture utilise un
  unique récepteur de journal toujours actif capturant tous les niveaux
  (Debug et supérieurs), en permanence — pas de filtre de verbosité, pas
  de fichier de débogage séparé. `DEBUG_LEVEL` reste présent dans
  `config_mutagenmon.json` pour la compatibilité mais n'a actuellement
  aucun effet. Justification : le journal de débogage désactivé par
  défaut dans l'ancienne version a été la cause directe d'un véritable
  incident de diagnosticabilité lors de la vérification manuelle de la
  Phase 1 (une exception au démarrage n'a produit littéralement aucune
  sortie de journal, car la journalisation n'était même pas encore
  configurée au moment où elle a été levée) — un journal toujours actif
  vaut mieux que « se souvenir d'activer un indicateur après coup ».
- **FR-14.3 (différé)** : pas encore implémenté. Les fichiers de journal
  dédiés au redémarrage/à la résolution dépendent de FR-13 (exécution du
  redémarrage automatique de session) et de FR-9/FR-10 (résolution des
  conflits), qui ne sont pas encore construits (Phase 3/5 selon le plan
  par phases des notes de migration). D'ici là, le seul mécanisme
  d'auto-redémarrage *effectivement* implémenté en Phase 1 (le
  chien de garde d'obsolescence de l'icône de la zone de notification,
  FR-6) journalise dans le même fichier unique que tout le reste.

## FR-15 — Fonctionnement en arrière-plan unique et permanent

- FR-15.1 : L'application est destinée à s'exécuter en continu (par ex.
  dès le démarrage du système d'exploitation) sans fenêtre persistante
  autre que l'icône de la zone de notification ; la « fenêtre » principale
  (le cas échéant) NE DOIT JAMAIS être affichée à l'utilisateur en
  fonctionnement normal.
- FR-15.2 : Les signaux d'arrêt gracieux (SIGINT/SIGTERM) DOIVENT
  entraîner un arrêt propre (arrêt de l'interrogation en arrière-plan,
  suppression de l'icône de la zone de notification) plutôt qu'un arrêt
  brutal.
