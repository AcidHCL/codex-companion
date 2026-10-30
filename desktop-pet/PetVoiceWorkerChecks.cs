using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

// Exercises ownership and retirement with a stdlib-only Python HTTP fixture.
// No torch, TTS, Ollama, reference audio or user configuration is imported.
internal static class PetVoiceWorkerChecks
{
    internal static int Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<int> RunAsync()
    {
        var checks = new List<string>();
        void Require(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        string folder = Path.Combine(AppContext.BaseDirectory, "voice-worker-fixture-" + Guid.NewGuid().ToString("N"));
        string? error = null; int result = 1; PetVoiceClient? client = null;
        var clock = Stopwatch.StartNew();
        try
        {
            Require(StartupModePolicy.IsIsolatedDiagnostic(new[] { "--voice-worker-lifecycle-test" }), "worker_fixture_uses_isolated_diagnostic_mode");
            Directory.CreateDirectory(folder);
            string script = Path.Combine(folder, "worker.py");
            File.WriteAllText(script, WorkerScript);
            File.WriteAllText(Path.Combine(folder, "config.json"), "{\"backend\":\"faster\"}");
            string python = FindPython();
            int port; using (var listener = new TcpListener(IPAddress.Loopback, 0)) { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
            client = new PetVoiceClient(() => (python, script), port);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await client.PreloadAsync("initial", deadline.Token);
            int first = client.OwnedWorkerProcessId ?? throw new InvalidOperationException("Fixture worker did not start.");
            File.WriteAllText(Path.Combine(folder, "hold-health.once"), "hold");
            using (var cancelHealth = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                Task probe = client.PreloadAsync("cancel-health-reuse", cancelHealth.Token);
                await WaitForFileAsync(Path.Combine(folder, "health-entered"), deadline.Token);
                cancelHealth.Cancel();
                bool cancelled = false;
                try { await probe; } catch (OperationCanceledException) { cancelled = true; }
                File.WriteAllText(Path.Combine(folder, "health-release"), "release");
                Require(cancelled && client.OwnedWorkerProcessId == first && !Gone(first),
                    "cancelling_reuse_health_probe_does_not_kill_existing_warm_worker");
            }
            await client.UnloadAsync(deadline.Token);
            Require(client.OwnedWorkerProcessId == null && Gone(first), "manual_unload_waits_until_exact_owned_worker_has_exited");

            await client.PreloadAsync("second", deadline.Token);
            int second = client.OwnedWorkerProcessId!.Value;
            Require(second != first, "request_after_unload_starts_a_fresh_owned_worker");
            Task release = client.ReleaseAsync();
            Task reload = client.PreloadAsync("after-release", deadline.Token);
            await Task.WhenAll(release, reload);
            Require(Gone(second) && client.OwnedWorkerProcessId is { } afterRelease && afterRelease != second,
                "release_and_next_load_serialize_worker_replacement");

            int previous = client.OwnedWorkerProcessId!.Value;
            await client.PreloadAsync("retire-header", deadline.Token);
            Require(Gone(previous) && client.OwnedWorkerProcessId != previous,
                "known_retiring_response_retries_once_before_audio_or_preload_results");

            previous = client.OwnedWorkerProcessId!.Value;
            await client.PreloadAsync("retire-socket", deadline.Token);
            Require(Gone(previous) && client.OwnedWorkerProcessId != previous,
                "owned_worker_exit_before_headers_retries_with_fresh_process");

            previous = client.OwnedWorkerProcessId!.Value;
            int chunks = 0;
            await foreach (var chunk in client.StreamAsync("fixed synthetic text", "retire-stream", deadline.Token)) chunks++;
            Require(chunks == 1 && Gone(previous) && client.OwnedWorkerProcessId != previous,
                "stream_retirement_before_headers_retries_without_duplicate_pcm");

            previous = client.OwnedWorkerProcessId!.Value;
            bool rejected = false; chunks = 0;
            try { await foreach (var chunk in client.StreamAsync("fixed synthetic text", "fail-after-audio", deadline.Token)) chunks++; }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && chunks == 1 && client.OwnedWorkerProcessId == previous && CountRequests(folder, "fail-after-audio") == 1,
                "stream_failure_after_first_pcm_never_retries_or_repeats_audio");

            rejected = false;
            try { await client.PreloadAsync("gpu-busy", deadline.Token); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected && client.OwnedWorkerProcessId == previous && CountRequests(folder, "gpu-busy") == 1,
                "ordinary_gpu_busy_error_does_not_restart_or_retry_worker");

            await client.PreloadAsync("retire-on-health", deadline.Token);
            previous = client.OwnedWorkerProcessId!.Value;
            await client.PreloadAsync("after-health-retirement", deadline.Token);
            Require(Gone(previous) && client.OwnedWorkerProcessId != previous,
                "health_retiring_flag_prevents_reuse_during_exit_grace_period");
            previous = client.OwnedWorkerProcessId!.Value;
            var customProfile = new PetVoiceProfile("custom-voice", "Serena", "Chinese", "温柔自然");
            await client.SelectProfileAsync(customProfile, deadline.Token);
            Require(Gone(previous) && client.OwnedWorkerProcessId == null,
                "changing_voice_profile_releases_previous_worker_without_loading_the_next_model");
            await client.PreloadAsync("custom-profile", deadline.Token);
            var profileHealth = await client.ReadHealthAsync(deadline.Token);
            Require(profileHealth?.Model == "custom-voice" && profileHealth.Loaded,
                "new_worker_receives_immutable_custom_voice_profile_over_stdin");
            previous = client.OwnedWorkerProcessId!.Value;
            await client.SelectProfileAsync(customProfile, deadline.Token);
            Require(client.OwnedWorkerProcessId == previous,
                "selecting_unchanged_voice_profile_keeps_warm_worker");
            await client.SelectProfileAsync(new("voice-design", "Vivian", "Chinese", "清亮自信的女声"), deadline.Token);
            Require(Gone(previous), "voice_design_switch_awaits_custom_voice_worker_exit");
            await client.PreloadAsync("designed-profile", deadline.Token);
            Require((await client.ReadHealthAsync(deadline.Token))?.Model == "voice-design",
                "voice_design_profile_is_used_by_replacement_worker");
            previous = client.OwnedWorkerProcessId!.Value;
            await client.SelectProfileAsync(new("gpt-fixed", "Vivian", "Chinese", "unused original design"), deadline.Token);
            Require(Gone(previous), "fixed_gpt_voice_switch_awaits_design_worker_exit");
            chunks = 0;
            await foreach (var chunk in client.StreamAsync("新的固定音色台词", "gpt-fixed-stream", deadline.Token)) chunks++;
            Require(chunks == 1 && (await client.ReadHealthAsync(deadline.Token))?.Model == "gpt-fixed",
                "fixed_gpt_voice_sends_dedicated_wire_id_and_no_design_instruction");
            previous = client.OwnedWorkerProcessId!.Value;
            await client.SelectProfileAsync(new("saved-voice", "Vivian", "Chinese", "", "abcdef0123456789abcdef0123456789"), deadline.Token);
            Require(Gone(previous), "saved_voice_switch_releases_previous_reference_worker");
            chunks = 0;
            await foreach (var chunk in client.StreamAsync("我的固定音色", "saved-voice-stream", deadline.Token)) chunks++;
            Require(chunks == 1 && (await client.ReadHealthAsync(deadline.Token))?.Model == "saved-voice",
                "saved_voice_passes_reference_id_over_stdin_and_correct_authenticated_wire_id");
            previous = client.OwnedWorkerProcessId!.Value;
            client.Dispose();
            Require(Gone(previous), "dispose_waits_for_owned_worker_exit_without_touching_other_processes");
            using (var stopEntered = new ManualResetEventSlim())
            using (var allowStop = new ManualResetEventSlim())
            {
                int stopCalls = 0;
                client = new PetVoiceClient(() => (python, script), port, () =>
                {
                    if (Interlocked.Increment(ref stopCalls) != 1) return;
                    stopEntered.Set();
                    if (!allowStop.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Stop fixture gate timed out.");
                });
                await client.PreloadAsync("dispose-race", deadline.Token);
                previous = client.OwnedWorkerProcessId!.Value;
                Task releasing = client.ReleaseAsync();
                try
                {
                    Require(await Task.Run(() => stopEntered.Wait(TimeSpan.FromSeconds(3))), "release_fixture_reaches_detached_owned_worker_stop");
                    Task disposing = Task.Run(client.Dispose);
                    await Task.Delay(100, deadline.Token);
                    Require(!disposing.IsCompleted, "dispose_joins_an_inflight_release_instead_of_returning_when_worker_field_is_null");
                    allowStop.Set();
                    await Task.WhenAll(releasing, disposing).WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
                    Require(Gone(previous) && client.OwnedWorkerProcessId == null, "release_dispose_race_leaves_no_owned_worker_alive");
                }
                finally { allowStop.Set(); }
            }
            result = 0;
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally { client?.Dispose(); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "voice-worker-lifecycle-validation.json"), JsonSerializer.Serialize(new
        {
            passed = result == 0, assertions = checks.Count, checks, error, elapsedSeconds = clock.Elapsed.TotalSeconds,
            workerFixture = "stdlib_only_fake_http", realModelRequests = 0, gpuModelLoads = 0,
            realAccountRequests = 0, realAudioOutput = false, microphoneOpened = false, userSettingsWrites = 0
        }, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }

    private static bool Gone(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
    private static int CountRequests(string folder, string id) => File.ReadAllLines(Path.Combine(folder, id + ".requests")).Length;
    private static async Task WaitForFileAsync(string path, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        while (!File.Exists(path)) await Task.Delay(10, timeout.Token);
    }
    private static string FindPython()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            foreach (string name in new[] { "deepseek-asr-venv", "deepseek-voice-venv", "deepseek-voice-fast-venv" })
            {
                string path = Path.Combine(directory.FullName, ".tools", name, "Scripts", "python.exe");
                if (File.Exists(path)) return path;
            }
        throw new InvalidOperationException("未找到供无模型进程测试使用的 Python。");
    }

    private const string WorkerScript = """
import argparse, base64, ctypes, hmac, json, os, sys, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--port',type=int);p.add_argument('--parent-pid',type=int);a=p.parse_args()
token=sys.stdin.readline().strip(); profile=json.loads(sys.stdin.readline() or '{}'); root=Path(__file__).parent; state={'retiring':False,'loaded':False}
def watch_parent():
    k=ctypes.WinDLL('kernel32',use_last_error=True)
    k.OpenProcess.argtypes=[ctypes.c_ulong,ctypes.c_int,ctypes.c_ulong];k.OpenProcess.restype=ctypes.c_void_p
    k.WaitForSingleObject.argtypes=[ctypes.c_void_p,ctypes.c_ulong]
    h=k.OpenProcess(0x100000,False,a.parent_pid)
    if not h: os._exit(0)
    k.WaitForSingleObject(h,0xffffffff);os._exit(0)
threading.Thread(target=watch_parent,daemon=True).start()
def retire():
    state['retiring']=True
    threading.Thread(target=lambda:(time.sleep(.25),os._exit(0)),daemon=True).start()
class H(BaseHTTPRequestHandler):
    def log_message(self,*args):pass
    def reply(self,code,data):
        b=json.dumps(data).encode();self.send_response(code);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(b)));self.end_headers();self.wfile.write(b)
    def auth(self):
        if not hmac.compare_digest(self.headers.get('X-AIPet-Voice-Token',''),token):self.reply(401,{});return False
        return True
    def do_GET(self):
        if not self.auth():return
        hold=root/'hold-health.once'
        if hold.exists():
            hold.unlink();(root/'health-entered').write_text('entered')
            until=time.monotonic()+5
            while not (root/'health-release').exists() and time.monotonic()<until:time.sleep(.01)
        self.reply(200,{'status':'unloading' if state['retiring'] else 'ready','retiring':state['retiring'],'loaded':state['loaded'],'model':profile.get('model_id','base')})
    def do_POST(self):
        if not self.auth():return
        body=json.loads(self.rfile.read(int(self.headers['Content-Length'])));rid=body.get('request_id','control')
        if rid != 'control':
            with (root/(rid+'.requests')).open('a') as f:f.write('request\n')
        if self.path=='/unload':self.reply(202,{'status':'unloading'});retire();return
        if self.path in ('/retention','/cancel'):self.reply(200,{});return
        if rid=='gpu-busy':self.reply(503,{'error':'gpu_busy'});return
        if rid in ('retire-header','retire-socket','retire-stream'):
            marker=root/(rid+'.once')
            if not marker.exists():
                marker.write_text('once')
                if rid=='retire-socket':os._exit(0)
                state['retiring']=True;self.reply(409,{'error':'retiring'});retire();return
        if self.path=='/preload':
            state['loaded']=True
            if rid=='retire-on-health':state['retiring']=True
            self.reply(200,{'status':'ready'})
            if rid=='retire-on-health':retire()
            return
        if self.path=='/v1/audio/stream':
            if rid=='saved-voice-stream' and (body.get('voice')!='saved-voice:abcdef0123456789abcdef0123456789' or profile.get('reference_id')!='abcdef0123456789abcdef0123456789'):
                self.reply(400,{'error':'incorrect_saved_reference'});return
            if rid=='gpt-fixed-stream' and (body.get('voice')!='gpt-dragon-fixed-v1' or profile.get('instruct')):
                self.reply(400,{'error':'incorrect_fixed_voice_profile'});return
            self.send_response(200);self.send_header('Content-Type','application/x-ndjson');self.end_headers()
            for item in ({'type':'format','sample_rate':24000,'channels':1,'sample_format':'s16le'}, {'type':'audio','data':base64.b64encode(bytes(8)).decode()}):
                self.wfile.write((json.dumps(item)+'\n').encode());self.wfile.flush()
            if rid!='fail-after-audio':self.wfile.write(b'{"type":"done"}\n');self.wfile.flush()
            self.close_connection=True;return
        self.reply(404,{})
s=ThreadingHTTPServer(('127.0.0.1',a.port),H);s.daemon_threads=True;s.serve_forever()
""";
}
