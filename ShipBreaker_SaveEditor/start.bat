@echo off
setlocal
cd /d "%~dp0"

if not exist "node\node.exe" (
  echo Could not find node\node.exe next to this script.
  echo Make sure you extracted the WHOLE release zip, not just start.bat.
  pause
  exit /b 1
)

echo Starting ShipBreaker_SaveEditor...
echo A browser tab will open automatically at http://localhost:4173
echo.
echo Windows may show a one-time Firewall prompt the first time this runs.
echo Click "Allow access" -- this app only listens on your own PC (localhost),
echo it does not accept connections from your network or the internet.
echo.

start "" http://localhost:4173
"node\node.exe" "dist\server.js"

pause
