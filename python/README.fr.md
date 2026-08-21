# MutagenMon
Interface graphique multiplateforme pour le synchroniseur de fichiers <a href=https://github.com/mutagen-io/mutagen>mutagen.io</a> : surveille l'état des sessions dans la barre système, redémarre les sessions bloquées, résout les conflits

[![Codacy Badge](https://api.codacy.com/project/badge/Grade/802c129fde624c2390086e9246f29b79)](https://www.codacy.com/manual/rualark/MutagenMon?utm_source=github.com&amp;utm_medium=referral&amp;utm_content=rualark/MutagenMon&amp;utm_campaign=Badge_Grade)

# Fonctionnalités

- MutagenMon démarre les sessions spécifiées dans le fichier `mutagen/mutagen-create.bat` et surveille leur état
- MutagenMon redémarre une session si elle se bloque et ne peut pas se connecter pendant un certain temps
- MutagenMon affiche une icône dans la barre système en fonction de l'état actuel des sessions (si plusieurs sessions sont surveillées, le pire état parmi toutes les sessions est affiché) :

<img src=https://i.imgur.com/mPu7mZq.png align=top width=30> Surveillance des changements (tout est ok)

<img src=https://i.imgur.com/Kg671Sm.png align=top width=30> Fichiers mis à jour (cette icône est affichée pendant une seconde lorsque des fichiers sont mis à jour dans une session mutagen)

<img src=https://i.imgur.com/TLt1EDe.png align=top width=30> Synchronisation en cours (réconciliation, préparation, application des changements ou sauvegarde de l'archive)

<img src=https://i.imgur.com/uOzXxHM.png align=top width=30> Analyse des fichiers en cours

<img src=https://i.imgur.com/tTMBScq.png align=top width=30> Conflits détectés (mais aucun problème ni erreur)

<img src=https://i.imgur.com/MW5448A.png align=top width=30> Problèmes détectés

<img src=https://i.imgur.com/376oKOM.png align=top width=30> En attente de réponse du démon mutagen

<img src=https://i.imgur.com/wR2LqjK.png align=top width=30> Arrêt de mutagen

<img src=https://i.imgur.com/jHplJEG.png align=top width=30> Mutagen non lancé (redémarrage)

<img src=https://i.imgur.com/Xayacab.png align=top width=30> Mutagen non lancé (désactivé)

<img src=https://i.imgur.com/5UAKYvo.png align=top width=30> Mutagen ne peut pas se connecter (redémarrage)

<img src=https://i.imgur.com/YcvEENO.png align=top width=30> Mutagen ne peut pas se connecter (désactivé)

- Cliquez sur l'icône MutagenMon dans la barre système pour voir l'état détaillé de chaque session individuelle :

<img src=https://i.imgur.com/B9ljxT7.png>

- En cas de conflits, vous pouvez les examiner, les résoudre visuellement à l'aide de winmerge (sur Windows) ou d'un autre logiciel - ou choisir directement le côté gagnant. L'heure et la taille des deux fichiers sont affichées. Le fichier avec l'horodatage le plus récent est sélectionné automatiquement pour la résolution :

<img src=https://i.imgur.com/d98x4xU.png>

- MutagenMon peut résoudre automatiquement les conflits si vous spécifiez des chemins pour lesquels les versions Alpha ou Beta doivent toujours l'emporter. Vous recevrez une notification sur le bureau si un conflit est résolu automatiquement.

# Prise en charge du système d'exploitation

MutagenMon peut fonctionner sous Windows, Linux ou Mac (actuellement testé uniquement sous Windows)

# Installation sous Windows

1. Téléchargez et décompressez la <a href=https://github.com/rualark/MutagenMon/releases>version de MutagenMon</a>
2. Téléchargez la version de <a href=https://github.com/mutagen-io/mutagen>mutagen.io</a> et placez le binaire mutagen dans le dossier `mutagen` de MutagenMon
3. Si vous souhaitez utiliser un outil de comparaison et de fusion visuelle, téléchargez et installez winmerge ou un programme alternatif capable de prendre deux fichiers différents en tant que deux paramètres.
4. Ajoutez vos sessions au fichier `mutagen/mutagen-create.bat` dans le dossier MutagenMon
5. Modifiez le fichier de configuration situé dans `mutagen/config/mutagenmon_config.json`
6. Lancez mutagenmon

# Installation depuis les sources

1. Installez python3
2. Installez wxpython : `pip install wxpython`
3. Téléchargez et décompressez la <a href=https://github.com/rualark/MutagenMon/releases>version de MutagenMon</a>
4. Téléchargez la version de <a href=https://github.com/mutagen-io/mutagen>mutagen.io</a> et placez le binaire mutagen dans le dossier `mutagen` de MutagenMon
5. Si vous souhaitez utiliser un outil de comparaison et de fusion visuelle, téléchargez et installez winmerge ou un programme alternatif capable de prendre deux fichiers différents en tant que deux paramètres.
6. Ajoutez vos sessions au fichier `mutagen/mutagen-create.bat` dans le dossier MutagenMon
7. Modifiez le fichier de configuration situé dans `mutagen/config/mutagenmon_config.json`
8. Lancez mutagenmon

# Limitations

- Nécessite que les noms de sessions soient uniques
- Fonctionne uniquement avec les transports mutagen locaux et ssh

Ticket sur mutagen : https://github.com/mutagen-io/mutagen/issues/173

[![Open Source Helpers](https://www.codetriage.com/rualark/mutagenmon/badges/users.svg)](https://www.codetriage.com/rualark/mutagenmon)
[![BCH compliance](https://bettercodehub.com/edge/badge/rualark/MutagenMon?branch=master)](https://bettercodehub.com/)

[![DeepSource](https://static.deepsource.io/deepsource-badge-light.svg)](https://deepsource.io/gh/rualark/MutagenMon/?ref=repository-badge)
