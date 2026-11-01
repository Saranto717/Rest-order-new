param([Parameter(Mandatory = $true)][string]$SetupPath)
$ErrorActionPreference = 'Stop'
$data = Join-Path $env:ProgramData 'RestaurantOrders'
$install = Join-Path $env:ProgramFiles 'RestaurantOrders'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$testProject = Join-Path $repo 'RestaurantOrders_Mobile_WiFi_Source\Tests\InstallationRegression\InstallationRegression.csproj'
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Install {
    $process = Start-Process -FilePath $SetupPath -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
    Assert ($process.ExitCode -eq 0) "Setup exited with $($process.ExitCode)"
    Assert ((Invoke-WebRequest -UseBasicParsing 'http://localhost:5080/health/ready').Content.Trim() -eq 'ready') 'Service is not ready'
    Assert ((Invoke-WebRequest -UseBasicParsing 'http://localhost:5080/app.css').StatusCode -eq 200) 'Static web files unavailable under service working directory'
}
try {
    Assert (!(Test-Path (Join-Path $data 'restaurant.db'))) 'Smoke test requires a clean Windows runner, not a customer installation.'
    Install
    $service = Get-CimInstance Win32_Service -Filter "Name='RestaurantOrders'"
    Assert ($service.StartMode -eq 'Auto' -and $service.StartName -eq 'NT AUTHORITY\NetworkService') 'Incorrect service startup/account'
    Assert ((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\RestaurantOrders').DelayedAutoStart -eq 1) 'Delayed automatic startup not configured'
    Assert (Test-Path (Join-Path $env:PUBLIC 'Desktop\RestaurantOrders.lnk')) 'Desktop shortcut missing'
    $rule = Get-NetFirewallRule -Name 'RestaurantOrders-Local-HTTP'
    Assert ($rule.Profile.ToString() -eq 'Private') 'Firewall rule must be private-network only'
    Stop-Service RestaurantOrders
    dotnet run --project $testProject --no-build -- --installed-database (Join-Path $data 'restaurant.db') write
    if ($LASTEXITCODE -ne 0) { throw 'Could not write database preservation marker.' }
    $keysBefore = @(Get-ChildItem (Join-Path $data 'DataProtection-Keys') -File | Select-Object -ExpandProperty Name)
    Assert ($keysBefore.Count -gt 0) 'Persistent authentication key ring missing'
    Install
    Stop-Service RestaurantOrders
    dotnet run --project $testProject --no-build -- --installed-database (Join-Path $data 'restaurant.db') check
    if ($LASTEXITCODE -ne 0) { throw 'Customer data was not preserved by upgrade.' }
    Assert ((Get-ChildItem (Join-Path $data 'Backups') -Directory).Count -gt 0) 'Upgrade backup missing'
    foreach ($key in $keysBefore) { Assert (Test-Path (Join-Path (Join-Path $data 'DataProtection-Keys') $key)) 'Authentication key lost on upgrade' }
    Start-Service RestaurantOrders
    $process = Start-Process -FilePath (Join-Path $install 'unins000.exe') -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
    Assert ($process.ExitCode -eq 0) 'Uninstall failed'
    Assert (!(Get-Service RestaurantOrders -ErrorAction SilentlyContinue)) 'Service remains after uninstall'
    Assert (!(Get-NetFirewallRule -Name 'RestaurantOrders-Local-HTTP' -ErrorAction SilentlyContinue)) 'Firewall rule remains after uninstall'
    Assert (Test-Path (Join-Path $data 'restaurant.db')) 'Uninstall removed customer database'
    dotnet run --project $testProject --no-build -- --installed-database (Join-Path $data 'restaurant.db') check
    if ($LASTEXITCODE -ne 0) { throw 'Uninstall altered customer data.' }
    Write-Host 'PASS: install, service startup, static files, upgrade, database/key preservation, backup and uninstall.'
} catch {
    foreach ($file in @('installer-error.log', 'installer.log')) {
        $path = Join-Path $data $file
        if (Test-Path $path) { Get-Content $path -Tail 80 | Write-Host }
    }
    Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='RestaurantOrders'} -MaxEvents 10 -ErrorAction SilentlyContinue | Format-List | Out-Host
    throw
}
