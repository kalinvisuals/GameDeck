# Builds the dashboard and the Windows app, then runs the tests.
#   ./build.ps1            debug build + tests
#   ./build.ps1 -Publish   also a self-contained win-x64 build in ./publish
#   ./build.ps1 -Installer also ./dist/GameDeck-Setup-<version>.exe (needs Inno Setup 7; implies -Publish)
param([switch]$Publish, [switch]$Installer, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
# npm and Vite write progress to stderr, which Windows PowerShell 5.1 would treat as a failure; run them
# through cmd and judge them by their exit code only.
function Invoke-Npm([string]$arguments) { cmd /c "npm $arguments 2>&1" | Out-Host; return $LASTEXITCODE }
$root = $PSScriptRoot
if ($Installer) { $Publish = $true }

Push-Location "$root/web"
try {
    if (-not (Test-Path node_modules)) { if (Invoke-Npm 'install --no-audit --no-fund') { throw 'npm install failed' } }
    if (Invoke-Npm 'run build') { throw 'dashboard build failed' }
    if (-not $SkipTests) { if (Invoke-Npm 'test') { throw 'dashboard tests failed' } }
} finally { Pop-Location }

dotnet build "$root/GameDeck.sln" -nologo; if ($LASTEXITCODE) { throw 'dotnet build failed' }
if (-not $SkipTests) { dotnet test "$root/GameDeck.sln" -nologo --no-build; if ($LASTEXITCODE) { throw 'tests failed' } }

if ($Publish) {
    # Start empty so files from an older publish never end up in the installer.
    if (Test-Path "$root/publish") { Remove-Item "$root/publish" -Recurse -Force }
    dotnet publish "$root/src/GameDeck.App/GameDeck.App.csproj" -c Release -r win-x64 --self-contained true -o "$root/publish" -nologo
    if ($LASTEXITCODE) { throw 'publish failed' }
    Write-Host "Published to $root/publish/GameDeck.exe"
}

if ($Installer) {
    $iscc = @("${env:ProgramFiles}\Inno Setup 7\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
              "${env:LOCALAPPDATA}\Programs\Inno Setup 7\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 7 not found: winget install JRSoftware.InnoSetup' }
    $version = ([xml](Get-Content "$root/src/GameDeck.App/GameDeck.App.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    & $iscc /Qp "/DAppVersion=$version" "$root/installer/GameDeck.iss"
    if ($LASTEXITCODE) { throw 'installer build failed' }
    Write-Host "Installer: $root/dist/GameDeck-Setup-$version.exe"
}
