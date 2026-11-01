$ErrorActionPreference = 'Stop'
try {
    $service = Get-Service -Name 'RestaurantOrders' -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name 'RestaurantOrders' -Force -ErrorAction Stop
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(45))
    }
    exit 0
} catch {
    Write-Error $_
    exit 1
}
