# Bump the plugin version, update pluginmaster.json, commit, and tag.
#
# Usage:
#   .\bump-version.ps1 1.1.0.0
#
# Then push to trigger the release workflow:
#   git push && git push --tags

param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# --- csproj ---
$csproj = Join-Path $root "ActionCamera.csproj"
$xml    = [xml](Get-Content $csproj)
$xml.Project.PropertyGroup[0].AssemblyVersion = $Version
$xml.Save($csproj)
Write-Host "csproj AssemblyVersion → $Version"

# --- pluginmaster.json ---
$master = Join-Path $root "pluginmaster.json"
$json   = Get-Content $master | ConvertFrom-Json
$json[0].AssemblyVersion = $Version
$json[0].LastUpdate      = [int](Get-Date -UFormat %s)
$json | ConvertTo-Json -Depth 5 | Set-Content $master
Write-Host "pluginmaster.json → $Version"

# --- git commit + tag ---
git -C $root add ActionCamera.csproj pluginmaster.json
git -C $root commit -m "chore: bump version to $Version"
git -C $root tag "v$Version"

Write-Host ""
Write-Host "Done. Push with:"
Write-Host "  git push && git push --tags"
