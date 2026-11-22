@echo off
cd /d "%~dp0publish"
if not exist Overlingo.exe (echo Overlingo.exe neexistuje - nejdriv spust build.cmd & pause & exit /b 1)
start "" Overlingo.exe
