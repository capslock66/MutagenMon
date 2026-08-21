# CLAUDE.md

Consignes pour Claude Code (et tout autre agent) travaillant dans ce dépôt.

## Ce qu'est ce dépôt

**MutagenMon** est un utilitaire de bureau Wpf qui supervise les sessions de synchronisation de fichiers [mutagen.io](https://github.com/mutagen-io/mutagen) et rapporte leur statut en temps réel via une icône de la barre système (system tray).
Il peut redémarrer automatiquement les sessions bloquées ou en erreur et aide à résoudre les conflits de synchronisation
(manuellement via un outil de diff/merge, ou automatiquement via des règles de chemin configurées).

Le dépôt contient actuellement deux éléments :

```
mutagenMon/
├── python/          # EXISTING implementation — wxPython desktop app (source of truth for behavior)
└── requirements/     # Requirements & wireframes extracted from python/, target: a rewrite
```

- `python/` est **l'implémentation existante et fonctionnelle (legacy)**. C'est la
  référence comportementale : en cas de doute sur le comportement attendu de la nouvelle application,
  lire ce code avant de poser la question à l'utilisateur.
- `requirements/` est une documentation **dérivée de** `python/`, rédigée pour
  guider une réécriture de cette application en application de bureau **.NET WPF**. Ce n'est
  pas en soi le code cible — voir [Où regarder en premier](#où-regarder-en-premier)
  ci-dessous pour l'emplacement réel du projet .NET.

## Où regarder en premier

Toujours commencer par [requirements/README.md](requirements/README.md) — il
indexe chaque document de spécifications et chaque wireframe, dans l'ordre de
lecture recommandé. En particulier :

- **[requirements/03-tray-icon-requirements.md](requirements/03-tray-icon-requirements.md)**
  est le document le plus important de ce dépôt. L'icône de la barre système affichant
  le statut de synchronisation en temps réel est l'exigence la plus importante
  de toute l'application — traiter tout changement touchant au calcul du statut,
  au polling, ou à l'icône de la barre système avec la rigueur que cette priorité impose.
- **[requirements/05-wpf-migration-notes.md](requirements/05-wpf-migration-notes.md)**
  consigne les décisions d'architecture déjà prises pour la réécriture (hôte WPF
  simple + icône de barre système basée sur `H.NotifyIcon.Wpf`, service hébergé en
  arrière-plan pour le polling, correspondance des composants). Noter sa « Revision note » :
  la réécriture utilisait initialement Blazor Hybrid (WPF + `BlazorWebView`) et a été
  réorientée vers du WPF simple après de réels échecs d'exécution Blazor/WebView2 sous
  .NET 10 — ne pas réintroduire Blazor/BlazorWebView sans raison claire, et lire le
  §8 de ce document avant de proposer une approche différente pour l'icône de la barre système.

## Travailler dans `python/` (application legacy)

- Point d'entrée : `python/mutagenmon.pyw`. Exécuter avec `python mutagenmon.pyw`
  depuis l'intérieur de `python/` (nécessite `wxpython` : `pip install wxpython`).
- La logique principale se trouve dans `mutagenmonlib/` :
  - `wx/icon.py` — la machine à états de l'icône de la barre système (`TaskBarIcon`). C'est
    le fichier qui implémente tout ce qui est décrit dans
    `requirements/03-tray-icon-requirements.md`.
  - `remote/monitor.py` — le thread de polling en arrière-plan.
  - `remote/mutagen.py` — le wrapper de la CLI `mutagen` et l'analyse du texte de statut.
  - `remote/resolve.py` — les boîtes de dialogue et la logique de résolution de conflits.
  - `local/file.py`, `local/lib.py`, `local/run.py` — la configuration, le formatage,
    et les fonctions utilitaires de sous-processus.
- Configuration : `python/config/config_mutagenmon.json` (JSON avec des lignes de
  commentaire `#`, retirées avant l'analyse). Les sessions à surveiller sont définies
  dans `python/mutagen/mutagen-create.bat`.
- Traiter ce code comme **principalement en lecture seule** : les corrections de bugs
  sont acceptables, mais ne pas le refactoriser vers la conception WPF — ce travail
  relève d'un nouveau projet, guidé par `requirements/`, et non d'une modification sur place
  dans `python/`.
- Les défauts connus et intentionnellement documentés (assets d'icônes manquants, un
  état d'icône « ready + updated » inatteignable, une infobulle (tooltip) générique et figée)
  sont listés dans `requirements/03-tray-icon-requirements.md` §7. Ne pas les « corriger »
  silencieusement dans l'application legacy sans vérifier si l'utilisateur souhaite une
  parité avec le legacy ou plutôt une correction directe dans la future application WPF.

## Travailler dans `requirements/`

- Ce sont des documents d'analyse **en anglais uniquement** (exigences fonctionnelles,
  exigences non fonctionnelles, inventaire des écrans, notes de migration) ainsi que des
  wireframes SVG sous `requirements/wireframes/`. Chaque exigence est
  traçable jusqu'à un fichier source ou un comportement spécifique dans `python/` — conserver
  cette traçabilité lors des modifications.
- Les wireframes sont des esquisses, pas des maquettes fidèles au pixel près. Si le texte
  ou le comportement de l'interface legacy change, mettre à jour ensemble le wireframe
  et le document de spécifications correspondants.
- En cas de découverte d'une nouvelle divergence entre un document de spécifications et le
  code source `python/` réel, privilégier une relecture du code source et la correction du
  document — l'application legacy en fonctionnement fait foi pour le comportement *actuel*.

## La réécriture .NET WPF

Le projet .NET se trouve dans `dotNet/` (voir `dotNet/README.md`). La phase 1
(polling en arrière-plan, classification du statut des sessions, la machine à états
complète de l'icône de la barre système, un menu contextuel minimal) est implémentée et la
compilation/les tests passent sur Linux ; voir la liste des tâches / les notes de phase dans
`requirements/05-wpf-migration-notes.md` §6 pour ce qui est fait et ce qui reste à faire.

Lors de la prise en charge des phases suivantes :

1. Relire `requirements/05-wpf-migration-notes.md` pour l'architecture convenue
   (hôte WPF simple + icône de barre système de type `H.NotifyIcon.Wpf` + un
   service hébergé en arrière-plan pour le polling) avant d'ajouter quoi que ce soit — noter
   son §8 « Runtime pitfalls found during Phase 1 Windows verification »,
   qui consigne plusieurs pièges non évidents liés à WPF/à l'icône de la barre système déjà
   rencontrés et corrigés une fois.
2. Suivre le plan de livraison par phases de ce document (§6) : icône de la barre système +
   polling + classification du statut en premier (fait), boîtes de dialogue/menu en second,
   résolution de conflits en troisième, notifications/résolution automatique en quatrième,
   récupération automatique/journalisation en dernier.
3. Garder le nouveau projet à l'intérieur de `dotNet/` — ne pas le mélanger avec
   `python/`.
4. Conserver les noms de clés/valeurs par défaut de la configuration issus de
   `python/config/config_mutagenmon.json`, sauf si l'utilisateur demande explicitement
   de repenser le schéma de configuration (voir
   `requirements/05-wpf-migration-notes.md` §5).
5. **Ne jamais lire depuis `python/img/` pour les bitmaps de l'icône de la barre système.**
   Utiliser plutôt `requirements/icons/` — ce dossier contient déjà toutes les icônes
   référencées par `requirements/03-tray-icon-requirements.md` §3 (à la fois
   les `.png`, pour la documentation, et les `.ico`, le format réellement chargé à
   l'exécution), y compris des placeholders générés pour les trois icônes qui
   manquaient déjà dans l'application legacy. `requirements/` est intentionnellement
   autonome, précisément pour que la génération du code .NET n'ait jamais besoin de
   passer par `python/`. Remplacer les 3 placeholders (`green-scan`,
   `green-timeout-white`, `green-timeout-red`) par des assets correctement conçus
   avant la sortie — voir
   `requirements/03-tray-icon-requirements.md` §3.1/§7.1.

## Conventions générales

- Toute la documentation de ce dépôt est rédigée en **anglais**, conformément à la
  consigne du propriétaire du projet, bien que celui-ci communique avec
  Claude en français. Conserver les documents nouveaux/modifiés en anglais sauf indication contraire.
- Ce n'est pas (encore) un dépôt git — il n'y a pas d'historique de branches/commits à
  consulter ; se référer aux documents de spécifications et au code source `python/`
  lui-même pour le contexte.
