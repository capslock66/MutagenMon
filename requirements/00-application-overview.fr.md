# MutagenMon — Aperçu de l'application et analyse du code source

## 1. Objectif

MutagenMon est un utilitaire de bureau multiplateforme qui supervise une ou
plusieurs sessions de synchronisation de fichiers
[mutagen.io](https://github.com/mutagen-io/mutagen). Il s'exécute en arrière-plan
avec une **icône de zone de notification (system tray)** comme interface
utilisateur principale (et, la plupart du temps, unique). Son objectif est de :

- démarrer/surveiller les sessions de synchronisation mutagen définies par l'utilisateur,
- afficher l'état de santé agrégé en temps réel de toutes les sessions via
  l'icône de la zone de notification et son infobulle,
- redémarrer automatiquement les sessions qui se bloquent, échouent ou  disparaissent,
- détecter les conflits de synchronisation et aider l'utilisateur à les
  résoudre (manuellement via un outil visuel de comparaison/fusion, ou
  automatiquement via des règles de chemins configurables),
- notifier l'utilisateur (toast/bulle du système d'exploitation) des événements importants,
- s'auto-réparer (redémarrer l'application entière) si sa propre
  surveillance de statut devient obsolète ou si l'icône de la zone de
  notification tombe en panne.

## 2. Pile technologique actuelle

| Aspect                     | Technologie                                                                                         |
|----------------------------|-----------------------------------------------------------------------------------------------------|
| Langage                    | Python 3                                                                                            |
| Boîte à outils UI          | wxPython (`wx`, `wx.adv.TaskBarIcon`)                                                               |
| Moteur de synchronisation  | Binaire externe `mutagen` CLI, invoqué en tant que sous-processus                                   |
| Transport distant          | Binaires externes SSH / SCP (`ssh.exe` / `scp.exe` de Git-for-Windows)                              |
| Comparaison/fusion visuelle| Outil externe (WinMerge par défaut, chemin configurable)                                            |
| Configuration              | Fichier JSON édité manuellement, avec lignes de commentaire `#` supprimées avant l'analyse          |
| Définitions de session     | Extraites d'un fichier `.bat` Windows contenant des commandes `mutagen sync create`                 |
| Persistance / état         | En mémoire uniquement (par processus) ; fichiers de log plats sur disque                            |
| Concurrence                | Un `threading.Thread` d'arrière-plan (`Monitor`) interrogeant le CLI mutagen, un thread UI/timer wx |
| Packaging                  | Exécutable Windows à instance unique (`mutagenmon.pyw` / `mutagenmon.exe` compilé)                  |

L'application n'a **aucune fenêtre** au sens traditionnel
`wx.Frame(None)` est créé uniquement pour héberger le `TaskBarIcon` et n'est jamais affiché.
Toute l'interaction se fait via l'icône de la zone de notification, 
son menu contextuel, et les boîtes de dialogue modales qu'elle ouvre à la demande.

## 3. Cartographie des modules

| Fichier | Responsabilité |
|---|---|
| `mutagenmon.pyw` | Point d'entrée. Charge la configuration, installe un gestionnaire d'exceptions global qui journalise et affiche une boîte de dialogue d'erreur, crée le `wx.Frame` caché et le `TaskBarIcon`, exécute la boucle principale wx. |
| `mutagenmonlib/wx/icon.py` | **Cœur de l'application.** Classe `TaskBarIcon` : machine à états de l'icône de la zone de notification, timer UI de 1 seconde, menu contextuel, boîte de dialogue de statut, notifications, logique d'auto-redémarrage. |
| `mutagenmonlib/wx/wx.py` | Petits utilitaires wx génériques : fabrique d'éléments de menu, boîte de dialogue d'information transitoire, utilitaires de boîtes de dialogue de message/erreur. |
| `mutagenmonlib/remote/monitor.py` | Thread d'arrière-plan `Monitor` : interroge le statut mutagen à intervalle fixe, maintient un état de session thread-safe (statut, compteurs d'erreurs, codes, conflits, log), décide quand une session doit être redémarrée, exécute la résolution automatique des conflits. |
| `mutagenmonlib/remote/mutagen.py` | Encapsule le CLI `mutagen` (`sync list`, `sync create`, `sync terminate`), analyse sa sortie textuelle en enregistrements structurés de statut par session et de conflits, charge les définitions de session depuis le fichier `.bat`. |
| `mutagenmonlib/remote/resolve.py` | Boîtes de dialogue et logique de résolution manuelle des conflits : boîte de dialogue à choix unique (fusion visuelle / A gagne / B gagne), workflow de fusion visuelle, garde-fou « trop de conflits », boucle de résolution par lot. |
| `mutagenmonlib/remote/ssh.py` | Utilitaires SSH/SCP utilisés lorsque le point de terminaison d'une session est distant (copie de fichiers, exécution d'un `stat` distant pour obtenir la taille/date de modification). |
| `mutagenmonlib/local/file.py` | Chargement de la configuration (JSON avec commentaires), utilitaires de chemins, écrivains de fichiers de log, accesseur de configuration `cfg()`. |
| `mutagenmonlib/local/lib.py` | Utilitaires de formatage (horodatages, affichage soigné de dictionnaires/statuts, analyseur de parenthèses correspondantes utilisé pour analyser la sortie de conflit imbriquée de mutagen). |
| `mutagenmonlib/local/run.py` | Wrapper `subprocess` avec journalisation/boîte de dialogue d'erreur unifiée et un utilitaire `run_merge` pour l'outil de comparaison externe. |
| `config/config_mutagenmon.json` | Toute la configuration d'exécution : chemins vers les binaires externes, seuils de scrutation/retard, bascules de notification, règles de résolution automatique. Chaque clé, son type, sa valeur par défaut et son unité sont documentés dans [06-configuration-reference.md](06-configuration-reference.md). |
| `mutagen/mutagen-create.bat` | Définit les sessions à surveiller (une ligne `mutagen sync create --name=... ...` par session). Également la source analysée pour construire la liste de sessions en mémoire. |
| `img/*.png` | Bitmaps de l'icône de la zone de notification, un par statut/état (voir [03-tray-icon-requirements.md](03-tray-icon-requirements.md)). |
| `log/*.log` | `error.log`, `restart.log`, `resolve.log`, `debug.log` (le journal de débogage est conditionné par `DEBUG_LEVEL`). |

## 4. Architecture d'exécution

```
┌────────────────────────────┐        1s wx.Timer         ┌───────────────────────────┐
│        wx App (UI thread)  │ ─────────────────────────▶ │      TaskBarIcon.update() │
│  hidden wx.Frame           │                             │  - reads Monitor state    │
│  TaskBarIcon (tray icon)   │◀──── click / menu ──────── │  - recomputes worst code  │
└────────────────────────────┘                             │  - sets icon + tooltip    │
             ▲                                             │  - shows notifications   │
             │ modal dialogs (status, conflicts, errors)   └───────────────────────────┘
             │
┌────────────┴───────────────┐   poll every                ┌───────────────────────┐
│   Monitor (background      │   MUTAGEN_POLL_PERIOD ms    │   mutagen CLI process │
│   thread, thread-safe      │ ───────────────────────────▶│  `mutagen sync list`  │
│   getters/setters)         │◀─────────────────────────── │  `sync create/terminate`│
└────────────────────────────┘   parsed text status        └───────────────────────┘
```

Deux boucles indépendantes s'exécutent simultanément :

1. **Thread `Monitor`** (`remote/monitor.py`) : à chaque `MUTAGEN_POLL_PERIOD`
   (1000 ms par défaut), il exécute `mutagen sync list`, analyse la sortie
   en un dictionnaire de statut par session et une liste de conflits, met à
   jour un « code de session » numérique par session, décide si une session
   doit être redémarrée (trop d'erreurs de connexion, session manquante,
   session dupliquée), arrête les sessions lorsque l'utilisateur a désactivé
   la surveillance, et exécute la résolution automatique des conflits.
2. **Timer UI wx** (`wx/icon.py`, `TaskBarIcon.update`, toutes les 1000 ms) :
   lit l'état publié par `Monitor` (ne touche jamais directement au CLI
   mutagen), calcule le *pire* statut parmi toutes les sessions, met à jour
   en conséquence le bitmap et l'infobulle de l'icône de la zone de
   notification, vide une file de messages pour afficher les notifications
   du système d'exploitation, et vérifie l'obsolescence de la dernière mise
   à jour du moniteur pour décider s'il faut forcer un redémarrage complet
   de l'application.

Tout l'état inter-thread est échangé via les accesseurs/mutateurs
verrouillés de `Monitor` et une `queue.Queue` de messages de notification —
il n'y a aucun état mutable partagé accédé sans synchronisation.

## 5. Comportement d'auto-réparation

L'application est conçue pour fonctionner sans surveillance pendant de
longues périodes ; elle contient donc plusieurs mécanismes de récupération
automatique :

- **Redémarrage par session** : si une session reste dans un état
  « connexion en cours » au-delà de `SESSION_MAX_ERRORS` scrutations,
  n'a aucune session trouvée au-delà de `SESSION_MAX_NOSESSION`
  scrutations, ou est dupliquée au-delà de `SESSION_MAX_DUPLICATE`
  scrutations, `Monitor` la termine et la recrée.
- **Redémarrage de l'application entière** : si l'icône de la zone de
  notification ne parvient pas à s'installer (`IsIconInstalled()` devient
  faux) ou si la dernière lecture de statut réussie de `Monitor` date de
  plus de `STATUS_MAX_LAG.Restart` secondes (90 s par défaut), l'application
  journalise la cause, lance un nouveau processus `mutagenmon`, et se
  termine.
- **Redémarrage manuel** : l'option « Reload config && restart mutagen »
  du menu de la zone de notification désactive toutes les sessions et,
  une fois que chaque session confirme son arrêt, déclenche le même
  chemin de redémarrage complet de l'application.

## 6. Particularités d'implémentation connues à résoudre dans la réécriture

Ces éléments sont observés directement dans le code source et devraient
être *consciemment* tranchés (conservés, corrigés, ou intentionnellement
abandonnés) lors de la refonte pour la réécriture WPF — voir
[03-tray-icon-requirements.md](03-tray-icon-requirements.md) §6 pour la
liste détaillée. Points saillants :

- Trois ressources d'icônes référencées par `icon.py` n'existent pas dans
  `img/` : `green-timeout-red.png`, `green-timeout-white.png`,
  `green-scan.png`. L'application lèverait une erreur/afficherait une
  icône cassée dans ces états aujourd'hui.
- Lorsqu'une session est dans l'état « ready » (pire code 100) *et* que
  son archive/profil vient de changer, aucune branche de `update_icon()`
  ne correspond (la condition externe exclut explicitement
  `updated_profile`, et aucun `elif` ne retteste le code 100), de sorte
  que l'icône « juste mis à jour » (`green-success.png`) est en pratique
  inaccessible dans ce cas, bien que le README du projet décrive une icône
  « fichiers mis à jour » affichée pendant une seconde dans cette
  situation.
- Le texte de l'infobulle « stale » (obsolète) est toujours le texte
  générique `"mutagen is watching for changes (stale)"`, quel que soit
  le pire statut réel (conflits/problèmes/synchronisation/scan) — un
  utilisateur voyant une session obsolète-mais-en-conflit obtient une
  infobulle trompeuse.
- Les noms de session doivent être globalement uniques (contrainte
  imposée uniquement par une boîte de dialogue d'avertissement au
  démarrage, et non par une erreur de validation stricte), et seuls les
  transports local/SSH sont pris en charge (limitation explicite du
  projet).
