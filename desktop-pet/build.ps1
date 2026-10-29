[CmdletBinding()]
param(
    [switch]$Release,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'),
    [switch]$SelfContained
)
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
$runtimeConfigPath = Join-Path $dist 'DesktopPet.runtimeconfig.json'
$hasBundledHost = (Test-Path -LiteralPath (Join-Path $dist 'hostfxr.dll')) -or (Test-Path -LiteralPath (Join-Path $dist 'hostpolicy.dll'))
$hadIncludedFrameworks = $false
if (Test-Path -LiteralPath $runtimeConfigPath) {
    $existingConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
    $hadIncludedFrameworks = @($existingConfig.runtimeOptions.includedFrameworks | Where-Object { $null -ne $_ }).Count -gt 0
}
# A framework-dependent apphost beside a previously published hostfxr.dll can
# fail before Main runs. Preserve an existing bundled-runtime output as a unit.
$publishSelfContained = $SelfContained.IsPresent -or $hasBundledHost -or $hadIncludedFrameworks
if ($publishSelfContained) {
    if (-not $SelfContained.IsPresent) { Write-Host 'Existing bundled runtime detected; preserving self-contained output.' }
    & $dotnet publish (Join-Path $projectRoot 'DesktopPet.csproj') --configuration $configuration --runtime win-x64 --self-contained true --output $dist --nologo
} else {
    & $dotnet build (Join-Path $projectRoot 'DesktopPet.csproj') --configuration $configuration --output $dist --nologo
}
if ($LASTEXITCODE -ne 0) { throw "DesktopPet build failed (exit $LASTEXITCODE)." }
$outputConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
$includedFrameworks = @($outputConfig.runtimeOptions.includedFrameworks | Where-Object { $null -ne $_ })
$externalFrameworks = @($outputConfig.runtimeOptions.frameworks | Where-Object { $null -ne $_ })
if ($publishSelfContained) {
    if ($includedFrameworks.Count -eq 0 -or $externalFrameworks.Count -ne 0) {
        throw 'Self-contained output has an inconsistent runtime configuration. Publish again to a new output directory.'
    }
    foreach ($runtimeFile in @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $dist $runtimeFile))) { throw "Self-contained output is missing $runtimeFile." }
    }
} elseif ($includedFrameworks.Count -ne 0 -or $externalFrameworks.Count -eq 0 -or
    (Test-Path -LiteralPath (Join-Path $dist 'hostfxr.dll')) -or (Test-Path -LiteralPath (Join-Path $dist 'hostpolicy.dll'))) {
    throw 'Framework-dependent output contains a bundled runtime or inconsistent configuration. Use -SelfContained or a new output directory.'
}
$outputMode = if ($publishSelfContained) { 'self-contained' } else { 'framework-dependent' }
Write-Host "Built ($outputMode): $(Join-Path $dist 'DesktopPet.exe')"
