#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the Retweak baseline enforcement service.

.DESCRIPTION
    Stops (or terminates) the service, deletes it, removes the Safe Mode entries and the
    Event Log source, and optionally deletes the install directory and logs.

    Note that uninstalling does not revert the registry values Retweak enforced. Those
    were the point; if you want them back as they were, change them after the service is
    gone. Use -Report to list what is currently enforced before removing anything.
#>
[CmdletBinding()]
param(
    [string] $ServiceName = 'RetweakService',

    [string] $EventSource = 'Retweak',

    [string] $InstallPath = (Join-Path $env:ProgramFiles 'Retweak'),

    [switch] $RemoveFiles,

    [switch] $RemoveLogs,

    [switch] $Report
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

if ($Report) {
    $configPath = Join-Path $InstallPath 'appsettings.json'
    if (Test-Path -LiteralPath $configPath) {
        Write-Step "Currently enforced baseline"
        (Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).Retweak.Entries |
            Format-Table Scope, Path, Name, Kind, Value -AutoSize
    }
    else {
        Write-Warning "No config found at $configPath."
    }
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    Write-Step "Stopping $ServiceName"
    try {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        $service.WaitForStatus('Stopped', '00:00:30')
    }
    catch {
        Write-Warning "Service refused to stop ($($_.Exception.Message)); terminating the process."

        # Disable first so SCM recovery does not restart it the moment it dies.
        & sc.exe config $ServiceName start= disabled | Out-Null
        & sc.exe failure $ServiceName reset= 0 actions= '' | Out-Null

        Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" |
            Where-Object { $_.ProcessId -gt 0 } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Seconds 3
    }

    Write-Step "Deleting service"
    & sc.exe delete $ServiceName | Out-Null

    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
}
else {
    Write-Host "Service $ServiceName is not installed."
}

Write-Step "Removing Safe Mode entries"
foreach ($mode in 'Minimal', 'Network') {
    $key = "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\$mode\$ServiceName"
    if (Test-Path -LiteralPath $key) {
        Remove-Item -LiteralPath $key -Recurse -Force
        Write-Host "    Removed $key"
    }
}

Write-Step "Removing Event Log source"
if ([System.Diagnostics.EventLog]::SourceExists($EventSource)) {
    Remove-EventLog -Source $EventSource
    Write-Host "    Source '$EventSource' removed."
}

Write-Step "Removing registry overrides"
$overrideKey = 'HKLM:\SOFTWARE\OptoCloud\Retweak'
if (Test-Path -LiteralPath $overrideKey) {
    Remove-Item -LiteralPath $overrideKey -Recurse -Force
    Write-Host "    Removed $overrideKey"
}

if ($RemoveFiles -and (Test-Path -LiteralPath $InstallPath)) {
    Write-Step "Removing $InstallPath"
    Remove-Item -LiteralPath $InstallPath -Recurse -Force
}

if ($RemoveLogs) {
    $logDir = Join-Path $env:ProgramData 'Retweak'
    if (Test-Path -LiteralPath $logDir) {
        Write-Step "Removing $logDir"
        Remove-Item -LiteralPath $logDir -Recurse -Force
    }
}

Write-Host ""
Write-Host "Retweak removed. Enforced registry values were left as they are." -ForegroundColor Green
