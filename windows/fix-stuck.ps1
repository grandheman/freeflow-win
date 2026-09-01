<#
.SYNOPSIS
    Diagnoses a FreeFlow that records but never pastes, and clears the usual cause.

.DESCRIPTION
    Symptom this is for: the recording capsule appears and the level bars move, but
    no text is ever pasted, and killing and restarting the app does not help.

    "Survives a restart" points at persisted state. The usual culprit is a
    rate-limit cooldown written to settings.json. FreeFlow persists a cooldown that
    looks like a daily quota so it survives restarts, and while it is active the
    cleanup stage refuses to send a request at all. Recording still works, because
    transcription is a different endpoint that does not consult the cooldown.

    A burst of rapid dictations, for example from fumbling the shortcut key several
    times in a row, is enough to trigger it.

.PARAMETER WhatIf
    Report findings without changing anything.

.EXAMPLE
    .\fix-stuck.ps1
    .\fix-stuck.ps1 -WhatIf
#>

[CmdletBinding()]
param(
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

# Captured as a plain switch rather than via SupportsShouldProcess, because
# $WhatIfPreference propagates into every cmdlet the script calls and makes
# module imports announce themselves. Only the settings write is conditional.
$applyChanges = -not $WhatIf

$dataDir = Join-Path $env:APPDATA 'FreeFlow'
$settingsPath = Join-Path $dataDir 'settings.json'
$logPath = Join-Path $dataDir 'diagnostic.log'

if (-not (Test-Path $dataDir)) {
    Write-Warning "No FreeFlow data at $dataDir. Has it ever run on this machine?"
    return
}

# 1. Is it even running?
Write-Host "`n=== is FreeFlow running? ===" -ForegroundColor Cyan
$running = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*FreeFlow*' }
if ($running) {
    $running | ForEach-Object { Write-Host "  running, pid $($_.ProcessId)" }
    Write-Host "  quit it from the tray before applying fixes" -ForegroundColor Yellow
} else {
    Write-Host "  not running"
}

# 2. What did the last dictation actually do?
Write-Host "`n=== last pipeline activity ===" -ForegroundColor Cyan
if (Test-Path $logPath) {
    Get-Content $logPath -Tail 14 | ForEach-Object { Write-Host "  $_" }
    Write-Host "`n  A healthy run ends with paste.done. The missing line names the broken stage."
} else {
    Write-Host "  no diagnostic log yet"
}

# 3. Can this machine even reach the provider?
Write-Host "`n=== provider reachability ===" -ForegroundColor Cyan

$vpnUp = @(Get-NetAdapter -ErrorAction SilentlyContinue |
    Where-Object { $_.Status -eq 'Up' -and
        ($_.InterfaceDescription -like '*WireGuard*' -or
         $_.InterfaceDescription -like '*TAP*' -or
         $_.Name -like '*VPN*' -or $_.Name -like '*Surfshark*' -or
         $_.Name -like '*NordLynx*' -or $_.Name -like '*Proton*') })

if ($vpnUp) {
    $vpnUp | ForEach-Object { Write-Host "  VPN adapter up: $($_.Name)" -ForegroundColor Yellow }
}

try {
    Add-Type -AssemblyName System.Net.Http
    $client = New-Object System.Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromSeconds(15)
    $response = $client.GetAsync('https://api.groq.com/openai/v1/models').Result
    $code = [int]$response.StatusCode
    $body = $response.Content.ReadAsStringAsync().Result

    if ($code -eq 401) {
        Write-Host "  HTTP 401 - reachable. The network is fine." -ForegroundColor Green
    }
    elseif ($code -eq 403 -and $body -match 'network settings') {
        Write-Host "  HTTP 403 - the provider is blocking this network" -ForegroundColor Red
        Write-Host ""
        Write-Host "  Groq blocks VPN exit IPs. This is not your API key: the same 403" -ForegroundColor Red
        Write-Host "  comes back with no key at all." -ForegroundColor Red
        Write-Host ""
        Write-Host "  Fix it one of these ways:"
        Write-Host "    - Surfshark Bypasser, adding C:\Program Files\dotnet\dotnet.exe"
        Write-Host "      (NOT FreeFlow.exe -- the app runs through the .NET host, so a"
        Write-Host "      rule naming FreeFlow.exe matches nothing)"
        Write-Host "    - or disconnect the VPN while dictating"
        Write-Host ""
        Write-Host "  Restart FreeFlow after any VPN change. It holds pooled connections"
        Write-Host "  and keeps failing on stale ones until it is restarted." -ForegroundColor Yellow
        Write-Host ""
        Write-Host "  Note this tested PowerShell's route, not FreeFlow's. With an"
        Write-Host "  app-based bypass this can read 403 while FreeFlow itself works."
    }
    else {
        Write-Host "  HTTP $code"
        if ($body) { Write-Host "  $($body.Substring(0, [Math]::Min(200, $body.Length)))" }
    }
}
catch {
    Write-Host "  could not reach the provider: $($_.Exception.Message)" -ForegroundColor Red
}

# 4. Persisted rate-limit cooldowns.
Write-Host "`n=== persisted rate-limit cooldowns ===" -ForegroundColor Cyan

if (-not (Test-Path $settingsPath)) {
    Write-Host "  no settings file"
    return
}

$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
$cooldownKeys = @($settings.PSObject.Properties.Name | Where-Object { $_ -like 'llm_cooldown_expiry_*' })

if ($cooldownKeys.Count -eq 0) {
    Write-Host "  none found, so cooldowns are not what is blocking you" -ForegroundColor Green
    Write-Host "`n  Next places to look:"
    Write-Host "    - VPN. Groq blocks VPN exit IPs. Reconnecting or switching servers can"
    Write-Host "      break it, and FreeFlow must be restarted after any VPN change because"
    Write-Host "      it holds pooled connections."
    Write-Host "    - The log above, whose last stage is the one that failed."
    return
}

$now = Get-Date
$blocking = $false

foreach ($key in $cooldownKeys) {
    $model = $key.Replace('llm_cooldown_expiry_', '')
    $expiry = [DateTimeOffset]::FromUnixTimeSeconds([long]$settings.$key).LocalDateTime

    if ($expiry -gt $now) {
        $blocking = $true
        $remaining = [int]($expiry - $now).TotalMinutes
        Write-Host "  $model" -NoNewline
        Write-Host "  blocked until $expiry ($remaining min left)" -ForegroundColor Red
    } else {
        Write-Host "  $model  expired $expiry, harmless"
    }
}

if (-not $blocking) {
    Write-Host "`n  All expired, so these are not blocking you." -ForegroundColor Green
    return
}

if (-not $applyChanges) {
    Write-Host "`n  -WhatIf given, so nothing was changed." -ForegroundColor Yellow
    return
}

foreach ($key in $cooldownKeys) { $settings.PSObject.Properties.Remove($key) }

# Write via a temporary file so an interrupted write cannot corrupt settings.
$temporary = "$settingsPath.tmp"
$settings | ConvertTo-Json -Depth 20 | Set-Content $temporary -Encoding utf8
Move-Item $temporary $settingsPath -Force

Write-Host "`n  Cleared. Restart FreeFlow." -ForegroundColor Green
Write-Host "  If the daily quota really is spent, the next request will earn a fresh"
Write-Host "  cooldown. The expiry times above tell you when you are genuinely clear."
