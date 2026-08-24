@echo off
setlocal
cd /d "%~dp0.."

if /i "%~1"=="clean" goto clean
if /i "%~1"==""      goto stop

echo Usage: scripts\stop.bat [clean]
echo.
echo   (no argument)  Stop the containers. Data is kept.
echo   clean          Stop and delete the MongoDB volume as well.
exit /b 1

:stop
docker ps >nul 2>&1
if errorlevel 1 (
  echo Docker is not responding, so there is nothing this script can stop.
  exit /b 1
)

docker compose down
if errorlevel 1 exit /b 1

echo.
echo Stopped. Volumes kept, so stock levels and holds survive the next start.
echo To wipe them too: scripts\stop.bat clean
exit /b 0

:clean
docker ps >nul 2>&1
if errorlevel 1 (
  echo Docker is not responding, so there is nothing this script can stop.
  exit /b 1
)

echo.
echo This deletes the MongoDB volume. Every hold and the seeded stock levels go with it.
choice /c YN /n /m "Continue? [Y/N] "
if errorlevel 2 (
  echo Cancelled. Nothing was removed.
  exit /b 0
)

docker compose down -v
if errorlevel 1 exit /b 1

echo.
echo Stopped and volumes removed. The next start reseeds the five products.
exit /b 0
