# Inventaire des écrans UI (source wxPython)

L'application n'a **pas de fenêtre principale traditionnelle**. Chaque
écran listé ci-dessous est soit l'icône de la barre d'état système
elle-même, soit un menu affiché depuis celle-ci, soit une boîte de
dialogue modale/non modale ouverte à la demande. Cet inventaire sert de
base aux wireframes dans [wireframes/](wireframes) ainsi qu'à la liste
des fenêtres/boîtes de dialogue de la réécriture WPF.

Les libellés de bouton, titres de boîte de dialogue et textes exacts de
chaque écran ci-dessous sont spécifiés dans
[01-functional-requirements.fr.md](01-functional-requirements.fr.md) (voir
la colonne « Texte exact ») et
[03-tray-icon-requirements.fr.md](03-tray-icon-requirements.fr.md) — ce
tableau est un index, pas la source de vérité pour ces textes ; un
développeur sans accès à `python/` devrait donc suivre ces liens plutôt
que la colonne « Source », conservée uniquement pour la traçabilité vers
l'implémentation existante.

| # | Écran | Implémentation wx | Source | Texte exact | Wireframe |
|---|---|---|---|---|---|
| 1 | Icône de la barre d'état système (tous les états visuels) | Sous-classe `wx.adv.TaskBarIcon` `TaskBarIcon`, `set_icon()` | `mutagenmonlib/wx/icon.py` | [03-tray-icon-requirements.fr.md §3](03-tray-icon-requirements.fr.md) (tableau complet des états/infobulles) | [tray-icon-states.svg](wireframes/tray-icon-states.svg) |
| 2 | Menu contextuel clic droit sur l'icône de la barre d'état système | `TaskBarIcon.CreatePopupMenu()` | `mutagenmonlib/wx/icon.py` | [FR-7](01-functional-requirements.fr.md#fr-7--menu-contextuel-de-la-zone-de-notification-et-contrôle-des-sessions) | [tray-context-menu.svg](wireframes/tray-context-menu.svg) |
| 3 | Vue de statut — sans conflit | `wx.MessageDialog` (OK / Information) via `on_left_down()` | `mutagenmonlib/wx/icon.py` | [FR-8](01-functional-requirements.fr.md#fr-8--vue-de-statut-détaillée) ; titre = texte de l'infobulle courante dérivé de `TRAY_TOOLTIP` | [status-dialog-ok.svg](wireframes/status-dialog-ok.svg) |
| 4 | Vue de statut — avec conflits | `wx.MessageDialog` (OK/Annuler, libellés personnalisés « Resolve conflicts »/« Cancel », icône Question) via `on_left_down()` | `mutagenmonlib/wx/icon.py` | [FR-8](01-functional-requirements.fr.md#fr-8--vue-de-statut-détaillée) | [status-dialog-conflicts.svg](wireframes/status-dialog-conflicts.svg) |
| 5 | Sélecteur de résolution de conflit | `wx.SingleChoiceDialog` (liste à boutons radio : fusion visuelle / A gagne / B gagne) via `resolve_single()` | `mutagenmonlib/remote/resolve.py` | [FR-9.1/FR-9.2](01-functional-requirements.fr.md#fr-9--résolution-manuelle-des-conflits) (format du titre, disposition du corps, bizarrerie `<N>`/`<total>`) | [conflict-resolution-dialog.svg](wireframes/conflict-resolution-dialog.svg) |
| 6 | Indicateur transitoire « connexion en cours » | Petit `wx.Dialog` sans décoration, sans bouton, fermé automatiquement par le code (`info_message()`) | `mutagenmonlib/wx/wx.py`, utilisé tout au long de `resolve.py` | [FR-9.6](01-functional-requirements.fr.md#fr-9--résolution-manuelle-des-conflits) ; titre = le texte de description de l'opération, pas de corps | [remote-connecting-toast.svg](wireframes/remote-connecting-toast.svg) |
| 7 | Confirmation de fusion résolue | `wx.MessageDialog` (OK / Information) via `visual_merge()` | `mutagenmonlib/remote/resolve.py` | [FR-9.2](01-functional-requirements.fr.md#fr-9--résolution-manuelle-des-conflits) — titre/corps exacts donnés en ligne | [status-dialog-ok.svg](wireframes/status-dialog-ok.svg) *(même modèle, texte différent)* |
| 8 | Garde-fou trop de conflits | `wx.MessageDialog` (OK / Information) via `resolve_all()` | `mutagenmonlib/remote/resolve.py` | [FR-9.5](01-functional-requirements.fr.md#fr-9--résolution-manuelle-des-conflits) — titre/corps exacts donnés en ligne | [status-dialog-ok.svg](wireframes/status-dialog-ok.svg) *(même modèle, texte différent)* |
| 9 | Avertissement de nom de session en double | `wx.MessageDialog` (OK / Information) via `get_sessions()` | `mutagenmonlib/remote/mutagen.py` | [FR-1.2](01-functional-requirements.fr.md#fr-1--chargement-de-la-configuration-des-sessions) — titre/corps exacts donnés en ligne | [status-dialog-ok.svg](wireframes/status-dialog-ok.svg) *(même modèle, texte différent)* |
| 10 | Boîte de dialogue d'erreur fatale | `wx.MessageDialog` (OK / Erreur) via `errorBox()` | `mutagenmonlib/wx/wx.py`, appelée depuis le gestionnaire d'exceptions global et `run.py` | [FR-14.1](01-functional-requirements.fr.md#fr-14--journalisation-et-diagnostics) — titre toujours `"MutagenMon error"`, corps = le texte de l'exception/trace | [error-dialog.svg](wireframes/error-dialog.svg) |
| 11 | Notification de bureau du système d'exploitation (bulle/toast) | `TaskBarIcon.ShowBalloon()` avec repli sur `wx.adv.NotificationMessage` | `mutagenmonlib/wx/icon.py` (`notify()`) | [FR-11](01-functional-requirements.fr.md#fr-11--notifications-de-bureau) — tableau titre/interrupteur par événement | [notification-toast.svg](wireframes/notification-toast.svg) |
| 12 | Fenêtre racine cachée | `wx.Frame(None)`, créée mais jamais affichée ; existe uniquement pour que l'icône de la barre d'état système ait une fenêtre propriétaire | `mutagenmon.pyw` | — (jamais visible, aucun texte) | *(non esquissée — jamais visible)* |

## Notes pour la réécriture

- Les écrans 3, 4, 7, 8, 9 partagent tous un même modèle générique de
  « boîte de message » (titre, texte multi-lignes, OK et éventuellement
  Annuler) — dans l'application WPF, cela devrait être regroupé en une
  seule fenêtre de dialogue réutilisable paramétrée par
  titre/corps/boutons, plutôt que cinq fenêtres distinctes.
- L'écran 6 (indicateur transitoire « connexion en cours ») n'a aucun
  bouton et est fermé de manière programmatique par l'appelant une fois
  l'opération distante terminée ; ce n'est pas un écran que l'utilisateur
  ferme lui-même.
- L'écran 5 est l'écran structurellement le plus distinct (choix par
  boutons radio + comparaison structurée à deux colonnes A/B) et mérite
  son propre composant dans la réécriture.
- Aucun de ces écrans ne prend actuellement en charge le redimensionnement,
  la thématisation, ou l'accessibilité au-delà des valeurs par défaut du
  système d'exploitation (ce sont des instances natives de
  wx.MessageDialog/SingleChoiceDialog) — la réécriture devrait au minimum
  correspondre au comportement modal natif du système d'exploitation
  (bloquant, fermable au clavier, titre/texte visible par un lecteur
  d'écran).
