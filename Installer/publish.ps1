# Onta ClickOnce publish script (win-x64 + win-arm64)
# Output: Installer\Onta\ (distribution folder) and Installer\Onta-install-v<version>.exe (self-extracting, needs 7-Zip to build)
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
if (-not (Test-Path -LiteralPath $project)) {
    throw "Project not found: $project"
}
# Installer file name carries the app version (<Version> in Onta_core.csproj)
$appVersion = ([xml][System.IO.File]::ReadAllText($project, [System.Text.Encoding]::UTF8)).Project.PropertyGroup |
    ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
if (-not $appVersion) {
    throw "<Version> not found in $project"
}
# Distribution folder (Installer\Onta) and its self-extracting installer (Installer\Onta-install-v<version>.exe, extracts to an Onta folder)
$publishRoot = Join-Path $installerRoot "Onta"
$sfxPath = Join-Path $installerRoot ("Onta-install-v{0}.exe" -f $appVersion.Trim())
$appPublishRoot = Join-Path $installerRoot "obj\app"
$distReadmes = @(
    @{ Source = Join-Path $installerRoot "dist-README-ja.txt"; Name = "README-ja.txt" },
    @{ Source = Join-Path $installerRoot "dist-README-en.txt"; Name = "README-en.txt" }
)
$thirdPartyNoticesSrc = Join-Path $installerRoot "THIRD-PARTY-NOTICES.txt"
$installLauncherSrc = Join-Path $installerRoot "Install-Onta.ps1"

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

function Resolve-SevenZip {
    # The self-extracting archive uses the 7-Zip GUI SFX module (7z.sfx next to 7z.exe).
    $candidates = @(
        (Join-Path $env:ProgramFiles "7-Zip\7z.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "7-Zip\7z.exe")
    )
    $onPath = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($onPath) {
        $candidates += $onPath.Source
    }
    foreach ($path in $candidates) {
        if ($path -and (Test-Path -LiteralPath $path) -and (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $path) "7z.sfx"))) {
            return $path
        }
    }

    throw "7-Zip (7z.exe + 7z.sfx) not found. Install 7-Zip to build the self-extracting installer (Onta-install-v<version>.exe)."
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
        /nodeReuse:false `
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
$sevenZip = Resolve-SevenZip
Write-Host "Tool: $tool"
Write-Host "7-Zip: $sevenZip"
Write-Host "Project: $project"
Write-Host "Profile: $profile"
Write-Host "Output: $publishRoot (x64 + arm64) -> $sfxPath"

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

# Root launcher: choose the display language (Install-Onta.ps1), then start the setup.exe for this CPU
Copy-Item -LiteralPath $installLauncherSrc -Destination (Join-Path $publishRoot "Install-Onta.ps1") -Force
$chooserPath = Join-Path $publishRoot "Install-Onta.cmd"
@(
    '@echo off',
    'setlocal',
    'cd /d "%~dp0"',
    'powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0Install-Onta.ps1"',
    'exit /b %ERRORLEVEL%'
) | Set-Content -LiteralPath $chooserPath -Encoding ASCII

foreach ($readme in $distReadmes) {
    Copy-Item -LiteralPath $readme.Source -Destination (Join-Path $publishRoot $readme.Name) -Force
}
$oldReadme = Join-Path $publishRoot "README.txt"
if (Test-Path -LiteralPath $oldReadme) {
    Remove-Item -LiteralPath $oldReadme -Force
}
if (Test-Path -LiteralPath $thirdPartyNoticesSrc) {
    Copy-Item -LiteralPath $thirdPartyNoticesSrc -Destination (Join-Path $publishRoot "THIRD-PARTY-NOTICES.txt") -Force
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

# Self-extracting installer: Onta-install-v<version>.exe asks for a destination and extracts the Onta folder there.
# 7z "a" adds to an existing archive, so remove the previous one first.
if (Test-Path -LiteralPath $sfxPath) {
    Remove-Item -LiteralPath $sfxPath -Force
}
Write-Host ""
Write-Host "=== Creating self-extracting archive ==="
$sfxModule = Join-Path (Split-Path -Parent $sevenZip) "7z.sfx"
& $sevenZip a -t7z -mx=7 -mmt=on "-sfx$sfxModule" $sfxPath $publishRoot
if ($LASTEXITCODE -ne 0) {
    throw "7-Zip failed to create $sfxPath (exit code $LASTEXITCODE)"
}
Write-Host ("Self-extracting archive: {0} ({1:N1} MB)" -f $sfxPath, ((Get-Item -LiteralPath $sfxPath).Length / 1MB))
