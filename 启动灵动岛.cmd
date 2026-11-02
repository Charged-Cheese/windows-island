@echo off
setlocal
set "islandDir=%~dp0src\DynamicIsland\bin\Release\net10.0-windows10.0.19041.0\win-x64"
if not exist "%islandDir%\DynamicIsland.exe" (
    echo No local Release build was found. This launcher does not build the project.
    echo This update and future updates provide source code only, without prebuilt binaries.
    pause
    exit /b 1
)
echo Please exit any running DynamicIsland instance before launching this version.
start "" /D "%islandDir%" "%islandDir%\DynamicIsland.exe"
