param(
    [Parameter(Mandatory = $true)][string]$PythonExe,
    [Parameter(Mandatory = $true)][string]$WorkspaceRoot,
    [switch]$DownloadModel
)
$ErrorActionPreference = 'Stop'
$voiceWorkspace = (Resolve-Path -LiteralPath $WorkspaceRoot).Path
$voicePython = (Resolve-Path -LiteralPath $PythonExe).Path
$voiceVersion = & $voicePython -c 'import sys; print(str(sys.version_info.major)+"."+str(sys.version_info.minor))'
if ($LASTEXITCODE -ne 0 -or $voiceVersion -ne '3.12') { throw 'Please supply a Python 3.12 executable.' }
$voiceTools = Join-Path $voiceWorkspace '.tools'
$voiceEnvironment = Join-Path $voiceTools 'deepseek-voice-venv'
$voiceTemp = Join-Path $voiceTools 'deepseek-voice-temp'
$voiceModels = Join-Path $voiceTools 'deepseek-voice-models'
New-Item -ItemType Directory -Path $voiceTools,$voiceEnvironment,$voiceTemp,$voiceModels -Force | Out-Null
$env:TEMP = $voiceTemp
$env:TMP = $voiceTemp
$env:PYTHONUTF8 = '1'
$env:PIP_CACHE_DIR = Join-Path $voiceTools 'deepseek-voice-cache\pip'
& $voicePython -m venv $voiceEnvironment
if ($LASTEXITCODE -ne 0) { throw 'Cannot create isolated voice environment.' }
$voiceRuntime = Join-Path $voiceEnvironment 'Scripts\python.exe'
& $voiceRuntime -m pip install --disable-pip-version-check --index-url https://download.pytorch.org/whl/cu128 torch==2.8.0 torchaudio==2.8.0
if ($LASTEXITCODE -ne 0) { throw 'CUDA PyTorch installation failed.' }
& $voiceRuntime -m pip install --disable-pip-version-check -r (Join-Path $PSScriptRoot 'requirements-lock.txt')
if ($LASTEXITCODE -ne 0) { throw 'Voice dependency installation failed.' }
& $voiceRuntime -m pip check
if ($LASTEXITCODE -ne 0) { throw 'Voice dependencies are inconsistent.' }
if ($DownloadModel) {
    & $voiceRuntime (Join-Path $PSScriptRoot 'download_model.py') --model-root $voiceModels
    if ($LASTEXITCODE -ne 0) { throw 'Official model download failed.' }
}
$voiceConfig = [ordered]@{
    model_path = (Join-Path $voiceModels 'Qwen3-TTS-12Hz-1.7B-Base')
    reference_path = 'reference.wav'
    idle_seconds = 60
    minimum_free_gib = 7
    request_timeout = 480
}
$voiceConfig | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'config.json') -Encoding utf8
Write-Output "Voice runtime prepared at $voiceRuntime"
Write-Output 'Keep reference.wav beside server.py. The running service is offline and is started by DesktopPet.'
