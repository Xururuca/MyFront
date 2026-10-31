@echo off
rem  MyFront release build: single-file, ReadyToRun-compiled exe.
rem    build.bat        framework-dependent  -> publish\fd\MyFront.exe  (needs .NET 10 Desktop Runtime)
rem    build.bat sc     self-contained       -> publish\sc\MyFront.exe  (no runtime needed, larger)
rem    build.bat all    both
rem    build.bat test   run the self-tests against the server in testdb.local.json
setlocal
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set DOTNET_CLI_UI_LANGUAGE=en
cd /d "%~dp0"

set COMMON=-c Release -r win-x64 -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none

rem  fd and sc need their own bin/obj: sc single-file defaults to *composite* ReadyToRun, and with a shared
rem  obj\...\R2R the next fd publish counts those images as up to date and bundles them -> fd exe fail-fasts at start.
set FD_DIRS=-p:OutputPath=bin\Release\fd\ -p:IntermediateOutputPath=obj\Release\fd\
set SC_DIRS=-p:OutputPath=bin\Release\sc\ -p:IntermediateOutputPath=obj\Release\sc\

if /i "%1"=="test" goto test
if /i "%1"=="sc" goto sc

:fd
echo === framework-dependent ===
dotnet publish src\MyFront\MyFront.csproj %COMMON% %FD_DIRS% --self-contained false -o publish\fd || exit /b 1
echo.
echo   publish\fd\MyFront.exe
if /i not "%1"=="all" goto end

:sc
echo === self-contained ===
dotnet publish src\MyFront\MyFront.csproj %COMMON% %SC_DIRS% --self-contained true -o publish\sc || exit /b 1
echo.
echo   publish\sc\MyFront.exe
goto end

:test
dotnet run --project tests\MyFront.SelfTest -c Release -- all || exit /b 1

:end
endlocal
