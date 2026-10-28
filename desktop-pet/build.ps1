[CmdletBinding()]
param([switch]$Release, [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$localSdk = Join-Path (Split-Path -Parent $projectRoot) '.tools\dotnet10\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_HOME = Join-Path (Split-Path -Parent $projectRoot) '.tools\dotnet-home'
$sdkVersion = & $dotnet --version
if ($LASTEXITCODE -ne 0 -or ([version]($sdkVersion -split '-')[0]).Major -lt 10) { throw 'DesktopPet requires .NET SDK 10 or later. Install it in .tools\dotnet10 or make it available on PATH.' }
$configuration = if ($Release) { 'Release' } else { 'Debug' }
$dist = [IO.Path]::GetFullPath($OutputDirectory)
& $dotnet build (Join-Path $projectRoot 'DesktopPet.csproj') --configuration $configuration --output $dist --nologo
if ($LASTEXITCODE -ne 0) { throw "DesktopPet build failed (exit $LASTEXITCODE)." }
Write-Host "Built: $(Join-Path $dist 'DesktopPet.exe')"
