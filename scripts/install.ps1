#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the Retweak baseline enforcement service.

.DESCRIPTION
    Copies the published output to an install directory, registers the Event Log source,
    creates the service with automatic start, and configures SCM recovery so the service
    is restarted if it crashes or exits non-zero.

.PARAMETER SourcePath
    Directory containing the published Retweak.Service.exe (dotnet publish output).

.PARAMETER InstallPath
    Where to install. Defaults to %ProgramFiles%\Retweak.

.PARAMETER Account
    Service account. LocalSystem is the default and is required to write HKLM policy keys
    and other users' hives. Override only if you have arranged equivalent permissions.

.PARAMETER SafeMode
    Also register the service to start in Safe Mode (minimal and networked). Off by default.

.EXAMPLE
    .\install.ps1 -SourcePath ..\src\Retweak.Service\bin\Release\net10.0-windows\win-x64\publish
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SourcePath,

    [string] $InstallPath = (Join-Path $env:ProgramFiles 'Retweak'),

    [string] $ServiceName = 'RetweakService',

    [string] $DisplayName = 'Retweak Baseline Enforcement',

    [string] $EventSource = 'Retweak',

    [string] $Account = 'LocalSystem',

    [switch] $SafeMode
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# sc.exe wants "option= value" as literal text with a single space after '=', and its
# own argument parser is naive about quoting. Windows PowerShell 5.1's native-argument
# encoder (pwsh 7 does not have this bug) can re-quote an array element that already
# contains a quoted path-with-spaces, producing a doubly-quoted, invalid command line
# (sc create then fails with exit code 1639). Routing through a temp .cmd file sidesteps
# PowerShell's argument encoding entirely: cmd.exe parses the literal text itself, the
# same as if it had been typed at a prompt, with none of that re-quoting.
function Invoke-Sc([string] $ArgumentText) {
    $tempBat = Join-Path ([System.IO.Path]::GetTempPath()) "retweak-sc-$([Guid]::NewGuid()).cmd"
    try {
        Set-Content -LiteralPath $tempBat -Value "@echo off`r`nsc.exe $ArgumentText" -Encoding ASCII
        # Out-Null is load-bearing, not cosmetic: without it, cmd's stdout lines are
        # collected into this function's own pipeline output alongside the `return`
        # below, so a caller doing `$code = Invoke-Sc ...` gets a garbled array
        # instead of a clean exit code.
        & cmd.exe /c $tempBat | Out-Null
        return $LASTEXITCODE
    }
    finally {
        Remove-Item -LiteralPath $tempBat -Force -ErrorAction SilentlyContinue
    }
}

# --- validate input ---------------------------------------------------------

$SourcePath = (Resolve-Path -LiteralPath $SourcePath).Path
$exeSource = Join-Path $SourcePath 'Retweak.Service.exe'
if (-not (Test-Path -LiteralPath $exeSource)) {
    throw "Retweak.Service.exe not found in '$SourcePath'. Publish the project first."
}

$exePath = Join-Path $InstallPath 'Retweak.Service.exe'

# --- stop and remove any previous instance ----------------------------------

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Step "Existing service found; stopping and removing it first."

    # CanStop=false makes Stop-Service fail. That is the documented trade-off of
    # RefuseStop; the upgrade path in that case is to kill the process and delete
    # the service, which is what this does.
    try {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped', '00:00:30')
    }
    catch {
        Write-Warning "Service refused to stop ($($_.Exception.Message)); terminating the process instead."
        Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" |
            Where-Object { $_.ProcessId -gt 0 } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Seconds 3
    }

    Invoke-Sc "delete ""$ServiceName""" | Out-Null

    # SCM keeps the entry until every handle closes; wait for it to disappear.
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
}

# --- copy files -------------------------------------------------------------

Write-Step "Installing to $InstallPath"
New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
Copy-Item -Path (Join-Path $SourcePath '*') -Destination $InstallPath -Recurse -Force

$logDir = Join-Path $env:ProgramData 'Retweak\logs'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

# --- event log source -------------------------------------------------------
# Created here rather than at runtime: source creation requires administrator rights the
# service account may not want, and it can require a reboot to take effect on some builds.

Write-Step "Registering Event Log source '$EventSource'"
if (-not [System.Diagnostics.EventLog]::SourceExists($EventSource)) {
    New-EventLog -LogName Application -Source $EventSource
    Write-Host "    Source created. If events do not appear immediately, a reboot may be needed."
}
else {
    Write-Host "    Source already exists."
}

# --- create service ---------------------------------------------------------

Write-Step "Creating service '$ServiceName'"
$binPath = '"{0}"' -f $exePath

$createExitCode = Invoke-Sc "create ""$ServiceName"" binPath= $binPath DisplayName= ""$DisplayName"" start= auto obj= ""$Account"""
if ($createExitCode -ne 0) {
    throw "sc create failed with exit code $createExitCode."
}

Invoke-Sc "description ""$ServiceName"" ""Keeps a configured registry baseline enforced, reacting to changes as they happen.""" | Out-Null

# --- recovery ---------------------------------------------------------------
# reset= 86400 means the failure count returns to zero after a day without incident.
# Three restart actions at 60 s; the third also applies to all subsequent failures.

Write-Step "Configuring recovery actions"
Invoke-Sc "failure ""$ServiceName"" reset= 86400 actions= restart/60000/restart/60000/restart/60000" | Out-Null

# Treat a non-zero exit code as a failure, not just a crash. Without this, a clean
# process exit with a bad status is not retried.
Invoke-Sc "failureflag ""$ServiceName"" 1" | Out-Null

# --- optional safe mode -----------------------------------------------------

if ($SafeMode) {
    Write-Step "Registering for Safe Mode start"
    foreach ($mode in 'Minimal', 'Network') {
        $key = "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\$mode\$ServiceName"
        New-Item -Path $key -Force | Out-Null
        Set-ItemProperty -Path $key -Name '(Default)' -Value 'Service'
    }
    Write-Warning "Safe Mode entries added. Safe Mode is where you go to fix a broken machine; a service that enforces settings there can get in the way of that. Remove them with uninstall.ps1 if you regret it."
}

# --- start ------------------------------------------------------------------

Write-Step "Starting service"
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Running', '00:00:30')

Write-Host ""
Write-Host "Retweak installed and running." -ForegroundColor Green
Write-Host "  Binary:    $exePath"
Write-Host "  Config:    $(Join-Path $InstallPath 'appsettings.json')"
Write-Host "  Overrides: HKLM\SOFTWARE\OptoCloud\Retweak\Config"
Write-Host "  Log file:  $(Join-Path $logDir 'retweak.log')"
Write-Host "  Event log: Application, source '$EventSource'"
