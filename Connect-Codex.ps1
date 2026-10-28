# Run from a local Codex task with its actual context. The application authenticates
# the pipe server and owns encrypted persistence; this helper never reads the store.
# DesktopPet relays this command into a verified independent desktop process
# before opening AppData. Caller context stays in memory-only child environment.
[CmdletBinding()]
param(
    [string]$ThreadId = $env:CODEX_THREAD_ID,
    [string]$TurnId = $env:CODEX_TURN_ID,
    [string]$Endpoint = $env:CODEX_APP_TOOLS_PIPE_PATH,
    [string]$ApplicationDirectory = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$pairingMutex = $null
$ownsPairingMutex = $false
$child = $null
$exitCode = 1

function Test-ContextId([string]$Value) {
    $parsedId = [Guid]::Empty
    return ([Guid]::TryParseExact($Value, 'D', [ref]$parsedId) -and $parsedId -ne [Guid]::Empty)
}

function Test-CodexEndpoint([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    $normalized = $Value.Trim()
    # Current AppTools and the older shared browser/AppTools service use
    # different prefixes. This is only a format check; DesktopPet still verifies
    # the peer's process, publisher, user, session, and read-only tool catalog.
    foreach ($prefix in @('\\.\pipe\codex-app-tools-', '\\.\pipe\codex-browser-use-')) {
        if ($normalized.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            $suffix = $normalized.Substring($prefix.Length)
            return ($suffix.Length -eq 36 -and (Test-ContextId $suffix))
        }
    }
    return $false
}

try {
    if (-not (Test-ContextId $ThreadId) -or -not (Test-ContextId $TurnId)) {
        throw '需要本机当前任务的真实 ThreadId 和 TurnId。请在 Codex 内按说明取得上下文；脚本不会生成 ID。'
    }
    if (-not [string]::IsNullOrWhiteSpace($Endpoint)) {
        $Endpoint = $Endpoint.Trim()
        if (-not (Test-CodexEndpoint $Endpoint)) {
            throw 'Endpoint 必须是 Codex 提供的本机 codex-app-tools 或 codex-browser-use 管道；也可不指定，由桌宠自动发现。'
        }
    }

    $applicationPath = [System.IO.Path]::GetFullPath($ApplicationDirectory)
    $executablePath = Join-Path $applicationPath 'DesktopPet.exe'
    if (-not [System.IO.File]::Exists($executablePath)) {
        throw '程序目录中找不到 DesktopPet.exe。请先构建源码，并将 ApplicationDirectory 指向构建输出目录。'
    }
    $validationPath = Join-Path $applicationPath 'connection-validation.json'
    $writeProbe = Join-Path $applicationPath ('.pairing-write-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try { [System.IO.File]::WriteAllText($writeProbe, '') }
    catch { throw '程序文件夹不可写。请解压到自己的下载、文档或用户目录后重试，无需管理员权限。' }
    finally { if ([System.IO.File]::Exists($writeProbe)) { [System.IO.File]::Delete($writeProbe) } }

    $pairingMutex = New-Object System.Threading.Mutex($false, 'Local\AIPet.CodexPairing')
    try { $ownsPairingMutex = $pairingMutex.WaitOne(0) }
    catch [System.Threading.AbandonedMutexException] { $ownsPairingMutex = $true }
    if (-not $ownsPairingMutex) { throw '另一项桌宠配对正在进行，请等待完成后重试。' }

    # Do not race the interactive application's connection or clear operation.
    $runningPets = @(Get-Process -Name 'DesktopPet' -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq [System.Diagnostics.Process]::GetCurrentProcess().SessionId })
    if ($runningPets.Count -gt 0) { throw '请先从桌宠托盘菜单选择退出，再运行配对。' }
    if ([System.IO.File]::Exists($validationPath)) { [System.IO.File]::Delete($validationPath) }

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $executablePath
    $startInfo.Arguments = '--pair-codex'
    $startInfo.WorkingDirectory = $applicationPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    # Context goes only into this child's environment, never its command line,
    # persistent user/machine environment, stdout, or an unencrypted file.
    $startInfo.EnvironmentVariables['CODEX_THREAD_ID'] = $ThreadId
    $startInfo.EnvironmentVariables['CODEX_TURN_ID'] = $TurnId
    if ([string]::IsNullOrWhiteSpace($Endpoint)) { $startInfo.EnvironmentVariables.Remove('CODEX_APP_TOOLS_PIPE_PATH') }
    else { $startInfo.EnvironmentVariables['CODEX_APP_TOOLS_PIPE_PATH'] = $Endpoint }

    Write-Host '正在验证 Codex 本机进程身份及额度、任务只读接口；不会调用消息读取接口。'
    $child = New-Object System.Diagnostics.Process
    $child.StartInfo = $startInfo
    if (-not $child.Start()) { throw '无法启动桌宠安全配对。' }
    if (-not $child.WaitForExit(90000)) {
        $child.Kill()
        $child.WaitForExit()
        throw '配对超时。请确认 Codex 正在运行且上下文有效；不要关闭安全校验或改用管理员权限。'
    }
    if ($child.ExitCode -ne 0 -or -not [System.IO.File]::Exists($validationPath)) {
        throw '配对验证未通过。请检查 Codex 状态与程序目录中的 connection-validation.json；身份或签名不受信任时会拒绝连接。'
    }
    if ((Get-Item -LiteralPath $validationPath).Length -gt 16384) { throw '配对诊断文件大小异常，未确认成功。' }
    $report = [System.IO.File]::ReadAllText($validationPath) | ConvertFrom-Json
    if ($report.passed -ne $true) { throw '配对诊断没有确认成功，请检查其中的错误代码。' }
    # Do not decrypt, parse, clone, or roll back connection.dat / legacy plaintext.
    # The application saves atomically only after successful validation.
    $exitCode = 0
    Write-Host '配对成功：连接记录已用当前 Windows 用户的 DPAPI 加密保存。'
    Write-Host '现在可以启动 DesktopPet.exe。若此前选择过断开并清除，请在面板中点重新连接。消息正文默认不读取，可在面板单独开启。'
} catch {
    Write-Host ('配对未完成：' + $_.Exception.Message)
} finally {
    if ($child) { $child.Dispose() }
    if ($ownsPairingMutex) { $pairingMutex.ReleaseMutex() }
    if ($pairingMutex) { $pairingMutex.Dispose() }
}
exit $exitCode

