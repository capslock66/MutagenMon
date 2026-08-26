# Exigences fonctionnelles

Chaque exigence est dérivée directement du comportement de l'implémentation
wxPython actuelle, de sorte qu'elle puisse servir de spécification
exécutable pour la réécriture WPF. Les identifiants d'exigence sont des
identifiants stables destinés à être référencés depuis le code, les tests
et les PR.

## FR-1 — Chargement de la configuration des sessions

- FR-1.1 : L'application DOIT charger une liste de sessions de
  synchronisation à partir d'une source de définition de sessions : chaque
  ligne de `mutagen/mutagen-create.bat` (chemin configurable, voir
  `MUTAGEN_SESSIONS_BAT_FILE` dans
  [06-configuration-reference.md](06-configuration-reference.md)) qui NE
  commence PAS par `rem `, et dont un nom de session peut être extrait
  avec le motif `--name=(.*?) ` (une correspondance **paresseuse** jusqu'à
  la prochaine espace littérale) — c'est-à-dire que la sous-chaîne entre
  `--name=` et l'espace suivante devient le nom de session, et une ligne
  est silencieusement ignorée (ne produit aucune session) si `--name=` est
  absent ou n'est pas suivi d'une espace plus loin sur la même ligne. La
  **ligne correspondante entière** (pas seulement le nom) est conservée
  comme commande de démarrage de cette session, réutilisée telle quelle
  pour la (re)créer (FR-13.5).
- FR-1.2 : Les noms de session DOIVENT être uniques. Si un nom en double
  est détecté, l'application DOIT avertir l'utilisateur avec une boîte de
  dialogue modale bloquante, informative, à bouton OK unique, au démarrage
  (titre `"MutagenMon"`, corps
  `"<name> session name is duplicate in <chemin de MUTAGEN_SESSIONS_BAT_FILE>"`),
  une boîte de dialogue par doublon rencontré, et DOIT ne conserver que la
  **dernière** définition vue pour ce nom (chaque nouvelle ligne pour le
  même nom écrase la précédente).
- FR-1.3 : L'application DOIT charger un fichier de configuration JSON qui
  contrôle l'ensemble du comportement à l'exécution décrit dans ce
  document (période d'interrogation, seuils de retard, activation des
  notifications, chemins des outils externes, règles de résolution
  automatique). Le format DOIT tolérer les lignes de commentaire préfixées
  par `#`. Voir
  [06-configuration-reference.md](06-configuration-reference.md) pour
  chaque clé, son type, sa valeur par défaut et son unité.

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
| `-1` | Pas de session / non exécutée | aucun statut rapporté pendant 3 interrogations consécutives d'affilée (2, s'il s'agit de la toute première interrogation pour cette session — voir la note ci-dessous) |
| `-2` | Connexion en cours / impossible de se connecter | le statut commence par « Connecting to », « Waiting to connect », ou « Unknown » pendant 3 interrogations consécutives d'affilée, OU la session est dupliquée pendant 3 interrogations consécutives d'affilée |
| `0` | Inconnu / en attente du premier statut | valeur initiale, avant que la première interrogation ne se termine |

Le chiffre « 3 interrogations consécutives » découle du même compteur
d'interrogations anormales consécutives défini en
[FR-13](#fr-13--récupération-automatique-des-sessions) : le compteur vaut
`0` à la première interrogation où la condition anormale apparaît, `1` à
la deuxième, `2` à la troisième — et le code passe à `-1`/`-2` dès que le
compteur est `> 1`, c'est-à-dire à la troisième interrogation. La seule
exception concerne « pas de session » : le « dernier statut connu »
initial d'une session est la chaîne vide, qui est également le marqueur
interne utilisé pour « pas de session » — une session qui n'a *jamais*
signalé le moindre statut atteint donc le code `-1` après seulement 2
interrogations, pas 3, uniquement à cause de cette coïncidence de valeur
initiale, pas parce que la règle serait différente.

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

- FR-9.1 : Pour chaque conflit non résolu, un par un (titre de la boîte de
  dialogue `"MutagenMon: resolve file conflict <N> of <total>"`),
  l'application DOIT présenter : le chemin du fichier en conflit, et pour
  chaque côté (A/« alpha » et B/« beta ») l'URL du point de terminaison,
  la taille du fichier, et l'horodatage de dernière modification (récupéré
  localement ou via `stat` SSH pour les points de terminaison distants),
  présentés sous la forme `"A: <url1>\n<taille> bytes, <horodatage>"` (et
  de même pour B).
  > **Bizarrerie de l'ancienne version, à ne pas reproduire silencieusement** :
  > dans l'implémentation actuelle, `<total>` est en réalité le **nombre
  > de sessions configurées**, pas le nombre de conflits à résoudre (il
  > est calculé comme `len(conflicts_dict)`, et ce dictionnaire possède
  > toujours exactement une entrée par session configurée, vide ou non).
  > Avec 3 sessions et 7 conflits réels, la boîte de dialogue affiche
  > successivement « 1 of 3 », « 2 of 3 », ... « 7 of 3 » — `<N>` dépasse
  > légitimement `<total>`. La réécriture DEVRAIT plutôt calculer
  > `<total>` comme le véritable nombre de conflits non résolus (non
  > auto-résolus) sur l'ensemble des sessions, sauf demande explicite de
  > parité avec l'ancienne version.
- FR-9.2 : L'utilisateur DOIT pouvoir choisir l'une des options
  suivantes : **Fusion visuelle** (ouvre un outil externe de diff/fusion,
  `MERGE_PATH`, sur des copies locales des deux fichiers ; si le fichier
  de la copie locale « A » a ensuite été modifié — horodatage de
  modification changé —, le résultat fusionné est copié sur **les deux**
  côtés, et une boîte de dialogue de confirmation est affichée : titre
  `"MutagenMon: resolved file conflict"`, corps
  `"Merged file copied to both sides:\n\n<nom du fichier>"`), **A gagne**
  (copie la version de A sur B), ou **B gagne** (copie la version de B sur
  A).
- FR-9.3 : La boîte de dialogue DOIT présélectionner automatiquement
  « A gagne » ou « B gagne » selon le côté ayant l'horodatage de
  modification le plus récent, comme valeur par défaut suggérée que
  l'utilisateur peut modifier.
- FR-9.4 : L'annulation de la boîte de dialogue pour un conflit DOIT
  **ignorer uniquement ce conflit** et présenter immédiatement le conflit
  non résolu suivant du lot — elle NE DOIT PAS interrompre l'ensemble du
  lot. (Correction d'une description erronée dans des révisions
  antérieures de ce document : dans l'ancienne version,
  `resolve_single()` traite l'annulation exactement comme une résolution
  terminée — les deux font passer la boucle externe au conflit suivant —
  il n'existe aucun chemin de code qui interrompt le lot plus tôt, à part
  épuiser tous les conflits ou atteindre le plafond de FR-9.5.) La seule
  façon de laisser un conflit *non résolu pour y revenir plus tard* est de
  fermer/tuer l'application entière avant la fin du lot.
  > **⚠ Divergence d'implémentation découverte en corrigeant cette
  > exigence** : l'implémentation .NET actuelle
  > (`ConflictResolutionController.cs`,
  > [dotNet/src/MutagenMon.App](../dotNet/src/MutagenMon.App)) interrompt
  > délibérément l'ensemble du lot lors d'une annulation, en citant FR-9.4
  > dans son propre commentaire de documentation — c'est-à-dire qu'elle a
  > été construite d'après la formulation *précédente et incorrecte* de ce
  > document, et non d'après le comportement réel de l'ancienne
  > application. Il s'agit d'une véritable divergence par rapport à la
  > parité avec l'ancienne version, qui nécessite une décision explicite
  > (corriger le code pour qu'il corresponde à ce FR-9.4 corrigé, ou
  > assumer sciemment l'interruption sur annulation comme une amélioration
  > délibérée propre à la réécriture, et le documenter ici) — ce point n'a
  > pas été corrigé dans le cadre de cette passe de documentation.
- FR-9.5 : Si plus de 100 conflits non résolus (non auto-résolus) sont en
  attente, l'application DOIT cesser de présenter d'autres conflits (les
  100 premiers dans l'ordre d'itération sont tout de même résolus un par
  un normalement) et afficher une boîte de dialogue bloquante,
  informative, à bouton OK unique — titre
  `"MutagenMon: resolve file conflict"`, corps
  `"Too many conflicts. You can restart resolving or resolve manually"`
  — puis abandonner le reste du lot (les conflits restants demeurent non
  résolus jusqu'à la prochaine invocation du flux).
- FR-9.6 : Pendant la copie/l'inspection d'un point de terminaison distant
  (SSH), l'application DOIT afficher un indicateur léger de « connexion en
  cours », qui disparaît automatiquement une fois l'opération terminée.
- FR-9.7 : Chaque résolution (manuelle ou automatique) DOIT être ajoutée à
  un journal de résolution comportant la session, les deux URL, le nom de
  fichier, la méthode, et si elle était automatique.

## FR-10 — Résolution automatique des conflits

- FR-10.1 : L'application DOIT prendre en charge une liste ordonnée et
  configurable de règles (`AUTORESOLVE`, par défaut : liste vide — aucune
  règle préconfigurée), chacune associant une expression régulière
  appliquée au chemin du fichier en conflit et une résolution (la chaîne
  littérale `"A wins"` ou `"B wins"`). La correspondance DOIT être une
  recherche de sous-chaîne non ancrée sur le chemin complet (répertoire +
  nom de fichier), pas une correspondance de chaîne entière — par ex. un
  motif `nohup\.out$` correspond à tout chemin se terminant par
  `nohup.out`, quel que soit son répertoire. Voir
  [06-configuration-reference.md](06-configuration-reference.md) pour le
  schéma exact et des exemples de règles.
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
automatique) pour les événements suivants. Chacun est activable
indépendamment via la configuration **sauf mention contraire** — les
trois causes de redémarrage de FR-13 ne partagent délibérément PAS un
seul et même interrupteur :

- FR-11.1 : **Nouveaux conflits** détectés depuis la dernière vérification
  (regroupés, une seule notification listant toutes les clés de conflit
  `session:file` nouvellement vues). Interrupteur : `NOTIFY_CONFLICTS`
  (par défaut `true`).
- FR-11.2 : **Résolution automatique de conflit** effectuée (FR-10.4).
  Interrupteur : `NOTIFY_AUTORESOLVE` (par défaut `true`).
- FR-11.3 : **Session redémarrée parce qu'elle était bloquée en
  « connecting »** (seuil FR-13.3 atteint). Interrupteur :
  `NOTIFY_RESTART_CONNECTION` (par défaut `false`, c'est-à-dire que cette
  notification est **désactivée par défaut**).
- FR-11.3b : **Session redémarrée parce qu'elle a été détectée comme
  dupliquée** (seuil FR-13.2 atteint). Cette notification est **toujours
  déclenchée, sans aucun interrupteur de configuration** — elle NE DOIT
  PAS être conditionnée par `NOTIFY_RESTART_CONNECTION` ni par aucun
  autre indicateur.
- FR-11.3c : **Session redémarrée parce qu'aucune session n'a été
  trouvée** (seuil FR-13.1 atteint) NE déclenche AUCUNE notification, par
  conception — cette cause de redémarrage est silencieuse.
- FR-11.4 : **Profil/archive de session Mutagen mis à jour** sur le disque
  (c'est-à-dire que l'archive de synchronisation sous-jacente d'une
  session a changé), par nom de session. Interrupteur :
  `NOTIFY_MUTAGEN_PROFILE_UPDATE` (par défaut `false`).

Voir [06-configuration-reference.md](06-configuration-reference.md) pour
le tableau complet des valeurs par défaut.

## FR-12 — Détection des changements de profil de session

- FR-12.1 : À un intervalle configurable
  (`MUTAGEN_PROFILE_DIR_WATCH_PERIOD`, par défaut `1` seconde, `0`
  désactive complètement la surveillance), pour chaque session activée,
  l'application DOIT surveiller l'horodatage de modification du fichier
  d'archive sur disque du moteur de synchronisation pour cette session
  (`<MUTAGEN_PROFILE_DIR>/archives/<id de session>`, par défaut
  `%USERPROFILE%\.mutagen\archives\<id de session>` — le répertoire de
  données propre au moteur de synchronisation, distinct des chemins de
  configuration/journalisation de cette application). Si le fichier
  d'archive est introuvable (par ex. session pas encore créée), la
  surveillance DOIT être silencieusement réinitialisée (sans erreur) afin
  que la prochaine apparition future du fichier ne soit pas immédiatement
  signalée comme une « mise à jour ».
- FR-12.2 : Un changement DOIT être temporisé (debounced) par une période
  de grâce (`MUTAGEN_PROFILE_GRACE`, par défaut `4` secondes) avant d'être
  signalé comme une mise à jour réelle : une nouvelle modification n'est
  confirmée qu'une fois que la période de grâce s'est au moins écoulée
  depuis la *précédente modification confirmée* — afin d'éviter de réagir
  à des écritures successives rapides.
- FR-12.3 : Une mise à jour confirmée DOIT être exposée à la fois à la
  logique de l'icône de la zone de notification (comme une variante
  visuelle « mis à jour », voir la spécification de l'icône) et au système
  de notification (FR-11.4), lui-même activable indépendamment
  (`NOTIFY_MUTAGEN_PROFILE_UPDATE`, par défaut `false`) — une mise à jour
  confirmée peut piloter l'icône sans jamais produire de notification.

## FR-13 — Récupération automatique des sessions

Chaque session possède un **compteur d'interrogations anormales
consécutives**, réinitialisé à `0` à l'interrogation où une paire
(statut, indicateur de doublon) anormale apparaît pour la première fois
(même en provenance d'une paire anormale *différente*), puis incrémenté
de 1 à chaque interrogation suivante où cette même paire se répète sans
changement ; tout retour à une paire saine le réinitialise aussi à `0`.
Un seuil de redémarrage ci-dessous est franchi dès que ce compteur dépasse
(strictement `>`) la valeur configurée — concrètement, la condition
anormale doit être observée pendant `valeur configurée + 2`
interrogations consécutives d'affilée (l'interrogation 1 met le compteur
à `0`, l'interrogation 2 l'amène à `1`, ..., l'interrogation
`valeur configurée + 2` l'amène à `valeur configurée + 1`, qui est la
première valeur `> valeur configurée`). Voir
[06-configuration-reference.md](06-configuration-reference.md) pour les
valeurs par défaut exactes et le temps réel approximatif qu'elles
représentent avec la période d'interrogation par défaut de 1000 ms (le
« +2 » interrogations est négligeable à cette échelle et est absorbé dans
les approximations « ≈ » qui s'y trouvent).

- FR-13.1 : Si une session ne renvoie aucun résultat du tout pendant plus
  de `SESSION_MAX_NOSESSION` interrogations consécutives (par défaut
  `200`, ≈3 min 20 s), elle DOIT être redémarrée (arrêt + recréation,
  FR-13.5). Aucune notification n'est déclenchée pour cette cause
  (FR-11.3c).
- FR-13.2 : Si une session est détectée comme portant un nom dupliqué
  pendant plus de `SESSION_MAX_DUPLICATE` interrogations consécutives
  (par défaut `10000`, ≈2 h 47 min), elle DOIT être redémarrée. Une
  notification est **toujours** déclenchée pour cette cause, sans
  condition (FR-11.3b).
- FR-13.3 : Si une session reste dans un état « connecting » (statut
  commençant par « Connecting to », « Waiting to connect », ou « Unknown »
  — voir FR-3) pendant plus de `SESSION_MAX_ERRORS` interrogations
  consécutives (par défaut `30000`, ≈8 h 20 min), elle DOIT être
  redémarrée. Une notification n'est déclenchée que si
  `NOTIFY_RESTART_CONNECTION` est activé (par défaut `false`) — voir
  FR-11.3.
- FR-13.4 : Chaque redémarrage automatique, quelle que soit laquelle des
  trois causes ci-dessus, DOIT être ajouté à un journal de redémarrage
  accompagné de l'instantané de statut brut qui l'a déclenché et de la
  cause parmi les trois qui s'est déclenchée.
- FR-13.5 : L'action de redémarrage elle-même est : demander la
  terminaison de la session (`mutagen sync terminate <name>`), puis la
  recréer à partir de sa définition d'origine (FR-1.1). Les deux étapes
  DOIVENT tolérer l'échec de l'autre (par ex. l'échec de la terminaison
  parce que la session était déjà absente NE DOIT PAS empêcher la
  tentative de recréation). Après un redémarrage, le compteur
  d'interrogations anormales consécutives de la session DOIT être
  immédiatement réinitialisé à `0`, indépendamment du résultat de la
  prochaine interrogation.
  - **Raffinement propre à la réécriture** : lorsque la cause du
    redémarrage est FR-13.1 (aucune session du tout), l'étape de
    terminaison DOIT être entièrement sautée plutôt que tentée puis
    tolérée — la session est déjà connue comme absente, donc
    `mutagen sync terminate` serait un appel voué à l'échec, purement
    bruyant. Pour les deux autres causes (FR-13.2 doublon, FR-13.3
    connexion bloquée), la session est connue comme existante, donc la
    terminaison DOIT toujours être demandée et son échec DOIT toujours
    être toléré comme décrit ci-dessus. L'application Python historique
    tente toujours la terminaison sans condition
    (`mutagenmonlib/remote/mutagen.py: restart_session()`) ; ce
    raffinement est une amélioration délibérée propre au .NET, pas une
    exigence de parité avec l'historique.
- FR-13.6 : Les redémarrages automatiques (cette section) ne s'exécutent
  que tant que la surveillance est actuellement activée (FR-7.2) ; tant
  qu'elle est désactivée, aucune session n'est redémarrée, et toute
  session actuellement en cours d'exécution DOIT à la place être arrêtée
  (terminée, pas recréée) à la prochaine interrogation.

## FR-14 — Journalisation et diagnostics

> **Les FR-14.1–14.3 ci-dessous décrivent uniquement le comportement de
> l'ancienne version (Python).** La réécriture .NET ne reproduit
> délibérément PAS l'organisation en fichiers séparés — voir la « Note
> d'implémentation pour la réécriture » à la fin de cette section, en
> particulier le point FR-14.1, pour ce que l'application .NET écrit
> réellement, et où.

- FR-14.1 : Les exceptions non gérées DOIVENT être journalisées avec la
  trace complète dans un fichier de journal d'erreurs
  (`<LOG_PATH>/error.log`), et, sauf si `DEBUG_EXCEPTIONS_TO_CONSOLE` vaut
  `true` (par défaut `false`), DOIVENT être affichées à l'utilisateur dans
  une boîte de dialogue d'erreur bloquante à bouton OK unique, de titre
  `"MutagenMon error"` et dont le corps est le texte de la trace. Ceci
  s'applique uniformément à : l'échec des processus externes (code de
  sortie non nul ou échec de lancement de `mutagen`/de l'outil de fusion),
  l'échec de la (ré)installation de l'icône de la zone de notification
  (FR-6.4), et toute autre exception non gérée remontant en haut de la
  boucle principale.
- FR-14.2 : Un niveau de verbosité configurable (`DEBUG_LEVEL`, par défaut
  `0`) DOIT conditionner un journal de débogage distinct
  (`<LOG_PATH>/debug.log`) capturant les transitions d'état internes
  (0 = désactivé, jusqu'à 100 = verbosité maximale) — chaque ligne
  journalisée porte son propre niveau de verbosité, et seules les lignes
  dont le niveau est inférieur ou égal au `DEBUG_LEVEL` configuré sont
  écrites.
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

- **FR-14.1 (implémenté, Phase 1)** : satisfait, mais **pas** via un
  `error.log` dédié — ce fichier n'existe pas dans l'application .NET.
  Les exceptions sont journalisées, au niveau `Information` ou supérieur,
  dans le même fichier journal unifié unique que tout le reste (voir la
  note de réécriture de FR-14.2 ci-dessous) :
  `<LOG_PATH>/mutagenMon.log` par défaut, écrit via un `ILoggerProvider`
  fait maison (`FileLoggerProvider`, voir
  [App.xaml.cs](../dotNet/src/MutagenMon.App/App.xaml.cs)). Un second
  fichier, réellement séparé cette fois, `mutagenMon.fatal.log` (à côté de
  l'exécutable, pas sous `LOG_PATH`), existe uniquement comme filet de
  sécurité pour les échecs survenant avant que le chemin de
  `mutagenMon.log` lui-même ne puisse être résolu depuis la configuration,
  ou si l'écriture dans ce dernier échoue — il reste vide en
  fonctionnement normal et ce n'est pas là qu'il faut chercher les
  exceptions ordinaires. Chaque exception non gérée (démarrage, thread
  d'interface utilisateur, threads en arrière-plan, exceptions de tâches
  non observées, y compris celles levées à l'intérieur d'une frame de
  dispatcher imbriquée comme un menu contextuel ouvert — une bizarrerie
  connue de WPF contre laquelle la réécriture se protège spécifiquement)
  est journalisée avec le détail complet de l'exception et toujours
  affichée à l'utilisateur via une `MessageBox` bloquante. L'indicateur
  existant « journaliser vers la console à la place »
  (`DEBUG_EXCEPTIONS_TO_CONSOLE` dans la configuration) est conservé
  comme clé de configuration pour la
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
- **FR-14.3 (partiellement implémenté, Phase 3)** : la moitié consacrée à
  la résolution des conflits est faite — chaque résolution manuelle
  (FR-9) est ajoutée à un `resolve.log` dédié, indépendant du journal
  principal (`MutagenMon.Core/Resolution/ResolveLogWriter.cs`). La moitié
  consacrée au journal de redémarrage dépend toujours de FR-13
  (exécution du redémarrage automatique de session, Phase 5), pas encore
  construite ; d'ici là, le seul mécanisme d'auto-redémarrage
  *effectivement* implémenté en Phase 1 (le chien de garde d'obsolescence
  de l'icône de la zone de notification, FR-6) journalise dans le même
  fichier unique que tout le reste.

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
