@echo off
rem Sample session definition — edit this to point at real folders before
rem running the app. See requirements/01-functional-requirements.md FR-1.1.
rem A local <-> local pair is the simplest way to manually verify the tray
rem icon on Windows without needing an SSH endpoint.

mutagen sync terminate --all
mutagen sync create --name=pc-ub1-mutagenmon  --sync-mode=two-way-resolved "C:\sources\mutagenMon" tparent@pc-ub1:sources/mutagenMon
mutagen sync create --name=pc-ub1-appman      --sync-mode=two-way-resolved "C:\sources\appman"     tparent@pc-ub1:sources/appman
mutagen sync list
timeout /t 3
mutagen sync list -l
pause


