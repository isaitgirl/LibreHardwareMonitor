param(
    [string]$ServiceName = "LibreHardwareMonitorService",
    [string]$InstallPath = "C:\LibreHardwareMonitorService",
    [string]$SourcePath = (Split-Path -Parent $PSCommandPath)
)

$ErrorActionPreference = "Stop"
$LogFile = "C:\LibreHardwareMonitorService\install-service.log"

function Initialize-InstallLog {
    $logDir = Split-Path -Parent $LogFile
    if (-not (Test-Path -Path $logDir)) {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }

    if (-not (Test-Path -Path $LogFile)) {
        New-Item -ItemType File -Path $LogFile -Force | Out-Null
    }
}

function Write-InstallLog {
    param(
        [string]$Message,
        [string]$Level = "INFO"
    )

    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    $line = "[$timestamp] [$Level] $Message"
    Add-Content -Path $LogFile -Value $line
    Write-Host $line
}

function Invoke-LoggedCommand {
    param(
        [string]$CommandText,
        [scriptblock]$Command
    )

    Write-InstallLog "Executing command: $CommandText" "DEBUG"
    $output = & $Command 2>&1
    foreach ($line in $output) {
        Write-InstallLog ("command output: " + $line) "DEBUG"
    }

    if ($LASTEXITCODE -ne 0) {
        Write-InstallLog "Command failed (exit code $LASTEXITCODE): $CommandText" "ERROR"
        throw "Command failed: $CommandText"
    }
}

Initialize-InstallLog
Write-InstallLog "=========== install-service.ps1 started =========="
Write-InstallLog "Parameters: ServiceName=$ServiceName; InstallPath=$InstallPath; SourcePath=$SourcePath"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    Write-InstallLog "Administrator check failed. Script must run elevated." "ERROR"
    throw "This script must run as Administrator."
}

Write-InstallLog "Administrator check passed."

#Write-Host "Installing $ServiceName to $InstallPath"
#Write-InstallLog "Ensuring install directory exists: $InstallPath"
#New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null

#$exclude = @("install-service.ps1", "uninstall-service.ps1", "install-service.cmd", "uninstall-service.cmd")
#Write-InstallLog "Copying deployment files from $SourcePath to $InstallPath"

#$copiedFiles = 0
#Get-ChildItem -Path $SourcePath -Recurse -File |
#    Where-Object { $exclude -notcontains $_.Name } |
#    ForEach-Object {
#        $relative = $_.FullName.Substring($SourcePath.Length).TrimStart("\\")
#        $target = Join-Path $InstallPath $relative
#        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
#        Copy-Item -Path $_.FullName -Destination $target -Force
#        $copiedFiles++
#        Write-InstallLog "Copied file: $relative" "DEBUG"
#    }
#Write-InstallLog "File copy completed. Files copied: $copiedFiles"

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $existingService) {
    Write-Host "Existing service found. Stopping and deleting..."
    Write-InstallLog "Existing service found: $ServiceName (status=$($existingService.Status))"
    if ($existingService.Status -ne "Stopped") {
        Write-InstallLog "Stopping existing service: $ServiceName"
        Stop-Service -Name $ServiceName -Force
        Write-InstallLog "Existing service stopped: $ServiceName"
    }
    Invoke-LoggedCommand -CommandText "sc.exe delete $ServiceName" -Command {
        & sc.exe delete $ServiceName
    }
}
else {
    Write-InstallLog "No existing service found with name: $ServiceName"
}

$exePath = Join-Path $InstallPath "LibreHardwareMonitorService.exe"
if (-not (Test-Path $exePath)) {
    Write-InstallLog "Expected executable not found at: $exePath" "ERROR"
    throw "Expected executable not found at $exePath"
}

Write-InstallLog "Found service executable: $exePath"

$telegrafRootDir = "C:\Telegraf"
$telegrafConfigDir = "C:\Telegraf\telegraf.d"
$telegrafConfigSource = Join-Path $InstallPath "telegraf-lhm.conf"
$telegrafConfigTarget = Join-Path $telegrafConfigDir "telegraf-lhm.conf"

if (-not (Test-Path -Path $telegrafRootDir)) {
    Write-InstallLog "Telegraf root directory does not exist: $telegrafRootDir" "ERROR"
    throw "Telegraf root directory not found at $telegrafRootDir"
}

if (-not (Test-Path -Path $telegrafConfigDir)) {
    Write-InstallLog "Creating Telegraf config directory: $telegrafConfigDir"
    New-Item -ItemType Directory -Path $telegrafConfigDir -Force | Out-Null
}
else {
    Write-InstallLog "Telegraf config directory already exists: $telegrafConfigDir" "DEBUG"
}

if (-not (Test-Path -Path $telegrafConfigSource)) {
    Write-InstallLog "Telegraf config source file not found: $telegrafConfigSource" "ERROR"
    throw "Telegraf config source file not found at $telegrafConfigSource"
}

Write-InstallLog "Copying Telegraf config from $telegrafConfigSource to $telegrafConfigTarget"
Copy-Item -Path $telegrafConfigSource -Destination $telegrafConfigTarget -Force
Write-InstallLog "Telegraf config copy completed: $telegrafConfigTarget"

Write-InstallLog "Restarting Telegraf service to apply config changes"
try {
    Restart-Service -Name "telegraf" -Force -ErrorAction Stop
    Write-InstallLog "Telegraf service restarted successfully"
}
catch {
    Write-InstallLog "Failed to restart Telegraf service: $($_.Exception.Message)" "ERROR"
    throw
}

$binPath = '"' + $exePath + '" --contentRoot "' + $InstallPath + '"'
Write-InstallLog "Using binPath: $binPath"

Invoke-LoggedCommand -CommandText "sc.exe create $ServiceName binPath= $binPath start= auto obj= \"LocalSystem\" DisplayName= \"Libre Hardware Monitor Service\"" -Command {
    & sc.exe create $ServiceName binPath= $binPath start= auto obj= "LocalSystem" DisplayName= "Libre Hardware Monitor Service"
}
Invoke-LoggedCommand -CommandText "sc.exe description $ServiceName \"Exports LibreHardwareMonitor temperature metrics for Prometheus.\"" -Command {
    & sc.exe description $ServiceName "Exports LibreHardwareMonitor temperature metrics for Prometheus."
}
Invoke-LoggedCommand -CommandText "sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000" -Command {
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000
}

Write-InstallLog "Starting service: $ServiceName"
Start-Service -Name $ServiceName
Write-InstallLog "Service started successfully: $ServiceName"
Write-Host "$ServiceName installed and started successfully."
Write-InstallLog "=========== install-service.ps1 completed successfully =========="