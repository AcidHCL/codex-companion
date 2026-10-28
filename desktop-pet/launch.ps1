[CmdletBinding()]
param([switch]$Rebuild, [Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArguments)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'dist\DesktopPet.exe'
if ($Rebuild -or -not (Test-Path -LiteralPath $exe)) { & (Join-Path $PSScriptRoot 'build.ps1') -Release }
$localRuntime = Join-Path (Split-Path -Parent $PSScriptRoot) '.tools\dotnet10'
if (Test-Path -LiteralPath (Join-Path $localRuntime 'dotnet.exe')) {
    $env:DOTNET_ROOT = $localRuntime
    $env:DOTNET_ROOT_X64 = $localRuntime
}
if ($AppArguments -and $AppArguments.Count -gt 0) {
    Start-Process -FilePath $exe -WorkingDirectory (Join-Path $PSScriptRoot 'dist') -ArgumentList $AppArguments -WindowStyle Hidden
} else {
    Start-Process -FilePath $exe -WorkingDirectory (Join-Path $PSScriptRoot 'dist') -WindowStyle Hidden
}
