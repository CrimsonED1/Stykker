<#
.SYNOPSIS
    Builds the native CUDA library (nanocut_gpu.dll) that Stykker.NanoCut.Gpu calls through LibraryImport.

.DESCRIPTION
    nvcc needs the MSVC host compiler, and cl.exe is not on the PATH by default, so this script runs the whole build
    inside a cmd.exe that first calls vcvars64.bat of Visual Studio 2022.

    The library is optional and is not part of "dotnet build": CI has neither a GPU nor nvcc. When bin/nanocut_gpu.dll
    exists, the Stykker.NanoCut.Gpu project copies it to its output; when it does not, the managed CPU backend is all
    that is available and CudaRuntime reports why.

.PARAMETER VcVars
    Path of vcvars64.bat. Defaults to Visual Studio 2022 Community and falls back to vswhere.

.PARAMETER Architecture
    Compute capability of the card that runs the library, without the dot (120 for Blackwell, 89 for Ada, ...).
    SASS for that architecture is embedded, plus PTX for compute_75 so older and future cards still load it.

.PARAMETER Configuration
    Release (-O3) or Debug (-O0 -lineinfo).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build.ps1 -Architecture 89 -Configuration Debug
#>
[CmdletBinding()]
param(
    [string]$VcVars = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat',
    [string]$Architecture = '120',
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $here 'zmap.cu'
$binDir = Join-Path $here 'bin'
$target = Join-Path $binDir 'nanocut_gpu.dll'

if (-not (Test-Path $source)) { throw "missing source $source" }
New-Item -ItemType Directory -Force -Path $binDir | Out-Null

# Find vcvars64.bat: the default covers Visual Studio 2022 Community, vswhere covers every other installation.
if (-not (Test-Path $VcVars)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $installPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -property installationPath 2>$null | Select-Object -First 1
        if ($installPath) { $VcVars = Join-Path $installPath 'VC\Auxiliary\Build\vcvars64.bat' }
    }
}
if (-not (Test-Path $VcVars)) {
    throw "vcvars64.bat not found (looked at '$VcVars'). Pass -VcVars <path>; nvcc cannot run without the MSVC environment."
}

# Find nvcc: on the PATH, or in the CUDA toolkit named by CUDA_PATH.
$nvcc = $null
$onPath = Get-Command nvcc.exe -ErrorAction SilentlyContinue
if ($onPath) {
    $nvcc = $onPath.Source
}
elseif ($env:CUDA_PATH) {
    $candidate = Join-Path $env:CUDA_PATH 'bin\nvcc.exe'
    if (Test-Path $candidate) { $nvcc = $candidate }
}
if (-not $nvcc) { throw 'nvcc.exe not found on the PATH or in %CUDA_PATH%\bin; install the CUDA toolkit.' }

$optimise = if ($Configuration -eq 'Debug') { '-O0 -lineinfo' } else { '-O3' }
$gencode = "-gencode arch=compute_$Architecture,code=sm_$Architecture -gencode arch=compute_75,code=compute_75"
$nvccArgs = "$optimise -shared -std=c++17 -cudart static $gencode -Xcompiler `"/EHsc`" -o `"$target`" `"$source`""

Write-Host "nvcc $nvccArgs"
Write-Host "  (inside $VcVars)"
cmd.exe /c "call `"$VcVars`" >nul 2>&1 && `"$nvcc`" $nvccArgs"
if ($LASTEXITCODE -ne 0) { throw "nvcc failed with exit code $LASTEXITCODE" }

$size = (Get-Item $target).Length
Write-Host "built $target ($([math]::Round($size / 1kb)) KiB, sm_$Architecture + compute_75 PTX)"
Write-Host 'Rebuild Stykker.NanoCut.Gpu to copy it next to the managed assembly:'
Write-Host '  dotnet build src/Stykker.NanoCut.Gpu -c Release'
