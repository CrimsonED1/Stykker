# Baut die Veröffentlichung: das Fenster StykkerUI (src\StykkerLlm.UI), die Terminal-CLI "stykker" (src\StykkerLlm.Cli) und den
# Server (src\StykkerLlm.Server, seit S7) als selbstenthaltene Einzeldateien für Windows x64 (.NET ist eingebettet und komprimiert,
# auf dem Zielrechner muss nichts installiert sein), packt alles in ein Zip und schreibt SHA256SUMS.txt.
# Das eingefrorene MAUI-Projekt wird nicht gebaut oder gepackt.
# Es wird nichts veröffentlicht oder hochgeladen. Läuft unter Windows PowerShell 5 und PowerShell 7.
#
# Beispiel:
#   powershell -ExecutionPolicy Bypass -File build\make-release.ps1 -Version 0.1.0
#
# Ergebnis in <OutDir> (Standard build\out):
#   publish\app\                                  StykkerUI.exe, stykker.exe, StykkerLLM-Server.exe, StykkerHost.exe (je eine Datei) plus LICENSE
#   StykkerLLM-<Version>-win-x64.zip     Ordner "StykkerLLM" mit dem Inhalt von publish\app
#   SHA256SUMS.txt

param(
    [Parameter(Mandatory = $true)][string]$Version,   # z. B. 0.1.0 (ohne v)
    [string]$OutDir = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.\-]+)?$') { throw "Ungültige Version '$Version' - erwartet wird SemVer wie 0.1.0" }
if ($OutDir -eq "") { $OutDir = Join-Path $PSScriptRoot "out" }

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishDir = Join-Path $OutDir "publish\app"
$folderName = "StykkerLLM"
$zipName = "StykkerLLM-$Version-win-x64.zip"
$zipPath = Join-Path $OutDir $zipName

# ── 1. Veröffentlichen (je eine selbstenthaltene Einzeldatei) ──
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
$single = @("-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:EnableCompressionInSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:Version=$Version", "-p:DebugType=none", "-p:DebugSymbols=false")
foreach ($proj in @("StykkerLlm.UI", "StykkerLlm.Cli", "StykkerLlm.Server", "StykkerLlm.Host"))
{
    Write-Host "dotnet publish src/$proj ($Version) -> $publishDir"
    dotnet publish (Join-Path $root "src\$proj\$proj.csproj") @single -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen: $proj" }
}
Copy-Item (Join-Path $root "LICENSE") (Join-Path $publishDir "LICENSE") -Force
$exe = Join-Path $publishDir "StykkerUI.exe"   # das Fenster um die Weboberfläche (Photino, ohne GPU)
$cli = Join-Path $publishDir "stykker.exe"
$server = Join-Path $publishDir "StykkerLLM-Server.exe"
foreach ($f in @($exe, $cli, $server)) { if (-not (Test-Path $f)) { throw "exe wurde nicht erzeugt: $f" } }
# Debug-Symbole referenzierter Projekte (Core, Platform) gehören nicht ins Release
Get-ChildItem $publishDir -File -Filter *.pdb | Remove-Item -Force
# Der Server bringt zwei Dateien mit, die zu ihm gehören (statische Dateien, IIS-Konfiguration)
$serverExtra = @("StykkerLLM-Server.staticwebassets.endpoints.json", "web.config")
$extra = Get-ChildItem $publishDir -File | Where-Object { $_.Name -notin (@("StykkerUI.exe", "stykker.exe", "StykkerLLM-Server.exe", "StykkerHost.exe", "app.ico", "LICENSE") + $serverExtra) }
if ($extra) { Write-Warning ("Zusätzliche Dateien im App-Ordner: " + (($extra | ForEach-Object Name) -join ", ")) }
$mb = [math]::Round(((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
Write-Host "App-Ordner: $mb MB, StykkerUI.exe $([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB, stykker.exe $([math]::Round((Get-Item $cli).Length / 1MB, 1)) MB, StykkerLLM-Server.exe $([math]::Round((Get-Item $server).Length / 1MB, 1)) MB"

# ── 2. Zip mit Ordner (Pfade mit / wie überall üblich) ──
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
$base = (Resolve-Path $publishDir).Path.TrimEnd('\')
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try
{
    foreach ($f in Get-ChildItem $base -Recurse -File)
    {
        $rel = $f.FullName.Substring($base.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, "$folderName/$rel", [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $zip.Dispose() }
Write-Host "Zip: $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB)"

# ── 3. Prüfsumme ──
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
$sums = Join-Path $OutDir "SHA256SUMS.txt"
[System.IO.File]::WriteAllText($sums, "$hash  $zipName`n", (New-Object System.Text.UTF8Encoding($false)))
Write-Host "SHA256: $hash"
Write-Host "Fertig. Veröffentlicht wird nichts: das Zip und SHA256SUMS.txt liegen in $OutDir."

# ── 4. Lokale Ablage (nur auf dem Rechner des Entwicklers, falls die Ordner neben dem Repo existieren) ──
# <Repo>_release: Zip und SHA256SUMS je Version; <Repo>_own: eigene, entpackte Arbeitskopie (nur wenn die App dort nicht läuft)
$repoDir = Split-Path $PSScriptRoot -Parent
$releaseDir = "$repoDir" + "_release"
$ownDir = "$repoDir" + "_own"
if (Test-Path $releaseDir) { Copy-Item $zipPath, $sums $releaseDir -Force; Write-Host "Kopiert nach $releaseDir" }
if (Test-Path $ownDir) {
    $busy = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($ownDir, [StringComparison]::OrdinalIgnoreCase) }
    if ($busy) { Write-Host "Nicht entpackt: die App läuft aus $ownDir" }
    else { Expand-Archive $zipPath -DestinationPath $ownDir -Force; Write-Host "Entpackt nach $ownDir" }
}