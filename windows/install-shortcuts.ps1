<#
.SYNOPSIS
    Creates Start Menu and (optionally) startup shortcuts for a local FreeFlow build.

.DESCRIPTION
    For running your own build on your own machine. The shortcuts point at
    run-freeflow.vbs, which launches FreeFlow through the Microsoft-signed .NET
    host so it works with Smart App Control enabled and no code-signing
    certificate.

    Nothing here needs administrator rights: everything is written under the
    current user's profile.

.PARAMETER AtLogin
    Also add FreeFlow to this user's startup folder.

.PARAMETER Remove
    Remove the shortcuts this script created.

.EXAMPLE
    .\install-shortcuts.ps1
    .\install-shortcuts.ps1 -AtLogin
    .\install-shortcuts.ps1 -Remove
#>

[CmdletBinding()]
param(
    [switch]$AtLogin,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

$launcher = Join-Path $PSScriptRoot 'run-freeflow.vbs'
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'FreeFlow.lnk'
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'FreeFlow.lnk'

if ($Remove) {
    foreach ($path in @($startMenu, $startup)) {
        if (Test-Path $path) {
            Remove-Item $path -Force
            Write-Host "Removed $path"
        }
    }
    Write-Host "`nShortcuts removed. Your build is untouched." -ForegroundColor Green
    return
}

if (-not (Test-Path $launcher)) {
    throw "run-freeflow.vbs was not found next to this script."
}

# Warn early rather than creating a shortcut to something that will not start.
$release = Join-Path $PSScriptRoot 'src\FreeFlow.App\bin\Release\net8.0-windows10.0.19041.0\FreeFlow.dll'
$debug = Join-Path $PSScriptRoot 'src\FreeFlow.App\bin\Debug\net8.0-windows10.0.19041.0\FreeFlow.dll'

if (-not (Test-Path $release) -and -not (Test-Path $debug)) {
    Write-Warning "No build found yet. Run this first:"
    Write-Warning "  dotnet build FreeFlow.sln --configuration Release"
    Write-Host ""
}

function New-FreeFlowShortcut {
    param([string]$Path, [string]$Description)

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)

    # wscript.exe runs the launcher with no console window.
    $shortcut.TargetPath = Join-Path $env:SystemRoot 'System32\wscript.exe'
    $shortcut.Arguments = "`"$launcher`""
    $shortcut.WorkingDirectory = $PSScriptRoot
    $shortcut.Description = $Description
    $shortcut.WindowStyle = 7   # minimized; the app lives in the notification area
    $shortcut.Save()

    Write-Host "Created $Path"
}

New-FreeFlowShortcut -Path $startMenu -Description 'FreeFlow dictation'

if ($AtLogin) {
    New-FreeFlowShortcut -Path $startup -Description 'FreeFlow dictation (starts at sign-in)'
    Write-Host "`nFreeFlow will start when you sign in." -ForegroundColor Green
    Write-Host "Leave 'Start FreeFlow when I sign in' OFF in Settings so it does not register twice."
}

Write-Host "`nDone. Search the Start Menu for FreeFlow." -ForegroundColor Green
Write-Host "It runs in the notification area; look for the microphone icon."
