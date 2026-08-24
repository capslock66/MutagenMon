# Exigences non fonctionnelles

## NFR-1 — Réactivité en temps réel (priorité la plus élevée)

- L'icône de la barre système DOIT refléter un changement de statut de
  session agrégé en un seul cycle de scrutation+UI, c'est-à-dire **au
  maximum ~1 à 2 secondes** de bout en bout en charge normale (valeurs par
  défaut actuelles : scrutation en arrière-plan de 1000 ms + tick UI de
  1000 ms). Il s'agit du critère de qualité le plus important de
  l'application — voir [03-tray-icon-requirements.md](03-tray-icon-requirements.md).
- La lecture/le rafraîchissement du statut de la barre système NE DOIT PAS
  bloquer le shell de l'OS ni le thread UI du processus lui-même ; la
  scrutation du moteur de synchronisation DOIT s'effectuer en dehors du
  thread UI.

## NFR-2 — Fiabilité et auto-réparation

- L'application DOIT pouvoir fonctionner sans surveillance pendant des
  semaines sans intervention manuelle, en se rétablissant automatiquement
  après : un processus de moteur de synchronisation bloqué/planté, un
  statut interne obsolète, et un échec d'enregistrement de l'icône de la
  barre système (voir FR-6, FR-13).
- Un plantage dans la logique de surveillance en arrière-plan DOIT être
  journalisé avec le contexte complet avant la sortie du processus (jamais
  une disparition silencieuse de la barre système).

## NFR-3 — Empreinte des ressources

- En tant qu'utilitaire d'arrière-plan fonctionnant en permanence,
  l'utilisation CPU et mémoire DOIT rester minimale au repos
  (implémentation actuelle : un timer d'une seconde plus une scrutation
  d'une seconde, chacun effectuant un traitement de texte léger sur la
  sortie d'une CLI — pas de rendu continu, pas d'historique lourd en
  mémoire).
- L'application DOIT s'exécuter en instance unique ; elle ne doit pas
  accumuler d'icônes de barre système en double ni de scrutateurs
  d'arrière-plan en double au fil des redémarrages.

## NFR-4 — Portabilité

- Le projet original cible Windows, Linux et macOS, mais n'a été testé que
  sous Windows ; les chemins des outils externes (SSH, SCP, diff/merge)
  sont spécifiques à l'OS et doivent rester configurables par plateforme.
- La cible principale de la réécriture est Windows en priorité (icône de
  barre système, notifications, intégration d'outils externes), avec une
  architecture qui reste ouverte à d'autres OS de bureau dans la mesure où
  la pile UI choisie le permet.

## NFR-5 — Configurabilité

- Tous les seuils, bascules et chemins d'outils externes DOIVENT rester
  configurables de l'extérieur sans recompilation (actuellement : un
  fichier JSON avec des commentaires en ligne). Des valeurs par défaut
  sensées DOIVENT permettre un démarrage sans configuration pour le cas
  courant (sessions locales uniquement, aucune règle de résolution
  automatique) — voir [06-configuration-reference.md](06-configuration-reference.md)
  pour chaque clé et sa valeur par défaut.

## NFR-6 — Observabilité

- Toutes les décisions automatiques affectant les données de l'utilisateur
  (redémarrages de session, résolution automatique de conflits) DOIVENT
  être journalisées avec suffisamment de contexte pour être reconstituées
  a posteriori (horodatage, nom de la session, les deux URL des points de
  terminaison, action effectuée).
- Un journal de diagnostic DOIT être disponible pour le dépannage sans
  nécessiter de débogueur attaché. L'application historique conditionne
  cela à un niveau de verbosité (`DEBUG_LEVEL`, désactivé par défaut) ; la
  réécriture .NET supprime délibérément ce verrou — un fichier de journal
  unique capture toujours tous les niveaux, de sorte qu'un échec de
  lancement ou un plantage ne soit jamais impossible à diagnostiquer
  simplement parce que personne n'avait pensé à augmenter la verbosité au
  préalable (voir
  [01-functional-requirements.md FR-14](01-functional-requirements.md#fr-14--logging--diagnostics)
  et
  [05-wpf-migration-notes.md §7](05-wpf-migration-notes.md#7-logging)).

## NFR-7 — Utilisabilité / intrusivité minimale

- L'application DOIT rester discrète par défaut : aucune fenêtre modale au
  démarrage, statut transmis de manière passive via l'icône de la barre
  système et son infobulle, et les interruptions (boîtes de dialogue,
  notifications) réservées aux situations qui nécessitent une attention
  (conflits, erreurs) ou que l'utilisateur a explicitement demandées (clic
  sur l'icône).
- Les actions destructrices ou nécessitant une attention particulière
  (résolution de conflits par lot, arrêt de sessions) DOIVENT toujours
  être accessibles depuis l'icône de la barre système en deux interactions
  (clic/clic droit, puis un choix de menu).

## NFR-8 — Sécurité des données pendant la résolution de conflits

- Toute opération qui écrase un fichier de l'utilisateur dans le cadre
  d'une résolution de conflit (FR-9, FR-10) DOIT être journalisée avant ou
  immédiatement après la copie, et DOIT indiquer clairement quel côté
  « l'a emporté », afin qu'une résolution non désirée puisse être
  identifiée et annulée manuellement à l'aide du journal.
- La résolution automatique de conflits DOIT être strictement optionnelle
  (opt-in) par motif de chemin (par défaut : aucune règle configurée, rien
  n'est résolu automatiquement).

## NFR-9 — Sécurité

- Les identifiants ne sont jamais stockés par l'application ; l'accès
  distant repose sur la configuration SSH ambiante (clés/agent) de
  l'utilisateur du système exécutant le processus.
- Les chemins de fichiers distants et les noms d'hôtes utilisés pour
  SSH/SCP DOIVENT être échappés (shell-escaped) avant d'être transmis aux
  processus externes, afin d'éviter toute injection de commande via des
  noms de fichiers/répertoires forgés.
- Les fichiers de configuration peuvent contenir des chemins du système de
  fichiers local mais aucun secret par conception ; la réécriture DOIT
  préserver cette propriété (aucun identifiant dans la configuration).

## NFR-10 — Maintenabilité

- L'analyse du statut dépend du format de texte exact produit par la CLI
  externe `mutagen`. Ce couplage DOIT être isolé derrière une seule
  frontière d'analyse bien testée, afin qu'un changement dans le format de
  sortie de la CLI (ou un passage à la sortie structurée/JSON de mutagen,
  si disponible dans la version cible) ne nécessite de modifier qu'un seul
  composant.
- Les codes de statut numériques (FR-3) sont un détail d'implémentation
  interne, et non un contrat public ; la réécriture est libre de les
  remplacer par une véritable énumération, tant que les mêmes règles de
  priorité (FR-3/FR-4) sont préservées.

## NFR-11 — Testabilité

- La logique de classification des statuts (FR-3, FR-4), la détection de
  péremption (staleness) (FR-6), et la correspondance de résolution
  automatique (FR-10) sont des fonctions pures de leurs entrées et
  DOIVENT pouvoir être testées unitairement sans lancer la véritable CLI
  `mutagen` ni une véritable icône de barre système.
