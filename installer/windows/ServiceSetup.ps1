param(
    [Parameter(Mandatory = $true)][ValidateSet('Install', 'Uninstall')][string]$Action,
    [Parameter(Mandatory = $true)][string]$InstallDirectory,
    [string]$ImportDatabase = '',
    [string]$DataDirectory = (Join-Path $env:ProgramData 'RestaurantOrders')
)
$ErrorActionPreference = 'Stop'
$serviceName = 'RestaurantOrders'
$ruleName = 'RestaurantOrders-Local-HTTP'
$port = 5080

function Invoke-ServiceControl([string[]]$Arguments) {
    & "$env:SystemRoot\System32\sc.exe" @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Service control failed ($LASTEXITCODE)." }
}

function Stop-Application {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(45))
    }
}

try {
    if ($Action -eq 'Uninstall') {
        Stop-Application
        if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
            Invoke-ServiceControl -Arguments @('delete', $serviceName)
        }
        Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        # Customer databases, configuration, key rings and backups are deliberately retained.
        exit 0
    }

    New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
    # Localized Windows account names are avoided by using well-known SIDs.
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($entry in @(@('S-1-5-18', 'FullControl'), @('S-1-5-32-544', 'FullControl'), @('S-1-5-20', 'Modify'))) {
        $sid = New-Object System.Security.Principal.SecurityIdentifier($entry[0])
        $access = New-Object System.Security.AccessControl.FileSystemAccessRule($sid, $entry[1], 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($access)
    }
    Set-Acl -LiteralPath $DataDirectory -AclObject $acl
    Start-Transcript -Path (Join-Path $DataDirectory 'installer.log') -Append | Out-Null
    Stop-Application

    $database = Join-Path $DataDirectory 'restaurant.db'
    if (Test-Path -LiteralPath $database) {
        $backup = Join-Path $DataDirectory ('Backups\before-update-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Path $backup -Force | Out-Null
        foreach ($name in @('restaurant.db', 'restaurant.db-wal', 'restaurant.db-shm', 'appsettings.Installed.json', 'DataProtection-Keys')) {
            $item = Join-Path $DataDirectory $name
            if (Test-Path -LiteralPath $item) { Copy-Item -LiteralPath $item -Destination $backup -Recurse }
        }
    }

    $exe = Join-Path $InstallDirectory 'RestaurantOrders.exe'
    if ($ImportDatabase) {
        if (Test-Path -LiteralPath $database) { throw 'An installed database already exists. Import will not overwrite it.' }
        & $exe "--import-database=$ImportDatabase" "--data-directory=$DataDirectory"
        if ($LASTEXITCODE -ne 0) { throw 'The selected database could not be imported. The original file was not modified.' }
    }

    $settings = Join-Path $DataDirectory 'appsettings.Installed.json'
    if (!(Test-Path -LiteralPath $settings)) {
        @{ Urls = "http://0.0.0.0:$port" } | ConvertTo-Json | Set-Content -LiteralPath $settings -Encoding UTF8
    }
    # An update preserves the original configuration. The installer owns this service's port.
    $configured = Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json
    if ($configured.Urls -ne "http://0.0.0.0:$port") {
        throw "Expected installed Urls http://0.0.0.0:$port. Existing configuration was preserved; check $settings."
    }
    if ([System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners().Port -contains $port) {
        throw "Port $port is already in use. Close the other application and run Setup again."
    }
    if (![System.Diagnostics.EventLog]::SourceExists($serviceName)) {
        New-EventLog -LogName Application -Source $serviceName
    }

    $binary = '"' + $exe + '" --Installation:Enabled=true --Installation:DataDirectory="' + $DataDirectory + '"'
    $existingService = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($existingService) {
        $changed = Invoke-CimMethod -InputObject $existingService -MethodName Change -Arguments @{ PathName = $binary }
        if ($changed.ReturnValue -ne 0) { throw "Could not update service path ($($changed.ReturnValue))." }
    } else {
        New-Service -Name $serviceName -BinaryPathName $binary -DisplayName 'RestaurantOrders' -StartupType Automatic | Out-Null
    }
    Invoke-ServiceControl -Arguments @('config', $serviceName, 'start=', 'delayed-auto', 'obj=', 'NT AUTHORITY\NetworkService')
    Invoke-ServiceControl -Arguments @('description', $serviceName, 'RestaurantOrders local ordering and POS server')
    Invoke-ServiceControl -Arguments @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/10000/restart/30000')

    Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -Name $ruleName -DisplayName 'RestaurantOrders - Local network' -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort $port -Profile Private -RemoteAddress LocalSubnet -Program $exe | Out-Null

    $addresses = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' -and $_.AddressState -eq 'Preferred' } |
        Select-Object -ExpandProperty IPAddress -Unique)
    $links = @("http://$($env:COMPUTERNAME):$port/") + @($addresses | ForEach-Object { "http://$($_):$port/" })
    $htmlLinks = ($links | ForEach-Object { '<li><a href="' + $_ + '">' + $_ + '</a></li>' }) -join "`n"
    @"
<!doctype html><html lang="el"><meta charset="utf-8"><title>RestaurantOrders</title>
<style>body{font:20px system-ui;max-width:800px;margin:50px auto;padding:20px}li{margin:20px 0}</style>
<h1>RestaurantOrders</h1><p>Στον κεντρικό υπολογιστή: <a href="http://localhost:$port/">Άνοιγμα εφαρμογής</a></p>
<p>Από κινητά και tablets στο ίδιο Wi-Fi του καταστήματος, άνοιξε μία από τις διευθύνσεις:</p><ul>$htmlLinks</ul>
<p>Ο κεντρικός υπολογιστής πρέπει να παραμένει ενεργός. Το δίκτυο των Windows πρέπει να είναι Ιδιωτικό.</p></html>
"@ | Set-Content -LiteralPath (Join-Path $InstallDirectory 'Access.html') -Encoding UTF8

    Start-Service -Name $serviceName
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Seconds 2
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$port/health/ready" -TimeoutSec 3
            if ($response.StatusCode -eq 200 -and $response.Content.Trim() -eq 'ready' -and (Get-Service $serviceName).Status -eq 'Running') {
                $ready = $true; break
            }
        } catch { }
    }
    if (!$ready) { throw 'The service did not become ready. Check the Windows Application event log and installer.log.' }
    Stop-Transcript | Out-Null
    exit 0
} catch {
    $message = $_.Exception.Message
    try { Add-Content -LiteralPath (Join-Path $DataDirectory 'installer-error.log') -Value ((Get-Date -Format o) + ' ' + $message) } catch { }
    try { Stop-Transcript | Out-Null } catch { }
    Write-Error $message
    exit 1
}
