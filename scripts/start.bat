@echo off
setlocal
cd /d "%~dp0.."

if /i "%~1"=="dev"    goto dev
if /i "%~1"=="docker" goto docker
if /i "%~1"==""       goto docker

echo Usage: scripts\start.bat [docker^|dev]
echo.
echo   docker  (default)  Everything in containers. Matches how the project is graded.
echo   dev                Infrastructure in containers, API and web on the host for hot reload.
exit /b 1

:docker
call :require_docker || exit /b 1

echo Building and starting the full stack...
docker compose up --build -d
if errorlevel 1 (
  echo.
  echo Startup failed. Check the output above, or run: docker compose logs api
  exit /b 1
)

echo.
echo   Web app    http://localhost:8081
echo   API        http://localhost:8080
echo   API docs   http://localhost:8080/scalar/v1
echo   Health     http://localhost:8080/health
echo   RabbitMQ   http://localhost:15672   (guest / guest)
echo.
echo The API waits for MongoDB, Redis and RabbitMQ to report healthy, so the first
echo start takes a little longer. Watch it with: docker compose logs -f api
echo.
echo Stop with: scripts\stop.bat
exit /b 0

:dev
call :require_docker || exit /b 1

echo Starting infrastructure only (MongoDB, Redis, RabbitMQ)...
docker compose up -d mongo redis rabbitmq
if errorlevel 1 exit /b 1

echo Opening the API and the Vite dev server in separate windows...
start "InventoryHold API" cmd /k dotnet watch --project src\InventoryHold.WebApi
start "InventoryHold Web" cmd /k "cd /d web && npm run dev"

echo.
echo   API   http://localhost:5095
echo   Web   http://localhost:5173
echo.
echo Both run in their own windows. Close a window to stop that process.
echo The containers keep running - shut them down with: scripts\stop.bat
echo.
echo If the API window reports a MongoDB server-selection timeout: the replica set
echo advertises itself as "mongo:27017", which only resolves inside Docker. Either add
echo   127.0.0.1 mongo
echo to C:\Windows\System32\drivers\etc\hosts, or run the API with
echo   set Mongo__ConnectionString=mongodb://localhost:27017/?directConnection^=true
echo   set Mongo__UseTransactions=false
echo Note the second one turns off multi-item atomicity - see ADR-002.
exit /b 0

:require_docker
docker ps >nul 2>&1
if errorlevel 1 (
  echo.
  echo Docker is not responding.
  echo.
  echo Start Docker Desktop and wait until it reports "Running", then try again.
  echo If it is already running, its engine may have wedged - restart Docker Desktop.
  echo.
  exit /b 1
)
exit /b 0
