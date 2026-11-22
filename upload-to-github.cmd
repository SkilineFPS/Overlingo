@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
where git >nul 2>nul
if errorlevel 1 (
  echo Git neni nainstalovany. Stahnete ho z https://git-scm.com/download/win a spustte skript znovu.
  pause & exit /b 1
)
set REPO=https://github.com/SkilineFPS/Overlingo.git
echo Nahraje cely projekt do %REPO%
echo POZOR: stavajici obsah repozitare na GitHubu se prepise timto projektem.
set /p OK=Pokracovat? (a/n): 
if /i not "%OK%"=="a" exit /b 0
if not exist .git git init
git branch -M main
git add .
git commit -m "Overlingo 1.0" 
git remote remove origin >nul 2>nul
git remote add origin %REPO%
git push -u origin main --force
if errorlevel 1 (echo. & echo CHYBA pri nahravani. Zkopirujte vypis a poslete ho. & pause & exit /b 1)
echo.
echo Hotovo: https://github.com/SkilineFPS/Overlingo
pause
