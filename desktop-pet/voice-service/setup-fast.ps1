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
$voiceEnvironment = Join-Path $voiceWorkspace '.tools\deepseek-voice-fast-venv'
$voiceModels = Join-Path $voiceWorkspace '.tools\deepseek-voice-models'
$voiceTemp = Join-Path $voiceWorkspace '.tools\deepseek-voice-temp'
New-Item -ItemType Directory -Path $voiceEnvironment,$voiceModels,$voiceTemp -Force | Out-Null
$env:PYTHONUTF8 = '1'
$env:TEMP = $voiceTemp
$env:TMP = $voiceTemp
$env:PIP_CACHE_DIR = Join-Path $voiceWorkspace '.tools\deepseek-voice-cache\pip'
& $voicePython -m venv $voiceEnvironment
if ($LASTEXITCODE -ne 0) { throw 'Cannot create isolated fast voice environment.' }
$voiceRuntime = Join-Path $voiceEnvironment 'Scripts\python.exe'
& $voiceRuntime -m pip install --disable-pip-version-check --progress-bar off --index-url https://download.pytorch.org/whl/cu128 torch==2.8.0 torchaudio==2.8.0
if ($LASTEXITCODE -ne 0) { throw 'CUDA PyTorch installation failed.' }
& $voiceRuntime -m pip install --disable-pip-version-check --progress-bar off -r (Join-Path $PSScriptRoot 'requirements-fast-lock.txt')
if ($LASTEXITCODE -ne 0) { throw 'Fast voice dependency installation failed.' }
& $voiceRuntime -m pip check
if ($LASTEXITCODE -ne 0) { throw 'Fast voice dependencies are inconsistent.' }
if ($DownloadModel) {
    & $voiceRuntime (Join-Path $PSScriptRoot 'download_model.py') --model-root $voiceModels
    if ($LASTEXITCODE -ne 0) { throw 'Official model download failed.' }
}
[ordered]@{
    backend = 'faster'
    model_path = (Join-Path $voiceModels 'Qwen3-TTS-12Hz-1.7B-Base')
    reference_path = 'reference.wav'
    idle_seconds = 60
    minimum_free_gib = 7
    request_timeout = 480
    stream_chunk_size = 8
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'config.json') -Encoding utf8
Write-Output "Fast voice runtime prepared at $voiceRuntime"
Write-Output 'The old deepseek-voice-venv is unchanged. Same model and reference.wav are reused.'
