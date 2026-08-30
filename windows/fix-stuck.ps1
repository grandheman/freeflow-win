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

[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'

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

# 3. Persisted rate-limit cooldowns, the usual cause.
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

if (-not $PSCmdlet.ShouldProcess($settingsPath, 'Remove persisted rate-limit cooldowns')) {
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
