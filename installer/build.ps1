# Builds the Kinora packages, every one with the .NET runtime inside (nothing to install first).
#   .\build.ps1                       -> Windows, Linux and macOS
#   .\build.ps1 -Platform Windows     -> only one of Windows, Linux, MacOS
#   .\build.ps1 -SkipTests            -> skip the unit tests
#   .\build.ps1 -NoCopy               -> don't copy them to Z:\Dropbox\Software\Kinora
# Output in installer\Output:
#   Kinora-Setup-<version>.exe              Windows x64 installer (Inno Setup)
#   Kinora-<version>-linux-x64.tar.gz       Linux x64: unpack, run install.sh
#   Kinora-<version>-macos-arm64.zip        macOS on Apple silicon: Kinora.app
#   Kinora-<version>-macos-x64.zip          macOS on Intel: Kinora.app

param(
    [ValidateSet('Windows', 'Linux', 'MacOS', 'All')]
    [string]$Platform = 'All',
    [switch]$SkipTests,
    [switch]$NoCopy
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Kinora\Kinora.csproj'
$packager = Join-Path $root 'tools\Packager\Packager.csproj'
$assets = Join-Path $root 'src\Kinora\Assets'
$output = Join-Path $PSScriptRoot 'Output'
$software = 'Z:\Dropbox\Software\Kinora'

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Version not found in Directory.Build.props.' }

if (-not $SkipTests) {
    Write-Host '== Tests' -ForegroundColor Cyan
    dotnet test --project (Join-Path $root 'tests\Kinora.Tests') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

function Publish([string]$rid) {
    $out = Join-Path $root "publish\$rid"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }   # no leftovers from previous builds
    Write-Host "== Publish $rid $version" -ForegroundColor Cyan
    dotnet publish $project -c Release -r $rid --self-contained true -p:DebugType=none -o $out -nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($rid) failed." }

    # Kinora's license and the notices of everything it ships, the .NET runtime's included
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $out 'LICENSE.txt')
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $out
    $deps = Get-Content (Join-Path $out 'Kinora.deps.json') -Raw
    if ($deps -notmatch "runtimepack\.Microsoft\.NETCore\.App\.Runtime\.$([regex]::Escape($rid))/([\d.]+)") { throw "Runtime pack version not found ($rid)." }
    $packs = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $pack = Join-Path $packs "microsoft.netcore.app.runtime.$rid\$($Matches[1])"
    Copy-Item (Join-Path $pack 'LICENSE.TXT') (Join-Path $out 'dotnet-LICENSE.txt')
    Copy-Item (Join-Path $pack 'THIRD-PARTY-NOTICES.TXT') (Join-Path $out 'dotnet-THIRD-PARTY-NOTICES.txt')
    return $out
}

New-Item -ItemType Directory -Force $output | Out-Null
$made = @()

if ($Platform -in 'Windows', 'All') {
    $iscc = @("$env:ProgramFiles\Inno Setup 7\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'ISCC.exe (Inno Setup) not found.' }
    Publish 'win-x64' | Out-Null
    Write-Host '== Windows installer' -ForegroundColor Cyan
    & $iscc /Q "/DAppVersion=$version" (Join-Path $PSScriptRoot 'Kinora.iss')
    if ($LASTEXITCODE -ne 0) { throw 'ISCC failed.' }
    $made += @{ File = "Kinora-Setup-$version.exe"; Stable = 'Kinora-Setup.exe' }
}

if ($Platform -in 'Linux', 'All') {
    $dir = Publish 'linux-x64'
    Write-Host '== Linux archive' -ForegroundColor Cyan
    $name = "Kinora-$version-linux-x64.tar.gz"
    dotnet run --project $packager -- linux $dir (Join-Path $output $name) $version (Join-Path $assets 'kinora-256.png')
    if ($LASTEXITCODE -ne 0) { throw 'Packager (linux) failed.' }
    $made += @{ File = $name; Stable = 'Kinora-linux-x64.tar.gz' }
}

if ($Platform -in 'MacOS', 'All') {
    foreach ($arch in 'arm64', 'x64') {
        $dir = Publish "osx-$arch"
        Write-Host "== macOS app ($arch)" -ForegroundColor Cyan
        $name = "Kinora-$version-macos-$arch.zip"
        dotnet run --project $packager -- macos $dir (Join-Path $output $name) $version (Join-Path $assets 'kinora.icns')
        if ($LASTEXITCODE -ne 0) { throw "Packager (macos $arch) failed." }
        $made += @{ File = $name; Stable = "Kinora-macos-$arch.zip" }
    }
}

if (-not $NoCopy -and (Test-Path (Split-Path $software -Parent))) {
    Write-Host "== Copy to $software" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force $software | Out-Null
    foreach ($m in $made) {
        Copy-Item (Join-Path $output $m.File) (Join-Path $software $m.Stable) -Force
    }
}

Get-ChildItem $output | Where-Object { $_.Name -match [regex]::Escape($version) } |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
