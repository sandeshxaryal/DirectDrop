#Requires -Version 5.1
<#
.SYNOPSIS
    Builds DirectDrop: runs tests, publishes a self-contained DirectDrop.exe,
    and (if Inno Setup is installed) compiles DirectDrop-Setup.exe.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -SkipTests
    .\build.ps1 -SkipInstaller
#>
param(
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$publishDir = Join-Path $root "publish"
$appProject = Join-Path $root "src\DirectDrop.App\DirectDrop.App.csproj"
$testProject = Join-Path $root "src\DirectDrop.Tests\DirectDrop.Tests.csproj"
$smokeTestProject = Join-Path $root "tools\DirectDrop.SmokeTest\DirectDrop.SmokeTest.csproj"
$innoScript = Join-Path $root "installer\DirectDrop.iss"

function Write-Step($message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

# ---------------------------------------------------------------------------
# 1. Smoke test - no NuGet required, always runs, fails the build if it fails
# ---------------------------------------------------------------------------
Write-Step "Running the offline smoke test (DirectDrop.Core + DirectDrop.Server)"
dotnet run --project $smokeTestProject -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Smoke test failed - see output above. Not proceeding with a build that failed its own transfer-engine checks."
}

# ---------------------------------------------------------------------------
# 2. Full test suite (requires NuGet access for xUnit packages)
# ---------------------------------------------------------------------------
if (-not $SkipTests) {
    Write-Step "Running the full test suite (dotnet test)"
    dotnet test $testProject -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed. Fix them, or re-run with -SkipTests to publish anyway (not recommended)."
    }
} else {
    Write-Host "Skipping dotnet test (-SkipTests specified)." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# 3. Publish a self-contained, single-file DirectDrop.exe
# ---------------------------------------------------------------------------
Write-Step "Publishing self-contained DirectDrop.exe (win-x64)"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $appProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Write-Host "Published to: $publishDir\DirectDrop.exe" -ForegroundColor Green

# ---------------------------------------------------------------------------
# 4. Installer (optional - needs Inno Setup: https://jrsoftware.org/isinfo.php)
# ---------------------------------------------------------------------------
if (-not $SkipInstaller) {
    $iscc = Get-Command "iscc.exe" -ErrorAction SilentlyContinue
    if (-not $iscc) {
        $defaultPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
        if (Test-Path $defaultPath) { $iscc = Get-Item $defaultPath }
    }

    if ($iscc) {
        Write-Step "Compiling installer with Inno Setup"
        & $iscc.Path $innoScript
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }
        Write-Host "Installer created in: installer\Output\DirectDrop-Setup.exe" -ForegroundColor Green
    } else {
        Write-Host ""
        Write-Host "Inno Setup (iscc.exe) was not found - skipping installer creation." -ForegroundColor Yellow
        Write-Host "Install it from https://jrsoftware.org/isinfo.php and re-run this script," -ForegroundColor Yellow
        Write-Host "or run 'iscc installer\DirectDrop.iss' yourself once it's installed." -ForegroundColor Yellow
    }
} else {
    Write-Host "Skipping installer build (-SkipInstaller specified)." -ForegroundColor Yellow
}

Write-Step "Done"
Write-Host "DirectDrop.exe: $publishDir\DirectDrop.exe"
