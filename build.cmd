@echo off
setlocal
cd /d "%~dp0"
echo Slozka: %CD%
if not exist "Overlingo.Tests\Overlingo.Tests.csproj" (
  echo CHYBA: v teto slozce chybi Overlingo.Tests\Overlingo.Tests.csproj.
  echo Rozbalte CELY zip do NOVE prazdne slozky ^(ne pres starou verzi^) a spustte build.cmd znovu.
  dir /b
  pause & exit /b 1
)
echo === Kontrola .NET SDK ===
dotnet --version || (echo Nainstaluj .NET 10 SDK: https://dotnet.microsoft.com/download & pause & exit /b 1)
echo === Testy jadra (preklad, OCR logika, pipeline) ===
dotnet run --project "Overlingo.Tests\Overlingo.Tests.csproj" -c Release || (echo TESTY SELHALY & pause & exit /b 1)
echo === Sestaveni aplikace ===
dotnet publish "Overlingo.App\Overlingo.App.csproj" -c Release -r win-x64 --self-contained false -o publish
if errorlevel 1 (echo.& echo SESTAVENI SELHALO - zkopiruj chybove hlasky a posli je mi.& pause & exit /b 1)
echo.
echo HOTOVO. Spust: run.cmd  (nebo publish\Overlingo.exe)
pause
