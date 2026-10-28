# PowerShell publish script for APTV Windows candidate builds
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir

$env:DOTNET_CLI_HOME = Join-Path $RepoRoot ".tools"
$env:NUGET_PACKAGES = Join-Path $RepoRoot ".tools\nuget"
$env:DOTNET_ROOT = Join-Path $RepoRoot ".tools\dotnet"

Write-Host "Executing APTV Windows candidate publish workflow via build_package.py..."
python "$ScriptDir\build_package.py"
