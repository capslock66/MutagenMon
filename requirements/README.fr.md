# MutagenMon — Exigences

Ce dossier documente le comportement de l'application wxPython existante
(`python/`), déduit par rétro-ingénierie de son code source, comme base de
spécification pour une réécriture sous forme d'application de bureau
**.NET WPF**.

## Ordre de lecture

1. [00-application-overview.md](00-application-overview.md) — ce que fait
   l'application, son architecture, la cartographie des modules et les
   particularités connues de l'implémentation.
2. [01-functional-requirements.md](01-functional-requirements.md) —
   l'ensemble des exigences fonctionnelles (FR-1 .. FR-15), chacune
   traçable au code source.
3. [02-non-functional-requirements.md](02-non-functional-requirements.md)
   — les attributs de qualité (NFR-1 .. NFR-11).
4. **[03-tray-icon-requirements.md](03-tray-icon-requirements.md)** — ⭐
   l'exigence la plus importante entre toutes : l'icône de la zone de
   notification doit afficher le statut en temps réel de toutes les
   sessions de synchronisation. Matrice complète des états de l'icône,
   temporisation, comportement au clic et règles d'auto-réparation.
5. [04-ui-screens-inventory.md](04-ui-screens-inventory.md) — chaque écran/
   boîte de dialogue de l'application legacy, avec des liens vers sa
   maquette.
6. [05-wpf-migration-notes.md](05-wpf-migration-notes.md)
   — comment héberger ce comportement sur la pile technique WPF
   (correspondance des composants, modèle de threading, plan de livraison
   par phases).

## Ressources d'icônes

[icons/](icons) contient chaque bitmap d'icône de zone de notification
référencé par [03-tray-icon-requirements.md](03-tray-icon-requirements.md)
§3 — 12 ressources réelles copiées depuis l'application legacy, plus 3
espaces réservés générés pour des icônes que l'application legacy
référence mais n'a jamais réellement livrées (voir les §3.1 et §7.1 de ce
document pour les détails). **Cela rend `requirements/` autonome :
l'implémentation de l'icône de zone de notification .NET WPF ne nécessite
jamais de consulter `python/img/`.**

## Maquettes

Des esquisses (et non des captures d'écran fidèles au pixel près) de
chaque écran que l'application legacy peut afficher, dans
[wireframes/](wireframes) :

- [tray-icon-states.svg](wireframes/tray-icon-states.svg) — la matrice
  complète des états de l'icône (le livrable central de cette analyse).
- [tray-context-menu.svg](wireframes/tray-context-menu.svg)
- [status-dialog-ok.svg](wireframes/status-dialog-ok.svg)
- [status-dialog-conflicts.svg](wireframes/status-dialog-conflicts.svg)
- [conflict-resolution-dialog.svg](wireframes/conflict-resolution-dialog.svg)
- [remote-connecting-toast.svg](wireframes/remote-connecting-toast.svg)
- [error-dialog.svg](wireframes/error-dialog.svg)
- [notification-toast.svg](wireframes/notification-toast.svg)

## Note sur le périmètre

L'application legacy `python/` demeure la référence exécutable en matière
de comportement. Lorsque ces documents et l'application legacy en
fonctionnement divergent, il faut considérer cela comme un bogue de
documentation à corriger ici — à l'exception des écarts délibérés
répertoriés dans 03-tray-icon-requirements.md §7, qui constituent des
défauts connus de l'application legacy que la réécriture doit corriger
plutôt que reproduire.
