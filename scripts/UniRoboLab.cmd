@echo off
rem Launch the UniRoboLab GUI on Windows (release layout: this file next to player\UniRoboLab.exe).
rem   UniRoboLab.cmd [project dir]
rem Needs Git for Windows (bash.exe) for the Python-side commands, and .venv from scripts\setup_python.ps1.
setlocal
set ROOT=%~dp0
if "%SIMULATION_RESOURCES_CONFIG%"=="" set SIMULATION_RESOURCES_CONFIG=%ROOT%scripts\gui_resources.json
if "%SIM_LEARNING_PORT%"=="" set SIM_LEARNING_PORT=10100
if not "%~1"=="" set SIM_WIZARD_PROJECT=%~f1
if not exist "%ROOT%player\UniRoboLab.exe" (
  echo player not found: %ROOT%player\UniRoboLab.exe
  pause
  exit /b 1
)
start "" "%ROOT%player\UniRoboLab.exe" -screen-width 1280 -screen-height 800 -screen-fullscreen 0
endlocal
