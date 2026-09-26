# Onta ClickOnce publish script (win-x64 + win-arm64)
# Usage:
#   .\publish.ps1
#   .\publish.ps1 -Configuration Release
#   .\publish.ps1 -Clean

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$Clean
)

$ErrorActionPreference = "Stop"
$installerRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $installerRoot
$project = Join-Path $repoRoot "Onta_core\Onta_core.csproj"
$profile = Join-Path $repoRoot "Onta_core\Properties\PublishProfiles\ClickOnceFolder.pubxml"
$publishRoot = Join-Path $installerRoot "publish"
$appPublishRoot = Join-Path $installerRoot "obj\app"
$distReadmeSrc = Join-Path $installerRoot "dist-README.txt"

# ClickOnce is one architecture per deployment; ship both side by side.
$targets = @(
    @{ Rid = "win-x64";   Folder = "x64";   Product = "Onta x64";   ArchLabel = "x64" },
    @{ Rid = "win-arm64"; Folder = "arm64"; Product = "Onta ARM64"; ArchLabel = "ARM64" }
)

if (-not (Test-Path -LiteralPath $project)) {
    throw "Project not found: $project"
}
if (-not (Test-Path -LiteralPath $profile)) {
    throw "Publish profile not found: $profile"
}

function Resolve-MsBuild {
    # ClickOnce needs full Framework MSBuild (GenerateBootstrapper / GenerateLauncher).
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere) {
        $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" |
            Select-Object -First 1
        if ($msbuild -and (Test-Path -LiteralPath $msbuild)) {
            return $msbuild
        }
    }

    $fallback = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
    )
    foreach ($path in $fallback) {
        if (Test-Path -LiteralPath $path) {
            return $path
        }
    }

    throw "Framework MSBuild not found. Install Visual Studio 2022 Build Tools with ClickOnce Build Tools."
}

function Publish-ClickOnceArch {
    param(
        [string]$MsBuild,
        [string]$Rid,
        [string]$Folder,
        [string]$ProductName,
        [string]$ArchLabel
    )

    $clickOnceDir = (Join-Path $publishRoot $Folder).TrimEnd('\') + '\'
    $appDir = (Join-Path $appPublishRoot $Folder).TrimEnd('\') + '\'

    New-Item -ItemType Directory -Force -Path $clickOnceDir | Out-Null
    New-Item -ItemType Directory -Force -Path $appDir | Out-Null

    Write-Host ""
    Write-Host "=== Publishing $ArchLabel ($Rid) ==="
    Write-Host "  ClickOnce -> $clickOnceDir"

    & $MsBuild $project `
        /nologo `
        /restore `
        /t:Publish `
        /p:Configuration=$Configuration `
        /p:PublishProfile=$profile `
        /p:RuntimeIdentifier=$Rid `
        /p:SelfContained=true `
        "/p:ProductName=$ProductName" `
        /p:ClickOncePublishDir=$clickOnceDir `
        /p:PublishUrl=$clickOnceDir `
        /p:PublishDir=$appDir `
        /p:ApplicationRevision=0 `
        /p:IsRevisionIncremented=false

    if ($LASTEXITCODE -ne 0) {
        throw "ClickOnce publish failed for $Rid (exit code $LASTEXITCODE)"
    }

    $strayLauncher = Join-Path $clickOnceDir "Launcher.exe"
    if (Test-Path -LiteralPath $strayLauncher) {
        Remove-Item -LiteralPath $strayLauncher -Force
    }

    $setup = Join-Path $clickOnceDir "setup.exe"
    $appManifest = Join-Path $clickOnceDir "Onta.application"
    if (-not (Test-Path -LiteralPath $setup)) {
        throw "setup.exe missing after publish: $setup"
    }
    if (-not (Test-Path -LiteralPath $appManifest)) {
        throw "Onta.application missing after publish: $appManifest"
    }
}

$tool = Resolve-MsBuild
Write-Host "Tool: $tool"
Write-Host "Project: $project"
Write-Host "Profile: $profile"
Write-Host "Output: $publishRoot (x64 + arm64)"

if ($Clean) {
    foreach ($dir in @($publishRoot, $appPublishRoot)) {
        if (Test-Path -LiteralPath $dir) {
            Write-Host "Cleaning $dir ..."
            Remove-Item -LiteralPath $dir -Recurse -Force
        }
    }
}

New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

foreach ($t in $targets) {
    Publish-ClickOnceArch -MsBuild $tool -Rid $t.Rid -Folder $t.Folder -ProductName $t.Product -ArchLabel $t.ArchLabel
}

# Root chooser: detect CPU and launch the matching setup.exe
$chooserPath = Join-Path $publishRoot "Install-Onta.cmd"
@(
    '@echo off',
    'setlocal',
    'cd /d "%~dp0"',
    'echo Onta ClickOnce installer',
    'echo.',
    'if /I "%PROCESSOR_ARCHITECTURE%"=="ARM64" goto ARM64',
    'if /I "%PROCESSOR_ARCHITEW6432%"=="ARM64" goto ARM64',
    'echo Architecture: x64',
    'start "" "%~dp0x64\setup.exe"',
    'exit /b 0',
    ':ARM64',
    'echo Architecture: ARM64',
    'start "" "%~dp0arm64\setup.exe"',
    'exit /b 0'
) | Set-Content -LiteralPath $chooserPath -Encoding ASCII

if (Test-Path -LiteralPath $distReadmeSrc) {
    Copy-Item -LiteralPath $distReadmeSrc -Destination (Join-Path $publishRoot "README.txt") -Force
}

Write-Host ""
Write-Host "Publish completed (x64 + arm64)."
Write-Host "  Install-Onta.cmd / x64\ / arm64\  -> $publishRoot"
Get-ChildItem -LiteralPath $publishRoot | Select-Object Name, Mode, Length | Format-Table -AutoSize
foreach ($folder in @("x64", "arm64")) {
    $dir = Join-Path $publishRoot $folder
    Write-Host "-- $folder --"
    Get-ChildItem -LiteralPath $dir -File | Select-Object Name, Length | Format-Table -AutoSize
}
