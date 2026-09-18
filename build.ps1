# Builds the plugin in Release and packs dist\LoupixDeck.Plugin.Twitch-<version>.zip.
# Layout matches the LoupixDeck plugin release workflow: plugin.json plus every
# build output at the zip root, minus *.pdb, *.runtimeconfig.json and the SDK dll
# (the host supplies LoupixDeck.PluginSdk.dll itself).
[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$root     = $PSScriptRoot
$project  = Join-Path $root 'src\LoupixDeck.Plugin.Twitch\LoupixDeck.Plugin.Twitch.csproj'
$manifest = Get-Content (Join-Path $root 'src\LoupixDeck.Plugin.Twitch\plugin.json') -Raw | ConvertFrom-Json
$version  = $manifest.version
$outDir   = Join-Path $root 'src\LoupixDeck.Plugin.Twitch\bin\Release'
$stage    = Join-Path $root 'dist\stage'
$zipPath  = Join-Path $root "dist\LoupixDeck.Plugin.Twitch-$version.zip"

& $Dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Get-ChildItem $outDir -File |
    Where-Object {
        $_.Extension -ne '.pdb' -and
        $_.Name -notlike '*.runtimeconfig.json' -and
        $_.Name -ne 'LoupixDeck.PluginSdk.dll'
    } |
    Copy-Item -Destination $stage

if (-not (Test-Path (Join-Path $stage 'plugin.json'))) { throw 'plugin.json missing from build output.' }

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath)
Remove-Item $stage -Recurse -Force

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Packed $zipPath"
Write-Host "SHA256 $hash"
