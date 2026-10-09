# Packt die npm-Pakete (stykker-llm + @stykker-llm/win32-x64) lokal oder in CI.
# Nichts wird veröffentlicht - nur .tgz-Artefakte in <OutDir>\npm.
# Das Plattformpaket enthält den App-Ordner von build\make-release.ps1 (Unterordner app\ mit StykkerUI.exe).
#
# Beispiel:
#   powershell -ExecutionPolicy Bypass -File build\pack-npm.ps1 -Version 0.1.0

param(
    [Parameter(Mandatory = $true)][string]$Version,          # z. B. 0.1.0 (aus dem Tag)
    [string]$SourceDir = "",                                  # fertiger App-Ordner; ohne Angabe <OutDir>\publish\app (wird bei Bedarf erzeugt)
    [string]$OutDir = "",                                     # leere Angabe = build\out ($PSScriptRoot steht in Param-Defaults nicht zur Verfügung)
    [switch]$NoPack                                           # nur Dateien legen, kein npm pack
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($OutDir -eq "") { $OutDir = Join-Path $PSScriptRoot "out" }
# absolut machen: npm pack läuft im Paketordner, ein relativer Zielpfad (wie im Release-Workflow) zeigte sonst dorthin
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.\-]+)?$') {
    throw "Ungültige Version '$Version' - erwartet wird SemVer wie 0.1.0 oder 0.0.0-local"
}

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$exeName = "StykkerUI.exe"

# ── 1. App-Ordner beschaffen ──
if ($SourceDir -eq "") {
    $SourceDir = Join-Path $OutDir "publish\app"
    if (-not (Test-Path (Join-Path $SourceDir $exeName))) {
        Write-Host "App-Ordner fehlt, rufe make-release.ps1 auf ..."
        & (Join-Path $PSScriptRoot "make-release.ps1") -Version $Version -OutDir $OutDir
    }
}
if (-not (Test-Path (Join-Path $SourceDir $exeName))) { throw "$exeName nicht gefunden in: $SourceDir" }
$SourceDir = (Resolve-Path $SourceDir).Path
Write-Host "App-Ordner: $SourceDir ($([math]::Round(((Get-ChildItem $SourceDir -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)) MB)"

# ── 2. Versionen in beide package.json schreiben (ohne BOM) ──
function Set-TextNoBom([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}
function Set-Version([string]$jsonPath, [string]$ver) {
    $text = Get-Content $jsonPath -Raw
    $text = $text -replace '"version"\s*:\s*"[^"]+"', ('"version": "' + $ver + '"')
    Set-TextNoBom $jsonPath $text
    Write-Host "Version $ver -> $jsonPath"
}
Set-Version (Join-Path $root "npm\stykker-llm\package.json") $Version
Set-Version (Join-Path $root "npm\win32-x64\package.json") $Version

# optionale Abhängigkeit auf dieselbe Version ziehen
$mainJson = Join-Path $root "npm\stykker-llm\package.json"
$text = Get-Content $mainJson -Raw
$text = $text -replace '("@stykker-llm/win32-x64"\s*:\s*")[^"]+(")', ('${1}' + $Version + '${2}')
Set-TextNoBom $mainJson $text

# ── 3. App-Ordner in das Plattformpaket kopieren (npm\win32-x64\app) ──
$appTarget = Join-Path $root "npm\win32-x64\app"
if (Test-Path $appTarget) { Remove-Item $appTarget -Recurse -Force }
Copy-Item $SourceDir $appTarget -Recurse -Force
Write-Host "App kopiert nach npm\win32-x64\app"

# ── 4. npm pack (beide Pakete) ──
$artifactDir = Join-Path $OutDir "npm"
New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
if (-not $NoPack) {
    foreach ($dir in @("npm\stykker-llm", "npm\win32-x64")) {
        $full = Join-Path $root $dir
        Write-Host "npm pack: $dir"
        Push-Location $full
        try {
            & npm.cmd pack --pack-destination $artifactDir
            if ($LASTEXITCODE -ne 0) { throw "npm pack fehlgeschlagen in $dir" }
        }
        finally { Pop-Location }
    }
    Write-Host "Artefakte:"
    Get-ChildItem $artifactDir -Filter *.tgz | ForEach-Object { Write-Host ("  " + $_.Name + " (" + [math]::Round($_.Length / 1MB, 1) + " MB)") }
}

Write-Host "Fertig. Veröffentlichen passiert bewusst nicht hier: npm publish ist ein eigener, manueller Schritt (Plattformpaket zuerst)."
