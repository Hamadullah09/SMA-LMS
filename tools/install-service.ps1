<#
    Installs the library PC's copy of SMA Library as a Windows service.

    Run once, from any PowerShell:

        powershell -ExecutionPolicy Bypass -File tools\install-service.ps1

    It needs administrator rights to create a service and will ask Windows to elevate, so approve
    the prompt that appears. The work then happens in the elevated window it opens.

    Why a service rather than a scheduled task at logon: this machine is the one wired to the
    reader, and the hosted kiosk only sees a reader while this copy is running. A service starts at
    boot, so a reboot does not leave the pad dead until somebody signs in, and Windows restarts it
    by itself if the process dies.

    The application is published to its own folder rather than run from the source tree. A service
    holding a lock on bin\Debug would make every later build fail, and the source tree is not where
    a production copy of anything should live.

    The service runs under its own virtual account, NT SERVICE\SMALibrary, and the script grants
    that account access to the database. Windows authentication means the service authenticates as
    itself, and without the grant it would start cleanly and then fail on its first query.

    Re-running is safe: an existing service is stopped and its files replaced, then it is started
    again. Configuration is preserved - appsettings.json holds the database connection string and
    the bridge secret, and is deliberately not overwritten once it exists.
#>

[CmdletBinding()]
param(
    [string] $ServiceName  = 'SMALibrary',
    [string] $DisplayName  = 'SMA Library',
    [string] $InstallPath  = 'C:\SMA-Library\app',
    # 5001, not 5000: a developer running dotnet run takes 5000, and a service that cannot bind
    # its port simply exits. Keeping them apart means both can run at once.
    [string] $ListenUrl    = 'http://localhost:5001'
)

$ErrorActionPreference = 'Stop'

function Test-Elevated {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Creating a service needs administrator rights. Rather than refusing and leaving the operator to
# find an elevated shell and navigate back here, relaunch through UAC: approving the prompt is the
# whole of it. -NoExit keeps the new window open so the result can actually be read, since an
# elevated window is a separate console that would otherwise vanish on completion.
if (-not (Test-Elevated)) {
    Write-Host ''
    Write-Host '  Administrator rights are needed to create a service.'
    Write-Host '  Asking Windows to elevate - approve the prompt that appears.'
    Write-Host ''

    $arguments = @(
        '-NoExit'
        '-NoProfile'
        '-ExecutionPolicy', 'Bypass'
        '-File', "`"$PSCommandPath`""
        '-ServiceName',    "`"$ServiceName`""
        '-DisplayName',    "`"$DisplayName`""
        '-InstallPath',    "`"$InstallPath`""
        '-ListenUrl',      "`"$ListenUrl`""
    )

    try {
        Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs
    }
    catch {
        Write-Host ''
        Write-Warning 'Elevation was declined, so nothing was installed.'
        Write-Host '  To do it by hand: open PowerShell as administrator, then run'
        Write-Host "    powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`""
        Write-Host ''
        exit 1
    }

    Write-Host '  Continuing in the elevated window.'
    exit 0
}

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

# ---- make the configuration fit for Production ------------------------------
# A service has no launchSettings.json, so it starts in Production - where ProductionGuards
# refuses to run with anything unsafe configured. A config copied out of a development source
# tree is not that, and the symptom is a service that will not start with nothing in the
# PowerShell output to say why: the reason only appears in the Windows event log.
#
# The sample-data seeder is the one that matters. Left enabled it inserts fictional books and
# simulated RFID tags into a live catalogue.
#
# The address is set here too, since a service is given no --urls argument.
$normaliser = Join-Path $PSScriptRoot 'prepare-service-config.js'

if (Test-Path $normaliser) {
    Write-Host '  Preparing the configuration for Production...'
    & node $normaliser $liveConfig $ListenUrl

    if ($LASTEXITCODE -ne 0) {
        throw 'Could not prepare the configuration. The service would refuse to start.'
    }
} else {
    Write-Warning "prepare-service-config.js not found beside this script; configuration left as copied."
}

# ---- create or update the service ------------------------------------------
$exe = Join-Path $InstallPath 'Library Management system.exe'
if (-not (Test-Path $exe)) { throw "Published executable not found at $exe" }

if (-not $existing) {
    Write-Host '  Creating the service...'
    # Quoted because the path contains spaces; binPath= needs the space after the equals sign.
    # obj= runs it under its own virtual account, NT SERVICE\<name>, which Windows creates with
    # the service and which has no password to manage. LocalSystem would work too and is the
    # default, but it is the most privileged account on the machine - anything else running as
    # SYSTEM would inherit whatever database rights are granted below.
    & sc.exe create $ServiceName binPath= "`"$exe`"" start= auto obj= "NT SERVICE\$ServiceName" DisplayName= "$DisplayName" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'sc create failed.' }

    & sc.exe description $ServiceName "Runs the SMA Library application and relays the RFID reader to the hosted site." | Out-Null
} else {
    Write-Host '  Service already exists; updating its binary path.'
    & sc.exe config $ServiceName binPath= "`"$exe`"" start= auto | Out-Null
}

# Restart on failure rather than leaving the reader dead: after 5s, then 10s, then every 60s.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/60000 | Out-Null

# ---- database access for the service account --------------------------------
# The connection string uses Windows authentication, so the service authenticates as itself. Its
# virtual account has no SQL login until one is made, which would leave the service starting
# cleanly and then failing on its first query - a failure that looks like a bug in the application
# rather than a missing grant.
#
# db_owner because Program.cs runs EF migrations at start-up, which is DDL. Scoped to this one
# database, so it is not server-wide.
$connection = $null
try {
    $configJson  = Get-Content $liveConfig -Raw | ConvertFrom-Json
    $connection  = $configJson.ConnectionStrings.DefaultConnection
} catch { }

if ($connection -and $connection -match 'Trusted_Connection\s*=\s*True') {
    $server   = ([regex]::Match($connection, '(?:Server|Data Source)\s*=\s*([^;]+)')).Groups[1].Value.Trim()
    $database = ([regex]::Match($connection, '(?:Database|Initial Catalog)\s*=\s*([^;]+)')).Groups[1].Value.Trim()
    $account  = "NT SERVICE\$ServiceName"

    if ($server -and $database) {
        Write-Host "  Granting $account access to $database on $server..."

        # Bracket-quoted rather than interpolated into the statement body: the account name
        # contains a backslash and SQL Server treats it as an identifier, not a string.
        $sql = @"
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$account')
    CREATE LOGIN [$account] FROM WINDOWS;

USE [$database];

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$account')
    CREATE USER [$account] FOR LOGIN [$account];

ALTER ROLE [db_owner] ADD MEMBER [$account];
"@

        $sqlFile = Join-Path $env:TEMP "sma-grant-$PID.sql"
        Set-Content -Path $sqlFile -Value $sql -Encoding utf8

        & sqlcmd -S $server -d master -i $sqlFile -b 2>&1 | Out-String | Write-Verbose
        $granted = ($LASTEXITCODE -eq 0)
        Remove-Item $sqlFile -Force -ErrorAction SilentlyContinue

        if ($granted) {
            Write-Host '    granted.'
        } else {
            Write-Warning "Could not grant database access to $account automatically."
            Write-Host  '    The service will start but fail to reach the database. Run this in SSMS'
            Write-Host  '    as an administrator, then restart the service:'
            Write-Host  ''
            Write-Host  "      CREATE LOGIN [$account] FROM WINDOWS;"
            Write-Host  "      USE [$database];"
            Write-Host  "      CREATE USER [$account] FOR LOGIN [$account];"
            Write-Host  "      ALTER ROLE [db_owner] ADD MEMBER [$account];"
            Write-Host  ''
        }
    }
} elseif ($connection) {
    # A connection string with its own credentials needs no Windows grant.
    Write-Host '  Connection string carries its own credentials; no Windows grant needed.'
}

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
Write-Host '  The reader is relayed to the hosted kiosk while this service runs.'
Write-Host '  A local kiosk is also available at ' + $ListenUrl + '/kiosk'
Write-Host ''
Write-Host '  Remove it:'
Write-Host "    Stop-Service $ServiceName; sc.exe delete $ServiceName"
Write-Host ''
