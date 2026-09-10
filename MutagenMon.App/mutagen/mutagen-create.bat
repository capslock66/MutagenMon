@echo off
rem Sample session definition — edit this to point at real folders before
rem running the app. See requirements/01-functional-requirements.md FR-1.1.
rem A local <-> local pair is the simplest way to manually verify the tray
rem icon on Windows without needing an SSH endpoint.
rem mutagen sync create --name=robbie-mutagenmon  --sync-mode=two-way-resolved "C:\sources\mutagenMon" robbie:sources/mutagenMon
rem mutagen sync create --name=robbie-mutagenmon -m=two-way-resolved "C:\sources\mutagenMon" robbie:sources/mutagenMon 
rem mutagen sync create --name=robbie-mutagenmon -m two-way-resolved "C:\sources\mutagenMon" robbie:sources/mutagenMon 

mutagen sync terminate --all
mutagen sync create --name=robbie-mutagenmon "C:\sources\mutagenMon" robbie:sources/mutagenMon -m two-way-resolved 
mutagen sync create --name=robbie-appman     "C:\sources\appman"     robbie:sources/appman


rem mutagen sync list
