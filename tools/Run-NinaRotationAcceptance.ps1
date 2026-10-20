[CmdletBinding()]
param(
    [string] $NinaPath = "C:\Program Files\N.I.N.A. - Nighttime Imaging 'N' Astronomy\NINA.exe",
    [string] $OutputDirectory,
    [switch] $TwilightFilters,
    [ValidateRange(180,1800)] [int] $TimeoutSeconds = 600
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
if (!(Test-Path -LiteralPath $NinaPath)) { throw 'Install NINA 3.2.0.9001 or newer first.' }
if (Get-Process -Name NINA -ErrorAction SilentlyContinue) { throw 'Close NINA before running simulator acceptance.' }
if (!(Test-Path 'Registry::HKEY_CLASSES_ROOT\ASCOM.Simulator.Rotator')) { throw 'Install the ASCOM rotator simulator first.' }
if ($TwilightFilters -and !(Test-Path 'Registry::HKEY_CLASSES_ROOT\ASCOM.Simulator.FilterWheel')) { throw 'Install the ASCOM filter wheel simulator first.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo ('artifacts/nina-acceptance-' + (Get-Date -Format yyyyMMdd-HHmmss)) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh output directory.' }
$generator = Join-Path $PSScriptRoot 'Nina.FieldKit.NinaAcceptance/bin/Release/net8.0-windows/Nina.FieldKit.NinaAcceptance.dll'
if (!(Test-Path -LiteralPath $generator)) { throw 'Build Nina.FieldKit.sln in Release first.' }
if ($TwilightFilters) { dotnet $generator $OutputDirectory --twilight-filters }
else { dotnet $generator $OutputDirectory }
if ($LASTEXITCODE) { throw 'Generating simulator configuration failed.' }
$run = Get-Content (Join-Path $OutputDirectory 'run.json') -Raw | ConvertFrom-Json
$profilePath = Join-Path $env:LOCALAPPDATA "NINA/Profiles/$($run.Id).profile"
if (Test-Path -LiteralPath $profilePath) { throw 'Refusing to overwrite an existing NINA profile.' }
$pluginDirectory = Join-Path $env:LOCALAPPDATA 'NINA/Plugins/3.0.0/Nina.FieldKit'
$backup = Join-Path $OutputDirectory 'plugin-backup'
New-Item -ItemType Directory -Path $backup | Out-Null
$dlls = @('Nina.FieldKit.Plugin.dll','Nina.FieldKit.Core.dll')
foreach ($dll in $dlls) { Copy-Item -LiteralPath (Join-Path $pluginDirectory $dll) -Destination $backup }
$testProcess = $null
$logs = @()
try {
    foreach ($dll in $dlls) { Copy-Item -LiteralPath (Join-Path $repo "src/Nina.FieldKit.Plugin/bin/Release/net8.0-windows/$dll") -Destination $pluginDirectory }
    Copy-Item -LiteralPath $run.ProfilePath -Destination $profilePath
    foreach ($sequenceName in @('acceptance.json','acceptance-restart.json')) {
        $sequencePath = Join-Path $OutputDirectory $sequenceName
        Write-Host "Running installed NINA with simulators: $sequenceName"
        $processStartedUtc = [DateTime]::UtcNow
        $testProcess = Start-Process -FilePath $NinaPath -ArgumentList @('-p',$run.Id,'-s',('"'+$sequencePath+'"'),'-r','-x') -WindowStyle Hidden -PassThru
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while (!$testProcess.WaitForExit(1000)) {
            if ([DateTime]::UtcNow -ge $deadline) { throw "NINA simulator run timed out: $sequenceName" }
        }
        if ($testProcess.ExitCode -ne 0) { throw "NINA exited with code $($testProcess.ExitCode)." }
        $logFiles = @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'NINA/Logs') -Filter "*.$($testProcess.Id)-*.log" |
            Where-Object { $_.CreationTimeUtc -ge $processStartedUtc.AddSeconds(-1) })
        if ($logFiles.Count -ne 1) { throw 'Expected exactly one NINA log for the simulator process.' }
        $destination = Join-Path $OutputDirectory "$sequenceName.nina.log"
        Copy-Item -LiteralPath $logFiles[0].FullName -Destination $destination
        $logs += $destination
        $allEvents = @(Get-Content -LiteralPath $destination | ForEach-Object {
            if ($_ -match '\|FieldKit (.+)$') {
                $entry = $Matches[1] | ConvertFrom-Json
                $entry
            }
        })
        $events = @($allEvents | Where-Object action -eq 'SkyFlatRotations')
        $expectedLoops = if ($TwilightFilters) { 2 } else { 1 }
        if (@($events | Where-Object transition -eq 'Completed').Count -ne $expectedLoops -or
            @($events | Where-Object transition -in @('Failure','Cancelled','ProfileChanged','ConditionStopped')).Count -ne 0) {
            throw "Rotation loop did not complete successfully: $sequenceName"
        }
        $plan = @($events | Where-Object transition -eq 'Plan')
        if ($plan.Count -ne $expectedLoops -or @($plan | Where-Object { ($_.evidence.Angles -join ',') -ne '12,192,45' }).Count) { throw 'Unexpected captured rotation plan.' }
        if ($TwilightFilters) {
            $filterEvents = @($allEvents | Where-Object action -eq 'TwilightFlats')
            if (@($filterEvents | Where-Object transition -eq 'Completed').Count -ne 1 -or
                @($filterEvents | Where-Object transition -in @('Failure','Cancelled','ProfileChanged','ConditionStopped')).Count -or
                (@($filterEvents | Where-Object transition -eq 'FilterMoveStarted' | ForEach-Object { $_.evidence.Name }) -join ',') -ne 'R,L') {
                throw 'The native twilight filter loop did not complete in R,L order.'
            }
        }
        $recorded = @($events | Where-Object transition -eq 'RotationRecorded').Count
        if (($sequenceName -eq 'acceptance.json' -and $recorded -ne 4) -or
            ($sequenceName -eq 'acceptance-restart.json' -and $recorded -ne 0)) { throw 'Unexpected light-frame recording count.' }
    }
    $frames = @(Get-ChildItem -LiteralPath $run.Frames -Recurse -Filter '*.fits' | ForEach-Object {
        $stream = [IO.File]::OpenRead($_.FullName)
        try {
            $card = [byte[]]::new(80); $values = @{}
            while ($stream.Read($card,0,80) -eq 80) {
                $text = [Text.Encoding]::ASCII.GetString($card)
                $key = $text.Substring(0,8).Trim()
                if ($key -eq 'END') { break }
                if ($text[8] -eq '=') { $values[$key] = ($text.Substring(10) -split ' / ')[0].Trim().Trim("'") }
            }
            [pscustomobject]@{ File=$_.Name; Type=$values['IMAGETYP']; Angle=[double]::Parse($values['ROTATOR'],[Globalization.CultureInfo]::InvariantCulture); Camera=$values['INSTRUME']; Filter=$values['FILTER']; Started=$values['DATE-OBS'] }
        } finally { $stream.Dispose() }
    })
    $lights = @($frames | Where-Object Type -eq LIGHT)
    $flats = @($frames | Where-Object Type -eq FLAT)
    if ($lights.Count -ne 4 -or $flats.Count -ne 12) { throw "Expected 4 LIGHT and 12 FLAT FITS frames; got $($lights.Count) and $($flats.Count)." }
    foreach ($angle in @(12,192,45)) {
        if (@($flats | Where-Object { [Math]::Abs($_.Angle-$angle) -le 0.1 }).Count -ne 4) { throw "Expected four flats at $angle degrees across both runs." }
    }
    if (@($frames | Where-Object Camera -ne 'N.I.N.A. Simulator Camera').Count) { throw 'Unexpected camera in FITS metadata.' }
    if ($TwilightFilters) {
        $ordered = @($flats | Sort-Object Started)
        $expected = @('R:12','R:192','R:45','L:12','L:192','L:45')
        for ($i=0; $i -lt $ordered.Count; $i++) {
            if ("$($ordered[$i].Filter):$($ordered[$i].Angle)" -ne $expected[$i % 6]) { throw 'FITS filter/angle order does not match the filter-first plan.' }
        }
    }
    [ordered]@{ Passed=$true; NinaVersion=(Get-Item -LiteralPath $NinaPath).VersionInfo.FileVersion; PluginVersion=[Reflection.AssemblyName]::GetAssemblyName((Join-Path $pluginDirectory $dlls[0])).Version.ToString(); Runs=2; Lights=$lights.Count; Flats=$flats.Count; Angles=@(12,192,45); RestartPersistence=$true; NativeTwilightFilters=[bool]$TwilightFilters; Frames=$frames; Logs=$logs } |
        ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory 'result.json')
    Write-Host "PASS: 4 lights, 12 flats, three mechanical angles, nested loops and restart persistence. Evidence: $OutputDirectory"
} finally {
    if ($null -ne $testProcess -and !$testProcess.HasExited) { Stop-Process -Id $testProcess.Id -Force; $testProcess.WaitForExit() }
    foreach ($dll in $dlls) { Copy-Item -LiteralPath (Join-Path $backup $dll) -Destination $pluginDirectory }
    if (Test-Path -LiteralPath $profilePath) {
        Copy-Item -LiteralPath $profilePath -Destination (Join-Path $OutputDirectory 'completed.profile')
        Remove-Item -LiteralPath $profilePath
    }
    foreach ($suffix in @('.bkp','.journal')) {
        if (Test-Path -LiteralPath ($profilePath+$suffix)) { Remove-Item -LiteralPath ($profilePath+$suffix) }
    }
}
