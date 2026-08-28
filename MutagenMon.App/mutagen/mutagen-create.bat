@echo off
rem Sample session definition — edit this to point at real folders before
rem running the app. See requirements/01-functional-requirements.md FR-1.1.
rem A local <-> local pair is the simplest way to manually verify the tray
rem icon on Windows without needing an SSH endpoint.

mutagen sync terminate --all
mutagen sync create --name=robbie-mutagenmon  --sync-mode=two-way-resolved "C:\sources\mutagenMon" robbie:sources/mutagenMon
mutagen sync create --name=robbie-appman                                   "C:\sources\appman"     robbie:sources/appman
mutagen sync list
