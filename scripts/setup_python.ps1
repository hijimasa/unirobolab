# Set up the Python side of UniRoboLab on Windows with uv (no system Python is touched).
#   powershell -ExecutionPolicy Bypass -File scripts\setup_python.ps1 [-Training]
# Creates <root>\.venv with the runtime extras; -Training adds stable-baselines3 and CPU torch
# for training from the GUI. The GUI finds .venv\Scripts\python.exe by itself.
param([switch]$Training)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
  Write-Host "installing uv (https://astral.sh/uv)"
  Invoke-RestMethod https://astral.sh/uv/install.ps1 | Invoke-Expression
  $env:Path = "$env:USERPROFILE\.local\bin;$env:Path"
}
$venv = Join-Path $root ".venv"
uv venv -q $venv
$extras = if ($Training) { "runtime,training" } else { "runtime" }
uv pip install -q --python (Join-Path $venv "Scripts\python.exe") -e "$root\python[$extras]"
Write-Host "ready: $venv"
Write-Host "  unirobolab: $venv\Scripts\unirobolab.exe"
