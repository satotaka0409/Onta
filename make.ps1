# Onta C# build helper for Windows PowerShell
# Usage:
#   .\make.ps1
#   .\make.ps1 build
#   .\make.ps1 clean
#   .\make.ps1 test
#   .\make.ps1 testdebug
#   .\make.ps1 rebuild -Config Debug
#   .\make.ps1 build -Dotnet "C:\Program Files\dotnet\dotnet.exe"
#   .\make.cmd build

param(
    [Parameter(Position = 0)]
    [ValidateSet("all", "build", "clean", "rebuild", "test", "testdebug")]
    [string]$Target = "build",

    [string]$Config = "Release",

    [string]$Dotnet = "dotnet"
)

$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot

$project = Join-Path $PSScriptRoot "Onta_core\Onta_core.csproj"
$testProject = Join-Path $PSScriptRoot "Onta_core\test\Onta_core.Tests.csproj"

function Resolve-Dotnet {
    param([string]$Candidate)

    if ($Candidate) {
        $cmd = Get-Command -Name $Candidate -ErrorAction SilentlyContinue
        if ($cmd) {
            return $cmd.Source
        }

        if (Test-Path -LiteralPath $Candidate) {
            return (Resolve-Path -LiteralPath $Candidate).Path
        }
    }

    $fallback = @(
        (Join-Path ${env:ProgramFiles} "dotnet\dotnet.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "dotnet\dotnet.exe"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe")
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

    if ($fallback) {
        return $fallback
    }

    throw "dotnet not found. Use -Dotnet or install .NET SDK."
}

$dotnetExe = Resolve-Dotnet -Candidate $Dotnet

function Invoke-Build {
    & $dotnetExe build $project -c $Config --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $dotnetExe build $testProject -c $Config --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Invoke-Clean {
    & $dotnetExe clean $project -c $Config --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $dotnetExe clean $testProject -c $Config --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Invoke-Test {
    Invoke-Build
    & $dotnetExe test $testProject -c $Config --no-build --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Invoke-TestDebug {
    & $dotnetExe build $project -c Debug --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $dotnetExe build $testProject -c Debug --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $dotnetExe test $testProject -c Debug --no-build --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

switch ($Target) {
    "all" { Invoke-Build }
    "build" { Invoke-Build }
    "clean" { Invoke-Clean }
    "rebuild" {
        Invoke-Clean
        Invoke-Build
    }
    "test" { Invoke-Test }
    "testdebug" { Invoke-TestDebug }
}
