[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string] $Version = '0.1.0.2',
    [switch] $StageOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo "artifacts/release/$Version"
$package = Join-Path $output 'package'
$dlls = @('Nina.FieldKit.Plugin.dll', 'Nina.FieldKit.Core.dll')

if ($StageOnly) {
    if (Test-Path -LiteralPath $package) { throw 'Release staging already exists. Use a fresh checkout for each release.' }
    New-Item -ItemType Directory -Path $package -Force | Out-Null
    foreach ($dll in $dlls) {
        $built = Join-Path $repo "src/Nina.FieldKit.Plugin/bin/Release/net8.0-windows/$dll"
        $actual = [Reflection.AssemblyName]::GetAssemblyName($built).Version.ToString()
        if ($actual -ne $Version) { throw "$dll version $actual does not match release $Version" }
        Copy-Item -LiteralPath $built -Destination $package
    }
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'), (Join-Path $repo 'README.md') -Destination $package
    return
}

# Sign the staged copies first. Never publish an unsigned fallback.
foreach ($dll in $dlls) {
    $path = Join-Path $package $dll
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*StackFoundry LLC*' -or
        $null -eq $signature.TimeStamperCertificate) {
        throw "$dll must have a valid timestamped StackFoundry LLC signature. Status: $($signature.Status)"
    }
    if ([Reflection.AssemblyName]::GetAssemblyName($path).Version.ToString() -ne $Version) { throw "Wrong version in $dll" }
}
$expectedFiles = @($dlls) + @('LICENSE', 'README.md')
$actualFiles = @(Get-ChildItem -LiteralPath $package -Recurse -File)
if ($actualFiles.Count -ne $expectedFiles.Count -or @($actualFiles | Where-Object { $_.Name -notin $expectedFiles -or $_.DirectoryName -ne $package }).Count) {
    throw 'Release package contains unexpected files.'
}
$archiveName = "Nina.FieldKit.$Version.zip"
$archive = Join-Path $output $archiveName
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive
$checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
$parts = $Version.Split('.')
$repository = 'https://github.com/theatrus/nina-field-kit'
$manifest = [ordered]@{
    Name = 'NINA Field Kit'
    Identifier = 'd2487d32-9277-45ec-b5df-89ecbff61c78'
    Version = [ordered]@{ Major = $parts[0]; Minor = $parts[1]; Patch = $parts[2]; Build = $parts[3] }
    Author = 'Yann Ramin'
    Homepage = $repository
    Repository = $repository
    License = 'Apache-2.0'
    LicenseURL = "$repository/blob/v$Version/LICENSE"
    ChangelogURL = "$repository/releases/tag/v$Version"
    Tags = @('safety', 'alpaca', 'monitoring', 'sequencer')
    MinimumApplicationVersion = [ordered]@{ Major = '3'; Minor = '2'; Patch = '0'; Build = '9001' }
    Descriptions = [ordered]@{
        ShortDescription = 'Alpaca safety monitoring and observing-session equipment checks.'
        LongDescription = 'Combines required Alpaca safety sources with background checks, bounded tolerance for missed checks, configurable confirmation thresholds, live setup, and NINA diagnostics. Also includes read-only mount snapshot and health-check sequencer actions. See the usage guide for setup and safety behavior.'
        FeaturedImageURL = "https://raw.githubusercontent.com/theatrus/nina-field-kit/v$Version/src/Nina.FieldKit.Plugin/Assets/field-kit.png"
        ScreenshotURL = ''
        AltScreenshotURL = ''
    }
    Installer = [ordered]@{
        URL = "$repository/releases/download/v$Version/$archiveName"
        Type = 'ARCHIVE'
        Checksum = $checksum
        ChecksumType = 'SHA256'
    }
}
[IO.File]::WriteAllText((Join-Path $output "Nina.FieldKit.$Version.manifest.json"), ($manifest | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$checksum  $archiveName`n", [Text.UTF8Encoding]::new($false))
Write-Output "Signed release packaged: $archive"
