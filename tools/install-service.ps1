<#
    Installs the library PC's copy of SMA Library as a Windows service.

    Run once, from an elevated PowerShell:

        powershell -ExecutionPolicy Bypass -File tools\install-service.ps1

    Why a service rather than a scheduled task at logon: this machine is the one wired to the
    reader, and the hosted kiosk only sees a reader while this copy is running. A service starts at
    boot, so a reboot does not leave the pad dead until somebody signs in, and Windows restarts it
    by itself if the process dies.

    The application is published to its own folder rather than run from the source tree. A service
    holding a lock on bin\Debug would make every later build fail, and the source tree is not where
    a production copy of anything should live.

    Re-running is safe: an existing service is stopped and its files replaced, then it is started
    again. Configuration is preserved - appsettings.json holds the database connection string and
    the bridge secret, and is deliberately not overwritten once it exists.
#>

[CmdletBinding()]
param(
    [string] $ServiceName  = 'SMALibrary',
    [string] $DisplayName  = 'SMA Library',
    [string] $InstallPath  = 'C:\SMA-Library\app',
    [string] $ListenUrl    = 'http://localhost:5000'
)

$ErrorActionPreference = 'Stop'

function Assert-Elevated {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This must run from an elevated PowerShell. Right-click PowerShell and choose Run as administrator.'
    }
}

Assert-Elevated

$project = Join-Path $PSScriptRoot '..\Library Management system.csproj' | Resolve-Path
$source  = Split-Path $project -Parent

Write-Host ''
Write-Host "  Project      : $project"
Write-Host "  Installing to: $InstallPath"
Write-Host "  Service      : $ServiceName ($DisplayName)"
Write-Host ''

# ---- stop an existing service so its files can be replaced ------------------
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($existing) {
    Write-Host '  Stopping the existing service...'
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $existing.WaitForStatus('Stopped', '00:01:00')
    }
}

# ---- publish ---------------------------------------------------------------
# Self-contained so the service does not depend on a .NET runtime staying installed on this PC.
Write-Host '  Publishing (this takes a minute)...'

$publishTemp = Join-Path $env:TEMP "sma-library-publish-$PID"

& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishTemp --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# ---- keep the existing configuration ---------------------------------------
$liveConfig = Join-Path $InstallPath 'appsettings.json'
$keptConfig = $null

if (Test-Path $liveConfig) {
    # It carries the connection string and the bridge secret. Replacing it on an upgrade would
    # silently disconnect the reader from the hosted site.
    $keptConfig = Get-Content $liveConfig -Raw
    Write-Host '  Keeping the existing appsettings.json.'
}

New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
Copy-Item (Join-Path $publishTemp '*') $InstallPath -Recurse -Force
Remove-Item $publishTemp -Recurse -Force

if ($keptConfig) {
    Set-Content -Path $liveConfig -Value $keptConfig -Encoding utf8
} else {
    # First install: carry over the copy that is already working in the source tree, so the
    # database connection and the bridge secret come across rather than having to be retyped.
    $sourceConfig = Join-Path $source 'appsettings.json'

    if (Test-Path $sourceConfig) {
        Copy-Item $sourceConfig $liveConfig -Force
        Write-Host '  Copied appsettings.json from the source tree.'
    } else {
        Write-Warning 'No appsettings.json found. Create one in the install folder before starting.'
    }
}

# A service gets no --urls argument, so the address has to be in configuration.
$config = Get-Content $liveConfig -Raw | ConvertFrom-Json
if (-not $config.Urls) {
    $config | Add-Member -NotePropertyName Urls -NotePropertyValue $ListenUrl -Force
    $config | ConvertTo-Json -Depth 20 | Set-Content $liveConfig -Encoding utf8
    Write-Host "  Set Urls to $ListenUrl"
}

# ---- create or update the service ------------------------------------------
$exe = Join-Path $InstallPath 'Library Management system.exe'
if (-not (Test-Path $exe)) { throw "Published executable not found at $exe" }

if (-not $existing) {
    Write-Host '  Creating the service...'
    # Quoted because the path contains spaces; binPath= needs the space after the equals sign.
    & sc.exe create $ServiceName binPath= "`"$exe`"" start= auto DisplayName= "$DisplayName" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'sc create failed.' }

    & sc.exe description $ServiceName "Runs the SMA Library application and relays the RFID reader to the hosted site." | Out-Null
} else {
    Write-Host '  Service already exists; updating its binary path.'
    & sc.exe config $ServiceName binPath= "`"$exe`"" start= auto | Out-Null
}

# Restart on failure rather than leaving the reader dead: after 5s, then 10s, then every 60s.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/60000 | Out-Null

Write-Host '  Starting...'
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Running', '00:02:00')

Write-Host ''
Write-Host "  $ServiceName is $((Get-Service -Name $ServiceName).Status) and set to start automatically."
Write-Host ''
Write-Host '  Check it:'
Write-Host "    Get-Service $ServiceName"
Write-Host "    Invoke-WebRequest $ListenUrl/kiosk/state/1 | Select-Object -ExpandProperty Content"
Write-Host ''
Write-Host '  Remove it:'
Write-Host "    Stop-Service $ServiceName; sc.exe delete $ServiceName"
Write-Host ''
