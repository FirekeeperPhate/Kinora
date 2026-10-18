# Publishes a new Kinora version on GitHub: the packages, the v<version> tag and the release.
# Before: set <Version> in Directory.Build.props, commit, and write the notes in a Markdown file.
#   .\release.ps1 -Notes notes.md
# Installed copies on Windows find it within a day and update
# themselves; the asset names are the ones UpdateService looks for, so keep build.ps1's names.

param(
    [Parameter(Mandatory)] [string]$Notes,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$gh = @("$env:ProgramFiles\GitHub CLI\gh.exe") + (Get-Command gh -ErrorAction SilentlyContinue | ForEach-Object Source) |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $gh) { throw 'GitHub CLI (gh) not found.' }
if (-not (Test-Path $Notes)) { throw "Notes file not found: $Notes" }

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
$tag = "v$version"

Push-Location $root
try {
    if (git status --porcelain) { throw 'Commit everything first: the tag must point at what was built.' }
    if (git tag --list $tag) { throw "$tag already exists: raise <Version> in Directory.Build.props." }

    & (Join-Path $PSScriptRoot 'build.ps1') -SkipTests:$SkipTests
    $out = Join-Path $PSScriptRoot 'Output'
    $assets = @("Kinora-Setup-$version.exe", "Kinora-$version-linux-x64.tar.gz", "Kinora-$version-macos-arm64.zip", "Kinora-$version-macos-x64.zip") |
        ForEach-Object { Join-Path $out $_ }
    foreach ($a in $assets) { if (-not (Test-Path $a)) { throw "Missing package: $a" } }

    $credential = "credential.helper=!'$($gh -replace '\\', '/')' auth git-credential"
    git -c credential.helper= -c $credential push
    git tag -a $tag -m "Kinora $version"
    git -c credential.helper= -c $credential push origin $tag
    & $gh release create $tag --repo FirekeeperPhate/Kinora --title "Kinora $version" --notes-file $Notes --verify-tag @assets
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed.' }
}
finally {
    Pop-Location
}
