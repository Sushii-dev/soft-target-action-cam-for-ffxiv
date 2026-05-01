# Serves pluginmaster.json and the built plugin zip on http://localhost:8080
# so Dalamud can browse and install from a local custom repo.
#
# Usage (from repo root, after building Release):
#   .\serve-local.ps1
#
# Then in Dalamud settings > Experimental > Custom Plugin Repos, add:
#   http://localhost:8080/pluginmaster.json

$root   = $PSScriptRoot
$output = Join-Path $root "bin\x64\Release\ActionCamera"

# Update pluginmaster.json timestamp so Dalamud knows there's a new version.
$master = Join-Path $root "pluginmaster.json"
$json   = Get-Content $master | ConvertFrom-Json
$json[0].LastUpdate = [int](Get-Date -UFormat %s)

# Pull AssemblyVersion from the built DLL.
$dll = Join-Path $output "ActionCamera.dll"
if (Test-Path $dll) {
    $ver = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion
    $json[0].AssemblyVersion = $ver
}

# Write back to a temp location served by the HTTP server (don't dirty the repo).
$serveRoot = Join-Path $env:TEMP "ActionCameraServe"
New-Item -ItemType Directory -Force -Path $serveRoot | Out-Null
$json | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $serveRoot "pluginmaster.json")

# Copy the plugin zip into the serve directory.
$zipSrc  = Join-Path $output "latest.zip"
$zipDest = Join-Path $serveRoot "ActionCamera"
New-Item -ItemType Directory -Force -Path $zipDest | Out-Null
if (Test-Path $zipSrc) {
    Copy-Item $zipSrc $zipDest -Force
    Write-Host "Serving plugin zip from $zipSrc"
} else {
    Write-Warning "latest.zip not found at $zipSrc — build Release first."
}

# Start a simple HTTP server.
Write-Host ""
Write-Host "Listening on http://localhost:8080"
Write-Host "Add http://localhost:8080/pluginmaster.json to Dalamud custom repos."
Write-Host "Press Ctrl+C to stop."
Write-Host ""

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:8080/")
$listener.Start()

try {
    while ($listener.IsListening) {
        $ctx  = $listener.GetContext()
        $path = $ctx.Request.Url.LocalPath.TrimStart('/')
        $file = Join-Path $serveRoot $path

        if (Test-Path $file -PathType Leaf) {
            $bytes = [System.IO.File]::ReadAllBytes($file)
            $ctx.Response.ContentLength64 = $bytes.Length
            $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        } else {
            $ctx.Response.StatusCode = 404
        }
        $ctx.Response.Close()
    }
} finally {
    $listener.Stop()
}
