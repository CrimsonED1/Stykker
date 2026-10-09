# Füllt das winget-Manifest-Template (winget/CrimsonED1.StykkerLLM.template.yaml) aus.
# Es wird nichts hochgeladen oder veröffentlicht: das Ergebnis liegt als Artefakt in <OutDir>.

param(
    [Parameter(Mandatory = $true)][string]$Version,           # z. B. 0.1.0 (ohne v)
    [Parameter(Mandatory = $true)][string]$Sha256,            # SHA256 des Zip-Installers (Kleinbuchstaben-Hex, 64 Stellen)
    [string]$InstallerUrl = "",                               # z. B. https://github.com/CrimsonED1/Stykker-LLM/releases/download/v0.1.0/StykkerLLM-0.1.0-win-x64.zip
    [string]$OutDir = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($OutDir -eq "") { $OutDir = Join-Path $PSScriptRoot "out" }
# absolut machen: [IO.File] löst relative Pfade gegen das Prozessverzeichnis auf, nicht gegen den PowerShell-Ort
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.\-]+)?$') { throw "Ungültige Version '$Version'" }
if ($Sha256 -notmatch '^[0-9a-f]{64}$') {
    if ($Sha256 -match '^[0-9A-F]{64}$') { $Sha256 = $Sha256.ToLowerInvariant() }
    else { throw "Ungültiger SHA256 '$Sha256' - erwartet werden 64 Hex-Zeichen" }
}
if ($InstallerUrl -eq "") {
    $InstallerUrl = "https://github.com/CrimsonED1/Stykker-LLM/releases/download/v$Version/StykkerLLM-$Version-win-x64.zip"
}
if ($Version -match '-') { Write-Warning "Vorabversionen ($Version) nimmt winget-pkgs nur mit Erklärung an - für den PR eine stabile Version verwenden." }

$templatePath = Join-Path $PSScriptRoot "..\winget\CrimsonED1.StykkerLLM.template.yaml"
$template = Get-Content $templatePath -Raw -Encoding UTF8
$releaseDate = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")

$manifest = $template
$manifest = $manifest.Replace('{VERSION}', $Version)
$manifest = $manifest.Replace('{RELEASE_DATE}', $releaseDate)
$manifest = $manifest.Replace('{INSTALLER_URL}', $InstallerUrl)
$manifest = $manifest.Replace('{SHA256}', $Sha256)

# Zielstruktur wie im winget-pkgs-Repo: manifests/<ein Buchstabe>/<Publisher>/<Paket>/<Version>/CrimsonED1.StykkerLLM.yaml
$targetDir = Join-Path $OutDir ("winget\manifests\C\CrimsonED1\StykkerLLM\" + $Version)
New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
$target = Join-Path $targetDir "CrimsonED1.StykkerLLM.yaml"
[System.IO.File]::WriteAllText($target, $manifest, (New-Object System.Text.UTF8Encoding($false)))

# Verbleibende Platzhalter prüfen
if ($manifest -match '\{(VERSION|RELEASE_DATE|INSTALLER_URL|SHA256)\}') { throw "Platzhalter blieben unausgefüllt: $target" }

Write-Host "winget-Manifest geschrieben: $target"
Write-Host "InstallerUrl: $InstallerUrl"
Write-Host "Sha256:       $Sha256"