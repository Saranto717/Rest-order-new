param([string]$Version = '17.3.0')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $repo 'RestaurantOrders_Mobile_WiFi_Source\Source\RestaurantOrders.csproj'
$publish = Join-Path $repo 'artifacts\publish\win-x64'
$output = Join-Path $repo 'artifacts\installer'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Path $output -Force | Out-Null
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish `
    -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
$forbidden = Get-ChildItem $publish -Recurse -File | Where-Object {
    $_.Extension -in @('.cs', '.razor', '.csproj', '.sln', '.pdb', '.db', '.sqlite', '.log') -or $_.Name -match '\.db-(wal|shm)$'
}
if ($forbidden) { throw ('Publish contains forbidden files: ' + (($forbidden | ForEach-Object { $_.FullName.Substring($publish.Length + 1) }) -join ', ')) }
$compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
if (!(Test-Path $compiler)) { throw 'Install Inno Setup 6 before building the installer.' }
& $compiler "/DPublishDir=$publish" "/DOutputDir=$output" "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'RestaurantOrders.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
$setup = Join-Path $output "RestaurantOrders-Setup-$Version-win-x64.exe"
$hash = (Get-FileHash -Algorithm SHA256 $setup).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($setup))" | Set-Content -Path ($setup + '.sha256') -Encoding ASCII
Write-Host "Installer ready: $setup"
