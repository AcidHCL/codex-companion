using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace DesktopPet;

// Uses only a fresh temporary folder and fabricated values. The application
// singleton/logger is not started, and no real user log is read or written.
internal static class LifecycleLogChecks
{
    internal static object Run()
    {
        string parent = Path.GetFullPath(Path.GetTempPath());
        string directory = Path.Combine(parent, "aipet-lifecycle-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Lifecycle check failed: " + label);
            checks.Add(label);
        }
        try
        {
            const int limit = 4096;
            var context = new LifecycleContext(12345, 2, 54321, 1, 8, 1, 0, true, false);
            var sink = new LifecycleLogSink(directory, context, limit);
            string current = Path.Combine(directory, "lifecycle.jsonl");
            string previous = Path.Combine(directory, "lifecycle.previous.jsonl");
            Check(!sink.Write((LifecycleEvent)int.MaxValue), "undefined event is refused");
            Check(!sink.Write(LifecycleEvent.Startup, new LifecycleFailure((LifecycleFailureKind)int.MaxValue, 0)), "undefined failure category is refused");
            Check(!File.Exists(current), "refused records do not create a log");
            Check(sink.Write(LifecycleEvent.Startup), "startup serializes");
            using (var json = JsonDocument.Parse(File.ReadLines(current).Single()))
            {
                var row = json.RootElement;
                Check(row.GetProperty("eventName").GetString() == "Startup", "fixed event name is present");
                Check(row.GetProperty("pid").GetInt32() == 12345 && row.GetProperty("session").GetInt32() == 2 && row.GetProperty("parentPid").GetInt32() == 54321, "numeric process and session identifiers round-trip");
                Check(row.GetProperty("version").EnumerateArray().Select(n => n.GetInt32()).SequenceEqual(new[] { 1, 8, 1, 0 }), "numeric version round-trips");
                Check(row.GetProperty("recovery").GetBoolean() && !row.GetProperty("compatibility").GetBoolean(), "recovery flags round-trip");
                Check(row.GetProperty("utc").GetDateTimeOffset().Offset == TimeSpan.Zero, "timestamp is UTC");
            }
            string secret = "do-not-log: private-message token pipe caller-context";
            Exception hostile = new HostileException();
            var examples = new Exception[] { hostile, new IOException(secret), new TimeoutException(secret),
                new InvalidOperationException(secret), new COMException(secret, GraphicsFailure.RenderThreadFailure) };
            foreach (Exception exception in examples)
                Check(sink.Write(LifecycleEvent.ConnectionRetry, LifecycleLog.Classify(exception)), "exception category is serializable without message: " + LifecycleLog.Classify(exception).Kind);
            string text = File.ReadAllText(current);
            Check(!text.Contains(secret, StringComparison.Ordinal) && !text.Contains(nameof(HostileException), StringComparison.Ordinal) && !text.Contains(directory, StringComparison.Ordinal), "no messages, dynamic exception names or output paths are serialized");
            Check(LifecycleLog.Classify(hostile).Kind == LifecycleFailureKind.Other, "unknown exception uses fixed Other category");
            Check(LifecycleLog.Classify(examples[^1]).Kind == LifecycleFailureKind.Graphics, "fatal graphics error retains a fixed category");
            Check(typeof(LifecycleLog).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Where(method => method.Name is "Record" or "RecordException" or "Classify" or "Start")
                .SelectMany(method => method.GetParameters()).All(parameter => parameter.ParameterType != typeof(string)),
                "runtime logging API accepts no arbitrary text");

            Parallel.For(0, 200, index => sink.Write(LifecycleEvent.ConnectionRecovered, code: index));
            var files = Directory.GetFiles(directory);
            Check(files.Length == 2 && File.Exists(current) && File.Exists(previous), "rotation retains only current and previous files");
            Check(files.All(path => new FileInfo(path).Length <= limit) && files.Sum(path => new FileInfo(path).Length) <= limit * 2, "both files and total size stay bounded");
            long last = 0;
            int validRows = 0;
            string[] allowed = { "schema", "utc", "sequence", "eventName", "pid", "session", "parentPid", "version", "recovery", "compatibility", "failure", "hresult", "code" };
            foreach (string path in new[] { previous, current })
                foreach (string line in File.ReadLines(path))
                {
                    using var json = JsonDocument.Parse(line);
                    var row = json.RootElement;
                    long sequence = row.GetProperty("sequence").GetInt64();
                    if (last != 0 && sequence != last + 1) throw new InvalidOperationException("Non-contiguous serialized sequence.");
                    last = sequence;
                    if (!row.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(allowed.Order()))
                        throw new InvalidOperationException("Unexpected serialized fields.");
                    validRows++;
                }
            Check(validRows > 1 && last == 206, "concurrent writes remain complete ordered JSON records across rotation");

            // Existing oversized files cannot defeat the cap on a later run.
            File.WriteAllBytes(current, new byte[limit * 2]);
            File.WriteAllBytes(previous, new byte[limit * 2]);
            Check(sink.Write(LifecycleEvent.ApplicationExit, code: 0), "oversized prior files recover on write");
            Check(Directory.GetFiles(directory).All(path => new FileInfo(path).Length <= limit), "oversized prior files are not retained");
            using (var json = JsonDocument.Parse(File.ReadLines(current).Single()))
                Check(json.RootElement.GetProperty("code").GetInt32() == 0, "exit code round-trips");

            return new { passed = true, count = checks.Count, checks, userLogsTouched = false,
                productionBytesPerFile = LifecycleLogSink.MaximumFileBytes, retainedFiles = 2 };
        }
        finally
        {
            string target = Path.GetFullPath(directory);
            string root = parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(target).StartsWith("aipet-lifecycle-check-", StringComparison.Ordinal) &&
                (File.GetAttributes(target) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(target, recursive: true);
        }
    }

    private sealed class HostileException : Exception
    {
        public override string Message => throw new InvalidOperationException("Exception messages must not be read by lifecycle logging.");
        public override string ToString() => throw new InvalidOperationException("Exception text must not be read by lifecycle logging.");
    }
}
