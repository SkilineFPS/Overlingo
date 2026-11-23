@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
set VER=%1
if "%VER%"=="" set VER=1.0.4
echo === Overlingo %VER%: sestavuji samostatnou verzi (bez nutnosti instalovat .NET) ===
if exist publish rmdir /s /q publish
dotnet publish Overlingo.App\Overlingo.App.csproj -c Release -r win-x64 --self-contained true -p:Version=%VER% -o publish
if errorlevel 1 goto fail
echo.
echo === Hotovo: slozka publish\ (spustitelny soubor publish\Overlingo.exe) ===
powershell -NoProfile -Command "Compress-Archive -Force -Path publish\* -DestinationPath Overlingo-%VER%-win-x64.zip"
echo Prenosna verze: Overlingo-%VER%-win-x64.zip
set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" set ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe
if exist "%ISCC%" (
  "%ISCC%" /DAppVersion=%VER% installer\overlingo.iss
  if errorlevel 1 goto fail
  echo Instalator: installer\Output\Overlingo-Setup-%VER%.exe
) else (
  echo.
  echo Instalator nevytvoren: nainstalujte zdarma Inno Setup 6 ^(https://jrsoftware.org/isdl.php^) a spustte skript znovu.
)
goto end
:fail
echo.
echo CHYBA: sestaveni selhalo. Zkopirujte vypis a poslete ho.
:end
pause
