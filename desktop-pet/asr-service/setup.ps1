param(
    [Parameter(Mandatory = $true)][string]$PythonExe,
    [Parameter(Mandatory = $true)][string]$WorkspaceRoot,
    [switch]$DownloadModel
)
$ErrorActionPreference = 'Stop'
$asrWorkspace = (Resolve-Path -LiteralPath $WorkspaceRoot).Path
$asrPython = (Resolve-Path -LiteralPath $PythonExe).Path
$asrTools = Join-Path $asrWorkspace '.tools'
$asrEnvironment = Join-Path $asrTools 'deepseek-asr-venv'
$asrTemp = Join-Path $asrTools 'deepseek-asr-temp'
$asrModels = Join-Path $asrTools 'deepseek-asr-models'
New-Item -ItemType Directory -Path $asrTools,$asrEnvironment,$asrTemp,$asrModels -Force | Out-Null
$env:TEMP = $asrTemp
$env:TMP = $asrTemp
$env:PYTHONUTF8 = '1'
$env:PIP_CACHE_DIR = Join-Path $asrTools 'deepseek-asr-cache'
$asrVersion = & $asrPython -c 'import sys; print(str(sys.version_info.major)+"."+str(sys.version_info.minor))'
if ($LASTEXITCODE -ne 0 -or $asrVersion -ne '3.12') { throw 'Please supply a Python 3.12 executable.' }
& $asrPython -m venv $asrEnvironment
if ($LASTEXITCODE -ne 0) { throw 'Cannot create isolated speech recognition environment.' }
$asrRuntime = Join-Path $asrEnvironment 'Scripts\python.exe'
& $asrRuntime -m pip install --disable-pip-version-check -r (Join-Path $PSScriptRoot 'requirements-lock.txt')
if ($LASTEXITCODE -ne 0) { throw 'Speech recognition dependency installation failed.' }
& $asrRuntime -m pip check
if ($LASTEXITCODE -ne 0) { throw 'Speech recognition dependencies are inconsistent.' }
if ($DownloadModel) {
    & $asrRuntime (Join-Path $PSScriptRoot 'download_model.py') --model-root $asrModels
    if ($LASTEXITCODE -ne 0) { throw 'Official speech recognition model download failed.' }
}
[ordered]@{ model_path = (Join-Path $asrModels 'faster-whisper-small') } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'config.json') -Encoding utf8
Write-Output "Speech recognition runtime prepared at $asrRuntime"
Write-Output 'DesktopPet starts the offline recognizer only after an explicit recording.'
