param(
    [Parameter(Mandatory)][string] $ReadyFile,
    [ValidateSet('status', 'events', 'activate', 'reset', 'pause', 'resume', 'stop')][string] $Action = 'status'
)
$ErrorActionPreference = 'Stop'
if ((Get-Item -LiteralPath $ReadyFile).Length -gt 65536) { throw 'Invalid readiness file' }
$ready = Get-Content -LiteralPath $ReadyFile -Raw | ConvertFrom-Json
$control = [Uri] $ready.controlUrl
if ($control.Scheme -ne 'http' -or $control.Host -ne '127.0.0.1' -or $control.UserInfo -ne '' -or $control.AbsolutePath -ne '/') { throw 'Control URL must be the local fault server' }
$method = if ($Action -in @('status','events')) { 'GET' } else { 'POST' }
Invoke-RestMethod -Uri ($control.AbsoluteUri.TrimEnd('/') + '/' + $Action) -Method $method -Headers @{ 'X-Control-Token' = $ready.controlToken }
