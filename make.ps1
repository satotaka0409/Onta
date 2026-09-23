# Onta C# build helper for Windows PowerShell
# Usage:
#   .\make.ps1
#   .\make.ps1 build
#   .\make.ps1 clean
#   .\make.ps1 test              # all tests
#   .\make.ps1 core              # test/core
#   .\make.ps1 history           # test/history
#   .\make.ps1 performance       # test/performance
#   .\make.ps1 testdebug
#   .\make.ps1 rebuild -Config Debug
#   .\make.ps1 build -Dotnet "C:\Program Files\dotnet\dotnet.exe"
#   .\make.cmd history

param(
    [Parameter(Position = 0)]
    [ValidateSet("all", "build", "clean", "rebuild", "test", "testdebug", "core", "history", "performance")]
    [string]$Target = "build",

    [string]$Config = "Release",

    [string]$Dotnet = "dotnet"
)

$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot

$project = Join-Path $PSScriptRoot "Onta_core\Onta_core.csproj"
$testProjects = @(
    Get-ChildItem -Path (Join-Path $PSScriptRoot "Onta_core") -Recurse -Filter "*.Tests.csproj" -File |
        ForEach-Object { $_.FullName }
)

if ($testProjects.Count -eq 0) {
    throw "No test projects (*.Tests.csproj) were found under Onta_core."
}

# Folder -> namespace filter for: dotnet test --filter
$TestFilters = @{
    core        = "FullyQualifiedName~Onta.Core.Tests.Core."
    history     = "FullyQualifiedName~Onta.Core.Tests.History."
    performance = "FullyQualifiedName~Onta.Core.Tests.Performance."
}

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

    foreach ($tp in $testProjects) {
        & $dotnetExe build $tp -c $Config --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}

function Invoke-Clean {
    & $dotnetExe clean $project -c $Config --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    foreach ($tp in $testProjects) {
        & $dotnetExe clean $tp -c $Config --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}

function Invoke-Test {
    param(
        [string]$Filter = ""
    )

    Invoke-Build
    foreach ($tp in $testProjects) {
        if ([string]::IsNullOrWhiteSpace($Filter)) {
            Write-Host "=== test (all): $tp ==="
            & $dotnetExe test $tp -c $Config --no-build --nologo
        }
        else {
            Write-Host "=== test ($Filter): $tp ==="
            & $dotnetExe test $tp -c $Config --no-build --nologo --filter $Filter
        }
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}

function Invoke-TestDebug {
    & $dotnetExe build $project -c Debug --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    foreach ($tp in $testProjects) {
        & $dotnetExe build $tp -c Debug --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    foreach ($tp in $testProjects) {
        & $dotnetExe test $tp -c Debug --no-build --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
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
    "core" { Invoke-Test -Filter $TestFilters.core }
    "history" { Invoke-Test -Filter $TestFilters.history }
    "performance" { Invoke-Test -Filter $TestFilters.performance }
}
