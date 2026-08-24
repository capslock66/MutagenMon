# Référence de configuration

Ce document est la source de vérité unique pour chaque clé de
configuration d'exécution lue depuis `config_mutagenmon.json`. Il existe
pour qu'un développeur implémentant la réécriture WPF à partir de
`requirements/` seul — **sans accès au code source `python/`** — n'ait
jamais besoin de deviner une valeur par défaut, une unité, ou quelle
exigence utilise quelle clé.

Le format de fichier historique tolère les lignes de commentaire
préfixées par `#` (FR-1.3), qui ne sont pas du JSON valide ; elles
DOIVENT être retirées avant l'analyse. Tous les seuils numériques de
« compte » ci-dessous sont exprimés en **interrogations consécutives**
sauf indication contraire, et toutes les valeurs de « période »/« âge »
sont dans l'unité donnée.

| Clé | Type | Valeur par défaut | Unité | Description | Utilisée par |
|---|---|---|---|---|---|
| `DEBUG_LEVEL` | entier | `0` | verbosité (0–100) | Conditionne le journal de débogage ; `0` le désactive, `100` est la verbosité maximale. | FR-14.2 |
| `DEBUG_EXCEPTIONS_TO_CONSOLE` | booléen | `false` | — | Si `true`, les exceptions non gérées sont affichées dans la console au lieu d'une boîte de dialogue d'erreur bloquante. | FR-14.1 |
| `NOTIFY_RESTART_CONNECTION` | booléen | `false` | — | Active la notification de bureau lorsqu'une session est redémarrée parce qu'elle était bloquée en « connecting » (FR-13.3). Ne conditionne PAS les cas de redémarrage « dupliqué » ou « pas de session » — voir la note FR-11.3 ci-dessous. | FR-11.3, FR-13.3 |
| `NOTIFY_CONFLICTS` | booléen | `true` | — | Active la notification « nouveaux conflits détectés ». | FR-11.1 |
| `NOTIFY_AUTORESOLVE` | booléen | `true` | — | Active la notification déclenchée quand un conflit est auto-résolu. | FR-10.4, FR-11.2 |
| `NOTIFY_MUTAGEN_PROFILE_UPDATE` | booléen | `false` | — | Active la notification par session quand l'archive de synchronisation a changé sur disque. | FR-11.4, FR-12.3 |
| `START_ENABLED` | booléen | `true` | — | Si `true`, la surveillance démarre à l'état « activé » (auto-redémarrage actif) plutôt qu'en pause. | FR-7.2, FR-15 |
| `MERGE_PATH` | chaîne (chemin) | `C:\Program Files (x86)\WinMerge\WinMergeU` | — | Chemin vers l'exécutable de l'outil externe de diff/fusion visuelle. | FR-9.2 |
| `SCP_PATH` | chaîne (chemin) | `C:\Program Files\Git\usr\bin\scp` | — | Chemin vers le binaire `scp` utilisé pour le transfert de fichiers distant (SSH) lors de la résolution de conflit. | FR-9.1, FR-9.2 |
| `SSH_PATH` | chaîne (chemin) | `C:\Program Files\Git\usr\bin\ssh` | — | Chemin vers le binaire `ssh` utilisé pour les appels `stat` distants (taille/horodatage de fichier). | FR-9.1 |
| `MUTAGEN_PATH` | chaîne (chemin) | `mutagen\mutagen` | — | Chemin vers l'exécutable CLI `mutagen`, utilisé à la fois pour l'interrogation (`sync list`) et le contrôle de session (`sync create`/`sync terminate`). | FR-1.1, FR-2.1, FR-13 |
| `TRAY_TOOLTIP` | chaîne | `"MutagenMon"` | — | Nom de l'application utilisé comme préfixe de chaque infobulle de la zone de notification (`"<TRAY_TOOLTIP>: <texte d'état>"`) et comme titre des boîtes de dialogue de statut/erreur. | FR-5.2, TIC-5 |
| `LOG_PATH` | chaîne (chemin) | `"log"` | — | Répertoire de base pour tous les fichiers journaux (`error.log`, `debug.log`, `restart.log`, `resolve.log` dans l'application historique — voir FR-14 et sa note de réécriture). | FR-14 |
| `MUTAGEN_SESSIONS_BAT_FILE` | chaîne (chemin) | `mutagen/mutagen-create.bat` | — | Chemin vers le fichier batch contenant une ligne `mutagen sync create ... --name=<name> ...` par session ; les lignes commençant par `rem ` sont ignorées. | FR-1.1 |
| `SESSION_MAX_ERRORS` | entier | `30000` | interrogations consécutives | Nombre d'interrogations consécutives pendant lesquelles une session peut rester dans l'état « connecting » (code `-2`, non dupliquée) avant d'être redémarrée. Avec la période d'interrogation par défaut de 1000 ms, cela représente **~8 h 20 min**. | FR-13.3 |
| `SESSION_MAX_NOSESSION` | entier | `200` | interrogations consécutives | Nombre d'interrogations consécutives pendant lesquelles une session peut ne renvoyer aucun résultat avant d'être redémarrée. Avec la période d'interrogation par défaut, cela représente **~3 min 20 s**. | FR-13.1 |
| `SESSION_MAX_DUPLICATE` | entier | `10000` | interrogations consécutives | Nombre d'interrogations consécutives pendant lesquelles une session peut être signalée comme un nom dupliqué avant d'être redémarrée. Avec la période d'interrogation par défaut, cela représente **~2 h 47 min**. | FR-13.2 |
| `MUTAGEN_POLL_PERIOD` | entier | `1000` | millisecondes | Intervalle entre deux appels `mutagen sync list` sur le thread/tâche d'interrogation en arrière-plan. | FR-2.1 |
| `STATUS_MAX_LAG` | objet `{Info, Warning, Error, Restart}` | `{"Info": 4, "Warning": 15, "Error": 50, "Restart": 90}` | secondes (par clé) | Âge du dernier résultat d'interrogation réussi, au-delà duquel l'icône de la zone de notification se dégrade vers le palier d'obsolescence correspondant (`Info`/`Warning`/`Error`), ou, au-delà de `Restart`, l'application entière se redémarre. | FR-6.2, FR-6.3, TIC-9/TIC-10 |
| `MUTAGEN_PROFILE_DIR` | chaîne (chemin) | `%USERPROFILE%\.mutagen` | — | Répertoire racine des propres données du moteur de synchronisation, contenant les fichiers d'archive par session surveillés pour la détection de « mise à jour » (`archives\<id de session>`). | FR-12.1 |
| `MUTAGEN_PROFILE_DIR_WATCH_PERIOD` | entier | `1` | secondes, ou `0` pour désactiver | Intervalle auquel l'horodatage de modification du fichier d'archive est revérifié. Implémenté comme un modulo sur le compteur du battement d'interface utilisateur à 1 Hz (c'est-à-dire qu'une valeur de `N` signifie « toutes les N interrogations de l'interface, pas toutes les N secondes de dérive d'horloge murale ») ; `0` désactive complètement la surveillance. | FR-12.1 |
| `MUTAGEN_PROFILE_GRACE` | entier | `4` | secondes | Fenêtre de temporisation (debounce) : une modification de l'archive n'est signalée comme une « mise à jour » confirmée qu'une fois qu'au moins ce nombre de secondes s'est écoulé depuis la précédente mise à jour confirmée, afin d'éviter de réagir à des écritures successives rapides. | FR-12.2 |
| `AUTORESOLVE` | tableau de `{filepath, resolve}` | `[]` (4 exemples d'entrées dans la configuration d'exemple fournie) | — | Liste ordonnée de règles de résolution automatique. `filepath` est une expression régulière comparée au chemin complet du fichier en conflit (répertoire + nom de fichier) ; `resolve` DOIT être la chaîne littérale `"A wins"` ou `"B wins"`. La première règle correspondante (dans l'ordre du tableau) l'emporte. | FR-10.1, FR-10.2 |
| `AUTORESOLVE_HISTORY_AGE` | entier | `30` | secondes | Une fois qu'une paire `(session, nom de fichier)` a été auto-résolue, elle n'est pas retraitée pendant cette durée, afin d'éviter une boucle de résolution pendant que le moteur de synchronisation se met à jour. | FR-10.3 |

## Notes pour la réécriture

- **Les compteurs d'interrogations consécutives se réinitialisent à
  chaque changement d'état**, pas seulement lors d'une récupération :
  l'implémentation historique réinitialise le compteur d'erreurs d'une
  session à `0` dès que sa paire (statut, indicateur de doublon) diffère
  de celle de l'interrogation précédente, même si le nouvel état est
  toujours anormal (par ex. passer de « connecting » à « pas de session »
  réinitialise le compteur plutôt que d'additionner les deux comptes).
  Seule une *série* de lectures anormales identiques compte pour le seuil.
  Voir FR-13 et
  [`update()` de monitor.py](../python/mutagenmonlib/remote/monitor.py)
  pour l'algorithme de référence.
- **La notification de redémarrage est volontairement incohérente dans
  l'application historique — ce n'est pas un bogue à « corriger »
  silencieusement sans décision** : parmi les trois causes de redémarrage
  de FR-13, seul le cas « connecting » (FR-13.3) est conditionné par
  `NOTIFY_RESTART_CONNECTION` ; le cas « dupliqué » (FR-13.2) déclenche
  *toujours* une notification, quel que soit l'indicateur de
  configuration, et le cas « pas de session » (FR-13.1) n'en déclenche
  *jamais*. Voir FR-11.3.
- Les expressions régulières d'`AUTORESOLVE` sont comparées avec la
  sémantique `re.search` de Python (recherche de sous-chaîne non ancrée,
  pas un ancrage sur le chemin complet) — un motif tel que `nohup\.out$`
  correspond n'importe où dans le chemin tant qu'il se termine par
  `nohup.out`.
- Aucune clé de ce fichier n'est censée contenir un secret (NFR-9) ;
  uniquement des chemins et des seuils.
