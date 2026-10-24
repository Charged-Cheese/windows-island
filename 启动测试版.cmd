@echo off
setlocal
set "islandDir=%~dp0src\DynamicIsland\bin\Debug\net10.0-windows10.0.19041.0\win-x64"
if not exist "%islandDir%\DynamicIsland.exe" (
    echo Debug build not found. Build src\DynamicIsland\DynamicIsland.csproj first.
    pause
    exit /b 1
)
echo Please exit any running DynamicIsland instance before testing this build.
start "" /D "%islandDir%" "%islandDir%\DynamicIsland.exe"
