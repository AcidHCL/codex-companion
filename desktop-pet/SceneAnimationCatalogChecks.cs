using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace DesktopPet;

internal static class SceneAnimationCatalogChecks
{
    // Exercises import publication and real local paths; never reads or changes the user's animation library.
    public static object Run(string? temporaryParent = null)
    {
        string parent = Path.GetFullPath(temporaryParent ?? Path.GetTempPath());
        string scratch = Path.Combine(parent, "AIPet.SceneChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string? link = null;
        int assertions = 0;
        bool reparseTestAvailable = false;
        void Check(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException("Scene import check: " + message); }
        try
        {
            string source = Path.Combine(scratch, "中文 动画源"), library = Path.Combine(scratch, "library");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(Path.Combine(source, "frames"));
            byte[] png = Png(false), apng = Png(true);
            File.WriteAllBytes(Path.Combine(source, "frames", "01.png"), png);
            File.WriteAllBytes(Path.Combine(source, "frames", "02.png"), png);
            File.WriteAllBytes(Path.Combine(source, "sheet.png"), png);
            File.WriteAllBytes(Path.Combine(source, "loop.apng"), apng);
            File.WriteAllBytes(Path.Combine(source, "demo.webm"), new byte[] { 0x1a, 0x45, 0xdf, 0xa3, 0, 0, 0, 0 });
            File.WriteAllBytes(Path.Combine(source, "demo.mp4"), new byte[] { 0, 0, 0, 24, 102, 116, 121, 112, 105, 115, 111, 109 });
            File.WriteAllBytes(Path.Combine(source, "song.mp3"), new byte[] { 73, 68, 51, 4, 0, 0, 0, 0, 0, 0 });
            File.WriteAllBytes(Path.Combine(source, "sound.wav"), "RIFF0000WAVE"u8.ToArray());
            File.WriteAllBytes(Path.Combine(source, "sound.ogg"), new byte[] { 79, 103, 103, 83, 0, 0 });
            File.WriteAllText(Path.Combine(source, "unreferenced.js"), "throw new Error('must never be copied')");
            string manifest = Path.Combine(source, "fixture.petanim.json");
            string good = """
                {"version":1,"name":"中文动画","skinId":"deepseek","anchorX":0.5,"anchorY":1,"actions":{
                  "working":{"start":{"type":"frames","frames":["frames/01.png"]},"loop":{"type":"frames","frames":["frames/01.png","frames/02.png"],"durationsMs":[90,110]},"end":{"type":"frames","frames":["frames/02.png"]},"repeat":true},
                  "complete":{"loop":{"type":"apng","file":"loop.apng"},"repeat":false},
                  "walk":{"loop":{"type":"spritesheet","file":"sheet.png","frameWidth":1,"frameHeight":1,"frameCount":1,"fps":12}},
                  "walk-left":{"loop":{"type":"frames","frames":["frames/01.png"],"durationsMs":[120]}},
                  "walk-right":{"loop":{"type":"frames","frames":["frames/02.png"],"durationsMs":[90]}},
                  "jump":{"loop":{"type":"video","file":"demo.webm"},"repeat":false},
                  "fall":{"loop":{"type":"video","file":"demo.mp4"},"repeat":false}
                }}
                """;
            File.WriteAllText(manifest, good);
            var catalog = new SceneAnimationCatalog(library);
            var preview = catalog.Validate(manifest, "fixture");
            Check(!Directory.Exists(library) && preview.Files.Count == 7 && preview.Actions.Count == 7, "Read-only validation must find all four media types and only referenced files.");
            Check(preview.Actions["working"].Start != null && preview.Actions["working"].End != null && !preview.Actions["complete"].Repeat, "Start/loop/end and finite complete action must survive parsing.");
            Check(preview.Actions["walk-left"].Loop.Frames[0] == "frames/01.png" && preview.Actions["walk-right"].Loop.Frames[0] == "frames/02.png" && preview.Actions.ContainsKey("walk"), "Directional walking must keep its own frames while legacy walk remains supported.");
            var first = catalog.ImportAsync(manifest).GetAwaiter().GetResult();
            var second = catalog.ImportAsync(manifest).GetAwaiter().GetResult();
            Check(first.Id != second.Id && File.ReadAllText(first.ManifestPath) == good && !File.Exists(Path.Combine(first.RootDirectory, "unreferenced.js")), "Atomic imports must preserve bytes, never overwrite, and exclude unreferenced scripts.");
            var loaded = catalog.Load(out var issues);
            Check(issues.Count == 0 && loaded.Count == 2 && loaded.Any(p => p.Id == first.Id), "Stable imported IDs must reload cleanly.");
            Check(catalog.GetValidatedFiles(first).SequenceEqual(preview.Files), "Renderer allowlist must match the inspected resource graph.");
            void Reject(string bad, string label)
            {
                File.WriteAllText(manifest, bad);
                bool rejected = false;
                try { catalog.ImportAsync(manifest).GetAwaiter().GetResult(); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, label);
                Check(catalog.Load(out _).Count == 2 && !Directory.EnumerateDirectories(library, ".import-*").Any(), "Rejected import must not publish a partial package or alter existing packs.");
            }
            string singing = good.Replace("\"walk-left\":{", "\"sing\":{\"audio\":{\"file\":\"song.mp3\"},\"thoughts\":{\"title\":\"DeepSeek 正在思考…\",\"lines\":[\"Okay.\",\"<b>plain text</b>\"]},", StringComparison.Ordinal);
            File.WriteAllText(manifest, singing);
            var singingPack = catalog.Validate(manifest, "singing");
            Check(singingPack.Actions["sing"].Audio?.File == "song.mp3" && singingPack.Files.Count == 8 && singingPack.Actions["sing"].Thoughts?.Lines[1] == "<b>plain text</b>", "Audio is a local allowlisted resource and thoughts are bounded plain text.");
            foreach (string supportedAudio in new[] { "sound.wav", "sound.ogg" })
            {
                File.WriteAllText(manifest, singing.Replace("song.mp3", supportedAudio, StringComparison.Ordinal));
                Check(catalog.Validate(manifest, "audio-header").Actions["sing"].Audio?.File == supportedAudio, "Accept supported local audio header: " + supportedAudio);
            }
            foreach (string unsafeAudio in new[] { "../song.mp3", "https://example.invalid/song.mp3", "song.mp3?x", "unreferenced.js", "frames/01.png", "missing.mp3" })
                Reject(singing.Replace("song.mp3", unsafeAudio, StringComparison.Ordinal), "Reject unsafe audio resource: " + unsafeAudio);
            Reject(singing.Replace("\"file\":\"song.mp3\"", "\"file\":\"song.mp3\",\"autoplayScript\":\"bad\"", StringComparison.Ordinal), "Audio cannot carry executable or unknown fields.");
            Reject(singing.Replace("\"Okay.\"", "\"line\\nline\"", StringComparison.Ordinal), "Thought lines cannot inject control characters.");
            Reject(singing.Replace("\"Okay.\"", "\"" + new string('x', 121) + "\"", StringComparison.Ordinal), "Thought line size is bounded.");
            using (var oversizedAudio = File.OpenWrite(Path.Combine(source, "song.mp3"))) oversizedAudio.SetLength(32L * 1024 * 1024 + 1);
            Reject(singing, "Oversized audio cannot be copied into the library.");
            File.WriteAllText(Path.Combine(source, "song.mp3"), "<script>wrong audio header</script>");
            Reject(singing, "Mislabeled audio files cannot enter the renderer allowlist.");
            File.WriteAllBytes(Path.Combine(source, "song.mp3"), new byte[] { 73, 68, 51, 4, 0, 0, 0, 0, 0, 0 });
            foreach (string unsafePath in new[] { "../outside.png", "C:/outside.png", "https://example.invalid/image.png", "%2e%2e/outside.png", "frames/NUL.png", "script.js", "frames/missing.png" })
                Reject(good.Replace("frames/01.png", unsafePath, StringComparison.Ordinal), "Reject unsafe/missing reference: " + unsafePath);
            Reject(good.Replace("\"version\":1", "\"version\":1,\"Version\":1", StringComparison.Ordinal), "Case-insensitive duplicate keys must be rejected.");
            Reject(good.Replace("\"fps\":12", "\"fps\":61", StringComparison.Ordinal), "Excessive FPS must be rejected.");
            Reject(good.Replace("\"frameCount\":1", "\"frameCount\":2", StringComparison.Ordinal), "Sprite frames may not extend outside the sheet.");
            Reject(good.Replace("\"durationsMs\":[90,110]", "\"durationsMs\":[0,110]", StringComparison.Ordinal), "Zero duration frames must be rejected.");
            Reject(good.Replace("\"anchorX\":0.5", "\"anchorX\":1.5", StringComparison.Ordinal), "Out-of-range anchor must be rejected.");
            Reject(good.Replace("\"skinId\":\"deepseek\"", "\"skinId\":\"*\"", StringComparison.Ordinal), "Skin wildcard must not silently attach animations to another character.");
            Reject(good.Replace("\"working\":", "\"arbitrary-script\":", StringComparison.Ordinal), "Unknown scene semantics must be rejected.");
            Reject(good.Replace("\"walk-left\":", "\"walk-up\":", StringComparison.Ordinal), "Directional walking must not permit arbitrary scene names.");
            byte[] badDimensions = png.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(badDimensions.AsSpan(16, 4), 0xffffffff);
            File.WriteAllBytes(Path.Combine(source, "sheet.png"), badDimensions);
            Reject(good, "Oversized PNG headers must be rejected before decoding.");
            File.WriteAllBytes(Path.Combine(source, "sheet.png"), png);
            File.WriteAllBytes(Path.Combine(source, "loop.apng"), png);
            Reject(good, "A static PNG cannot pass as animated APNG.");
            File.WriteAllBytes(Path.Combine(source, "loop.apng"), apng);
            File.WriteAllText(manifest, good);
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); bool cancelled = false;
                try { catalog.ImportAsync(manifest, cancel.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && catalog.Load(out _).Count == 2, "Pre-cancelled import must leave library intact.");
            }
            string external = Path.Combine(scratch, "external"); Directory.CreateDirectory(external);
            File.WriteAllBytes(Path.Combine(external, "01.png"), png);
            link = Path.Combine(source, "linked");
            try { Directory.CreateSymbolicLink(link, external); reparseTestAvailable = true; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException) { link = null; }
            if (reparseTestAvailable) Reject(good.Replace("frames/01.png", "linked/01.png", StringComparison.Ordinal), "Directory symlink resources must be rejected.");
            Check(File.ReadAllText(first.ManifestPath) == good && File.ReadAllText(second.ManifestPath) == good, "All failed operations must preserve existing imported bytes.");
            File.WriteAllText(manifest, singing);
            var importedSinging = catalog.ImportAsync(manifest).GetAwaiter().GetResult();
            Check(File.ReadAllBytes(Path.Combine(importedSinging.RootDirectory, "song.mp3")).SequenceEqual(File.ReadAllBytes(Path.Combine(source, "song.mp3"))) &&
                !File.Exists(Path.Combine(importedSinging.RootDirectory, "sound.wav")) && !File.Exists(Path.Combine(importedSinging.RootDirectory, "sound.ogg")), "Audio import copies only the referenced bytes and does not include unrelated sounds.");
            var builtIn = BundledSceneAnimations.FindForSkin("deepseek") ?? throw new InvalidOperationException("Scene import check: packaged DeepSeek animation is missing.");
            Check(BundledSceneAnimations.IsBuiltIn(builtIn.Id) && !BundledSceneAnimations.IsBuiltIn("builtin-deepseek-small-other") && !BundledSceneAnimations.IsBuiltIn(first.Id), "Only the exact reserved DeepSeek package ID is built-in.");
            Check(BundledSceneAnimations.FindForSkin("claude") == null && BundledSceneAnimations.FindForSkin("deepseek/../claude") == null &&
                BundledSceneAnimations.FindForSkin("gpt", BundledSceneAnimations.DeepSeekId) == null &&
                BundledSceneAnimations.FindForSkin("deepseek", BundledSceneAnimations.GptId) == null,
                "Bundled defaults are explicitly mapped to their character and cannot attach another character's reserved pack.");
            var gpt = BundledSceneAnimations.FindForSkin("gpt") ?? throw new InvalidOperationException("Scene import check: packaged GPT animation is missing.");
            Check(gpt.Id == BundledSceneAnimations.GptId && BundledSceneAnimations.IsBuiltIn(gpt.Id) &&
                !BundledSceneAnimations.IsDeepSeekBuiltIn(gpt.Id) && gpt.Actions.Count == 17 && gpt.Files.Count == 121,
                "GPT has its own 17-action built-in package with 120 PNG frames and no borrowed DeepSeek preference.");
            var gptFrames = gpt.Actions.Values.SelectMany(action => new[] { action.Start, action.Loop, action.End }
                .OfType<SceneAnimationClip>().SelectMany(clip => clip.Frames)).ToArray();
            Check(gptFrames.Length == 120 && gptFrames.Distinct(StringComparer.Ordinal).Count() == 120 && gptFrames.All(relative =>
            {
                byte[] data = File.ReadAllBytes(SceneFileAccess.ChildPath(gpt.RootDirectory, relative));
                return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4)) == 512 && BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4)) == 512 && data[25] == 6;
            }), "GPT uses 120 distinct 512-square RGBA frame resources in the classic animation format.");
            Check(gpt.Actions.All(entry => entry.Value.Audio == null && entry.Value.Thoughts == null) &&
                PetPerformanceCatalog.Installed(gpt).OrderBy(name => name).SequenceEqual(new[] { "angry", "bashful", "happy", "startled" }),
                "GPT advertises only its four expressions, without songs or unavailable independent performances.");
            Check(gpt.Actions.All(entry =>
            {
                if (!builtIn.Actions.TryGetValue(entry.Key, out var legacyAction) || entry.Value.Repeat != legacyAction.Repeat) return false;
                var actualClips = new[] { entry.Value.Start, entry.Value.Loop, entry.Value.End };
                var expectedClips = new[] { legacyAction.Start, legacyAction.Loop, legacyAction.End };
                return actualClips.Zip(expectedClips).All(pair => pair.First == null ? pair.Second == null :
                    pair.Second != null && pair.First.Type == "frames" && pair.First.Frames.Length == pair.Second.Frames.Length &&
                    pair.First.DurationsMs != null && pair.First.DurationsMs.SequenceEqual(pair.Second.DurationsMs ?? Array.Empty<int>()));
            }),
                "GPT preserves classic per-action frame counts, explicit timing, phases and repeat behavior.");
            var gptMotion = DeepSeekMotionTimeline.ForPack(gpt);
            Check(!gptMotion.IsLegacy && gptMotion.StandingFoot != null && gptMotion.Clips.Count == 6 &&
                gptMotion.Clips.All(pair => pair.Value.FrameCount == 6 && pair.Value.DurationsMs.SequenceEqual(gpt.Actions[pair.Key].Loop.DurationsMs!)),
                "GPT scripted movement uses its own calibrated six-frame contact metadata.");
            var gptSelection = new DesktopSettings { SkinId = "gpt", ScenePackId = gpt.Id, DeepSeekAnimationPackId = BundledSceneAnimations.DeepSeekRedrawnId };
            gptSelection.Normalize();
            Check(gptSelection.ScenePackId == gpt.Id && gptSelection.DeepSeekAnimationPackId == BundledSceneAnimations.DeepSeekRedrawnId,
                "Normalizing GPT selection retains the separate DeepSeek animation-version preference.");
            string[] newActions = { "happy", "angry", "startled", "bashful", "jump-left", "jump-right", "climb-left", "climb-right", "climb-top-left", "climb-top-right" };
            string[] gifActions = { "token-come", "bowl-tap", "bowl-tap-eager", "dance", "dance-2", "dance-3", "fireworks" };
            Check(builtIn.Files.Count == 337 && builtIn.Actions.Count == 26 && builtIn.Actions.Where(entry => entry.Key != "bangyou-dance").All(entry => entry.Value.Loop.Type == "frames" && entry.Value.Loop.Frames.Length == (gifActions.Contains(entry.Key) ? 12 : newActions.Contains(entry.Key) ? 6 : 8)), "Published DeepSeek preserves all existing artwork and adds 122 Bangyou frames and its separate audio resource.");
            var bangyou = builtIn.Actions["bangyou-dance"];
            Check(bangyou.Start is { Type: "frames", Frames.Length: 87 } && bangyou.Loop is { Type: "frames", Frames.Length: 35 } && bangyou.End == null && !bangyou.Repeat,
                "Bangyou plays the complete source once, then repeats only its continuous final dance segment until audio ends.");
            var bangyouClips = new[] { bangyou.Start!, bangyou.Loop };
            var bangyouFrames = bangyouClips.SelectMany(clip => clip.Frames).ToArray();
            Check(bangyouFrames.SequenceEqual(Enumerable.Range(1, 122).Select(index => $"frames/bangyou-dance/{index:000}.png")) &&
                bangyouClips.All(clip => clip.DurationsMs?.Length == clip.Frames.Length && clip.DurationsMs.All(ms => ms == 80)),
                "Bangyou keeps all 122 original poses in source order at 80 ms each, without generated interpolation.");
            Check(builtIn.Actions["sing"].Audio?.File.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) == true && builtIn.Actions["sing"].Thoughts?.Lines.Length > 0 &&
                bangyou.Audio?.File == "audio/bangyou-de-jiu.mp3" && bangyou.Thoughts == null &&
                builtIn.Actions.Where(entry => !PetPerformanceCatalog.IsMusicPerformance(entry.Key)).All(entry => entry.Value.Audio == null && entry.Value.Thoughts == null),
                "Only the two musical performances have music; only Let Me Go has its thinking panel.");
            Check(builtIn.Actions["drag"].Start is { Type: "frames", Frames.Length: 4 } && builtIn.Actions["drag"].Repeat && builtIn.Actions["drag"].End == null, "Dragging must play a four-frame lift once before the eight-frame struggle loop, and release immediately without an end segment.");
            string[] oneShotActions = { "land", "happy", "angry", "startled", "bashful", "jump-left", "jump-right", "climb-top-left", "climb-top-right" };
            Check(builtIn.Actions.All(entry => entry.Value.Repeat == !(oneShotActions.Contains(entry.Key) || gifActions.Contains(entry.Key) || entry.Key == "bangyou-dance")), "Reactions, GIF performances, jump and crest previews play once; Bangyou ends with its music, while ordinary states and climbing loop.");
            Check(gifActions.All(id => builtIn.Actions[id].Loop.DurationsMs is { Length: 12 } times && times.Sum() >= 1000 && times.Sum() <= 8000), "Each GIF performance has twelve timed frames and a bounded one-shot duration.");
            Check(newActions.All(id => builtIn.Actions[id].Loop.DurationsMs is { Length: 6 }), "Every new movement/reaction preserves its six explicit source frame durations.");
            Check(newActions.Where(id => id.StartsWith("jump-", StringComparison.Ordinal)).All(id => builtIn.Actions[id].Loop.DurationsMs!.Sum() == 850) &&
                newActions.Where(id => id.StartsWith("climb-top-", StringComparison.Ordinal)).All(id => builtIn.Actions[id].Loop.DurationsMs!.Sum() == 1460) &&
                new[] { "climb-left", "climb-right" }.All(id => builtIn.Actions[id].Loop.DurationsMs!.Sum() == 820), "Directional physical animation timing matches the reviewed source.");
            foreach (string id in newActions.Concat(gifActions).Append("bangyou-dance"))
            {
                Check(new[] { builtIn.Actions[id].Start, builtIn.Actions[id].Loop, builtIn.Actions[id].End }.OfType<SceneAnimationClip>().SelectMany(clip => clip.Frames).All(relative =>
                {
                    byte[] data = File.ReadAllBytes(SceneFileAccess.ChildPath(builtIn.RootDirectory, relative));
                    return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4)) == 512 && BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4)) == 512 && data[25] == 6;
                }), "New action keeps the common 512-square RGBA canvas: " + id);
            }
            Check(builtIn.Actions["fall"].Loop.DurationsMs!.Sum() == 720 && builtIn.Actions["land"].Loop.DurationsMs!.Sum() == 760, "Fall and landing timings are preserved in the bundled package.");
            Check(builtIn.Actions["idle"].Loop.DurationsMs!.Sum() == 2820 && builtIn.Actions["working"].Loop.DurationsMs!.Sum() == 880 && builtIn.Actions["walk-left"].Loop.DurationsMs!.Sum() == 840 && builtIn.Actions["walk-right"].Loop.DurationsMs!.Sum() == 840, "Packaged state timing must preserve the selected v1.3 animations.");
            string leftFrame = SceneFileAccess.ChildPath(builtIn.RootDirectory, builtIn.Actions["walk-left"].Loop.Frames[0]);
            string rightFrame = SceneFileAccess.ChildPath(builtIn.RootDirectory, builtIn.Actions["walk-right"].Loop.Frames[0]);
            Check(leftFrame != rightFrame && !File.ReadAllBytes(leftFrame).SequenceEqual(File.ReadAllBytes(rightFrame)), "Left and right walking must reference separate direction-specific artwork.");
            return new { passed = true, offline = true, assertions, reparseTestAvailable, realUserLibraryTouched = false,
                formats = new[] { "PNG frames", "APNG", "spritesheet", "video header/allowlist", "audio header/allowlist", "bounded thought text" },
                builtInStates = builtIn.Actions.Keys.ToArray(),
                note = "Video decoding and actual codec support are checked separately by the isolated player." };
        }
        finally
        {
            if (link != null && Directory.Exists(link)) Directory.Delete(link, false);
            Live2DModelCatalog.CleanupOwnedDirectory(scratch, parent, "AIPet.SceneChecks-");
        }
    }

    private static byte[] Png(bool animate)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] ihdr = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), 1); BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), 1); ihdr[8] = 8; ihdr[9] = 6;
        Chunk(output, "IHDR", ihdr);
        if (animate)
        {
            byte[] actl = new byte[8]; BinaryPrimitives.WriteUInt32BigEndian(actl.AsSpan(0, 4), 2); Chunk(output, "acTL", actl);
            Chunk(output, "fcTL", Control(0));
        }
        Chunk(output, "IDAT", CompressedPixel(255, 0, 0));
        if (animate)
        {
            Chunk(output, "fcTL", Control(1));
            byte[] compressed = CompressedPixel(0, 0, 255), frame = new byte[4 + compressed.Length];
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), 2); compressed.CopyTo(frame, 4); Chunk(output, "fdAT", frame);
        }
        Chunk(output, "IEND", Array.Empty<byte>()); return output.ToArray();
    }
    private static byte[] Control(uint sequence)
    {
        byte[] result = new byte[26]; BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4, 4), 1); BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(20, 2), 1); BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(22, 2), 10); return result;
    }
    private static byte[] CompressedPixel(byte red, byte green, byte blue)
    {
        using var output = new MemoryStream();
        using (var zip = new ZLibStream(output, CompressionLevel.Fastest, true)) zip.Write(new byte[] { 0, red, green, blue, 255 });
        return output.ToArray();
    }
    private static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(size, (uint)data.Length); stream.Write(size);
        byte[] kind = Encoding.ASCII.GetBytes(type); stream.Write(kind); stream.Write(data);
        uint crc = 0xffffffff;
        foreach (byte value in kind.Concat(data)) { crc ^= value; for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1; }
        BinaryPrimitives.WriteUInt32BigEndian(size, ~crc); stream.Write(size);
    }
}
