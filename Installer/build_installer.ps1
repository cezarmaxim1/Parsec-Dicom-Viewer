# build_installer.ps1 - Automated Build for Parsec v1.2 Beta Installer
$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Parsec Medical DICOM Viewer - v1.2 Beta Setup Packager  " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$rootDir = "F:\Rad"
$installerDir = Join-Path $rootDir "Installer"
$payloadDir = Join-Path $installerDir "Payload"
$outputDir = Join-Path $installerDir "Output"
$issScript = Join-Path $installerDir "parsec.iss"

# Clean any previous Publish_Test folder from root
$oldPublishTest = Join-Path $rootDir "Publish_Test"
if (Test-Path $oldPublishTest) { Remove-Item $oldPublishTest -Recurse -Force -ErrorAction SilentlyContinue }

# 1. Clean previous directories
Write-Host "`n[1/5] Cleaning previous build artifacts..." -ForegroundColor Yellow
if (Test-Path $payloadDir) { Remove-Item $payloadDir -Recurse -Force }
if (Test-Path $outputDir) { Remove-Item $outputDir -Recurse -Force }
New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

# 2. Build local Debug binary (used by Desktop shortcuts) & Publish Parsec Main Application (self-contained win-x64)
Write-Host "`n[2/5] Building local Debug binary & Publishing Release application (win-x64, self-contained with full .NET 8 runtime)..." -ForegroundColor Yellow
$parsecCsproj = Join-Path $rootDir "OpenRadViewer\OpenRadViewer.csproj"
Stop-Process -Name "Parsec" -Force -ErrorAction SilentlyContinue
dotnet build $parsecCsproj -c Debug
dotnet publish $parsecCsproj -c Release -r win-x64 --self-contained true -o $payloadDir

# Remove unnecessary pdb and createdump to optimize package size
Get-ChildItem -Path $payloadDir -Filter "*.pdb" | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem -Path $payloadDir -Filter "createdump.exe" | Remove-Item -Force -ErrorAction SilentlyContinue

# 3. Locate Inno Setup Compiler (ISCC.exe)
Write-Host "`n[3/5] Locating Inno Setup compiler..." -ForegroundColor Yellow
$isccCandidates = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
)

$isccPath = $null
foreach ($cand in $isccCandidates) {
    if ($cand -and (Test-Path $cand)) {
        $isccPath = $cand
        break
    }
}

if (-not $isccPath) {
    throw "Inno Setup compiler (ISCC.exe) was not found."
}
Write-Host "  -> Found ISCC at: $isccPath" -ForegroundColor Green

# 4. Compile Installer with Inno Setup (LZMA2 Ultra Compression)
Write-Host "`n[4/5] Compiling native Parsec_Setup_v1.2_Beta.exe with LZMA2 ultra compression..." -ForegroundColor Yellow
& $isccPath $issScript

$setupExe = Join-Path $outputDir "Parsec_Setup_v1.2_Beta.exe"
if (-not (Test-Path $setupExe)) {
    throw "Setup build failed: Parsec_Setup_v1.2_Beta.exe not found in $outputDir"
}

# 5. Copy final setup executable to root folder F:\Rad
Write-Host "`n[5/5] Finalizing distribution packages..." -ForegroundColor Yellow
$finalSetupExe = Join-Path $rootDir "Parsec_Setup_v1.2_Beta.exe"
Copy-Item $setupExe -Destination $finalSetupExe -Force

# Clean intermediate temporary build payload directories
Write-Host "  -> Cleaning temporary build staging directories..." -ForegroundColor Gray
Remove-Item $payloadDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $outputDir -Recurse -Force -ErrorAction SilentlyContinue

$setupSizeMb = (Get-Item $finalSetupExe).Length / 1MB
Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host "  BUILD COMPLETE!" -ForegroundColor Green
Write-Host ("  Setup Installer:  {0} ({1:N2} MB)" -f $finalSetupExe, $setupSizeMb) -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
