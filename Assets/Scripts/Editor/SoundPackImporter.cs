using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using PoDecath.Audio;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// Fills the <see cref="AudioBank"/> from a folder of real recordings — written for the Sonniss GDC
    /// Game Audio Bundle (royalty-free, commercial use, no attribution required), and working on any folder
    /// of WAV files with descriptive names.
    ///
    /// The bundle is tens of gigabytes of professionally named files, which is exactly what makes this
    /// scriptable: "Stadium Crowd Ambience Loop", "Starter Pistol Single Shot", "Footstep Concrete Run 03".
    /// Each bank slot has a rule — words that must appear, words that disqualify, and the length the slot
    /// wants — and the best-scoring file per slot is taken. Then each file is made into what
    /// <c>DOCS/AUDIO_REPLACEMENT.md</c> asks for, because a raw library file is not: 96 kHz 24-bit stereo
    /// with half a second of silence before the hit is the norm. So every pick is
    ///
    ///  - down-mixed to mono where the sound is placed in the world (the spatialiser makes it stereo),
    ///  - resampled to 44.1 kHz,
    ///  - trimmed of leading silence (a footfall that starts 20 ms late lands after the foot),
    ///  - cut to the slot's length with a short fade, or for the four loops, cross-faded into itself so the
    ///    join does not click,
    ///  - normalised to a −3 dBFS peak, which is the headroom the mix buses expect,
    ///
    /// and written as 16-bit WAV to <c>Assets/Audio/Real/</c>, then wired onto the bank. The baked
    /// placeholder set is untouched, so any slot can go back by dragging the old clip onto it.
    ///
    /// Two ways to run it: <c>PoDecath/Audio/Import Sound Pack...</c> asks for the folder;
    /// <c>PoDecath/Audio/Import Sound Pack (config)</c> reads <c>training/logs/soundpack.json</c>
    /// (<c>{"folder": "...", "dryRun": true}</c>), which is how it runs from the command line.
    /// A dry run only writes the report of what it would pick.
    /// </summary>
    public static class SoundPackImporter
    {
        const string OutDir = "Assets/Audio/Real";
        const string BankPath = "Assets/Audio/AudioBank.asset";
        const string ConfigPath = "training/logs/soundpack.json";
        const string ReportPath = "training/logs/soundpack_import.json";
        const int Rate = 44100;

        /// <summary>What one bank slot wants from a file.</summary>
        class Rule
        {
            public string field;          // AudioBank field name
            public int count = 1;         // how many files (the footfall arrays take several)
            public string[] all;          // every group must match (each group is alternatives, '|')
            public string exclude;        // disqualifying words
            public float minLen, maxLen;  // seconds the source should be
            public float cut;             // seconds kept
            public bool loop, mono = true, trim = true;
        }

        static readonly Rule[] Rules =
        {
            new Rule { field = "crowdBed", all = new[] { "stadium|crowd|arena|sport|spectator", "ambien|atmos|walla|bed|background|loop|room tone" },
                       exclude = "cheer|applause|boo|chant|goal|scream|clap", minLen = 15f, maxLen = 600f, cut = 45f, loop = true, mono = false, trim = false },
            new Rule { field = "footfalls", count = 4, all = new[] { "footstep|foot step|footfall|step|run", "asphalt|concrete|tarmac|pavement|street|road|stone" },
                       exclude = "walk loop|ladder|stair|snow|wood|metal|grass|water|mud|heel", minLen = 0.08f, maxLen = 4f, cut = 0.28f },
            new Rule { field = "crowdSwell", all = new[] { "cheer|roar|goal|celebrat|excite", "crowd|stadium|arena|audience" },
                       exclude = "boo|small|kid|child", minLen = 1.5f, maxLen = 20f, cut = 4.5f, mono = false },
            new Rule { field = "crowdGroan", all = new[] { "groan|aww|oh no|ooh|disappoint|gasp|sigh", "crowd|stadium|arena|audience|group" },
                       minLen = 0.8f, maxLen = 10f, cut = 2.8f, mono = false },
            new Rule { field = "windBed", all = new[] { "wind", "ambien|loop|steady|constant|roof|howl|atmos|bed" },
                       exclude = "gust|whoosh|chime|instrument|interior|door", minLen = 10f, maxLen = 900f, cut = 30f, loop = true, mono = false, trim = false },
            new Rule { field = "pistol", all = new[] { "starter pistol|starting pistol|start pistol|cap gun|pistol|blank" },
                       exclude = "reload|cock|dry fire|mag|holster|silenc", minLen = 0.15f, maxLen = 6f, cut = 0.6f },
            new Rule { field = "breath", all = new[] { "breath|breathing|pant|exhale|inhale" },
                       exclude = "monster|creature|horse|dog|zombie|robot|scuba", minLen = 1f, maxLen = 30f, cut = 2.2f, loop = true },
            new Rule { field = "footfallsSand", count = 3, all = new[] { "footstep|foot step|footfall|step", "sand|beach|dune" },
                       minLen = 0.08f, maxLen = 4f, cut = 0.3f },
            new Rule { field = "footfallsRubber", count = 3, all = new[] { "footstep|foot step|footfall|step", "rubber|gym|sneaker|trainer|court|track|linoleum" },
                       exclude = "metal|wood", minLen = 0.08f, maxLen = 4f, cut = 0.28f },
            new Rule { field = "crowdApplause", all = new[] { "applause|clapping" },
                       exclude = "single|one person|golf", minLen = 3f, maxLen = 60f, cut = 7f, mono = false },
            new Rule { field = "crowdChant", all = new[] { "chant|rhythmic clap|clap rhythm|stomp|chanting" },
                       minLen = 4f, maxLen = 120f, cut = 12f, loop = true, mono = false, trim = false },
            new Rule { field = "countBeep", all = new[] { "beep|countdown|timer" },
                       exclude = "alarm|phone|truck|reverse|error", minLen = 0.04f, maxLen = 2f, cut = 0.16f },
            new Rule { field = "lapBell", all = new[] { "bell" },
                       exclude = "door|bicycle|bike|church|cow|jingle|sleigh|school", minLen = 0.4f, maxLen = 10f, cut = 1.8f },
            new Rule { field = "hurdleClatter", all = new[] { "metal|frame|pole|barrier|scaffold", "crash|clatter|fall|topple|knock over|collapse" },
                       minLen = 0.3f, maxLen = 8f, cut = 1.3f },
            new Rule { field = "hurdleClip", all = new[] { "metal|pole|bar|pipe", "hit|clang|knock|impact|tap|bump" },
                       exclude = "crash|fall|large|heavy", minLen = 0.05f, maxLen = 4f, cut = 0.5f },
            new Rule { field = "sandThud", all = new[] { "sand", "land|impact|body|fall|jump|thud|drop" },
                       minLen = 0.1f, maxLen = 5f, cut = 0.5f },
            new Rule { field = "whoosh", all = new[] { "whoosh|swish|swoosh|woosh|pass by|passby" },
                       exclude = "fire|sword", minLen = 0.15f, maxLen = 4f, cut = 0.5f },
            new Rule { field = "sting", all = new[] { "sting|stinger|logo|fanfare|jingle|hit short|musical accent" },
                       exclude = "bee|wasp|scorpion", minLen = 0.3f, maxLen = 8f, cut = 1f, mono = false },
        };

        [Serializable] class Config { public string folder; public bool dryRun = true; }

        [Serializable] class Pick { public string slot; public string source; public float sourceSeconds; public float score; public string output; }
        [Serializable] class Report { public string folder; public bool dryRun; public int scanned; public List<Pick> picks = new List<Pick>(); public List<string> unfilled = new List<string>(); }

        [MenuItem("PoDecath/Audio/Import Sound Pack...", priority = 30)]
        public static void ImportWithDialog()
        {
            string folder = EditorUtility.OpenFolderPanel("Folder of WAV files (e.g. the unzipped Sonniss GDC bundle)", "", "");
            if (string.IsNullOrEmpty(folder)) return;
            bool dry = EditorUtility.DisplayDialog("Import sound pack",
                "Scan the folder and write what would be picked to training/logs/soundpack_import.json, or import and wire the bank now?",
                "Dry run", "Import");
            Import(folder, dry);
        }

        [MenuItem("PoDecath/Audio/Import Sound Pack (config)", priority = 31)]
        public static void ImportFromConfig()
        {
            if (!File.Exists(ConfigPath)) { Debug.LogError($"[SoundPack] {ConfigPath} not found. Write {{\"folder\": \"D:/Sonniss\", \"dryRun\": true}} there."); return; }
            var cfg = JsonUtility.FromJson<Config>(File.ReadAllText(ConfigPath));
            if (cfg == null || string.IsNullOrEmpty(cfg.folder)) { Debug.LogError($"[SoundPack] {ConfigPath} has no folder."); return; }
            Import(cfg.folder, cfg.dryRun);
        }

        public static void Import(string folder, bool dryRun, string bankPath = BankPath, string outDir = OutDir)
        {
            if (!Directory.Exists(folder)) { Debug.LogError($"[SoundPack] no folder {folder}"); return; }
            var files = new List<(string path, string key, float seconds)>();
            foreach (string f in Directory.EnumerateFiles(folder, "*.wav", SearchOption.AllDirectories))
            {
                float sec = WavSeconds(f);
                if (sec <= 0f) continue;
                // The folder path counts as well as the name: packs file things under "Crowds/" or "Footsteps/".
                string rel = f.Substring(folder.Length).Replace('\\', '/');
                files.Add((f, Normalise(rel), sec));
            }

            var report = new Report { folder = folder, dryRun = dryRun, scanned = files.Count };
            var used = new HashSet<string>();
            AudioBank bank = dryRun ? null : AssetDatabase.LoadAssetAtPath<AudioBank>(bankPath);
            if (!dryRun && bank == null) { Debug.LogError($"[SoundPack] no bank at {bankPath}"); return; }
            if (!dryRun) PolicyLibraryTools.EnsureFolder(outDir);
            var sources = new StringBuilder();
            var assign = new Dictionary<string, List<string>>();

            foreach (Rule rule in Rules)
            {
                var ranked = files.Where(x => !used.Contains(x.path))
                                  .Select(x => (x.path, x.seconds, score: Score(rule, x.key, x.seconds)))
                                  .Where(x => x.score > 0f)
                                  .OrderByDescending(x => x.score)
                                  .Take(rule.count)
                                  .ToList();
                if (ranked.Count == 0) { report.unfilled.Add(rule.field); continue; }

                for (int i = 0; i < ranked.Count; i++)
                {
                    var pick = ranked[i];
                    used.Add(pick.path);
                    string name = rule.count > 1 ? $"{rule.field}_{i + 1}.wav" : $"{rule.field}.wav";
                    string output = $"{outDir}/{name}";
                    if (!dryRun)
                    {
                        if (!Process(pick.path, output, rule)) { report.unfilled.Add($"{rule.field} ({Path.GetFileName(pick.path)} unreadable)"); continue; }
                        if (!assign.TryGetValue(rule.field, out var list)) assign[rule.field] = list = new List<string>();
                        list.Add(output);
                        sources.AppendLine($"{name}  <-  {pick.path.Substring(folder.Length).TrimStart('/', '\\')}");
                    }
                    report.picks.Add(new Pick { slot = rule.field, source = pick.path, sourceSeconds = pick.seconds, score = pick.score, output = dryRun ? "" : output });
                }
            }

            if (!dryRun)
            {
                File.WriteAllText($"{outDir}/SOURCES.txt",
                    "Recordings imported by PoDecath/Audio/Import Sound Pack from " + folder + "\n" +
                    "Sonniss GDC Game Audio Bundle licence: royalty-free, commercial use allowed, no attribution\n" +
                    "required; the raw files may not be redistributed as a sound library.\n\n" + sources);
                AssetDatabase.Refresh();
                foreach (var kv in assign) foreach (string p in kv.Value) ConfigureImporter(p, Rules.First(r => r.field == kv.Key));
                Wire(bank, assign);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
            Debug.Log($"[SoundPack] {(dryRun ? "dry run" : "imported")}: scanned {files.Count} WAV file(s), " +
                      $"{report.picks.Count} pick(s), {report.unfilled.Count} slot(s) unfilled" +
                      (report.unfilled.Count > 0 ? $" ({string.Join(", ", report.unfilled)})" : "") + $". Report: {ReportPath}");
        }

        // ---------------------------------------------------------------- choosing

        static string Normalise(string s) => Regex.Replace(s.ToLowerInvariant(), @"[_\-\.\s/]+", " ");

        /// <summary>
        /// 0 = not a candidate. Otherwise every required group scored once, plus a bonus for how well the
        /// length fits: the middle of the range the slot wants scores best, and a file far outside it is out.
        /// </summary>
        static float Score(Rule r, string key, float seconds)
        {
            if (seconds < r.minLen || seconds > r.maxLen) return 0f;
            if (!string.IsNullOrEmpty(r.exclude) && Regex.IsMatch(key, $@"\b({r.exclude})")) return 0f;
            float score = 0f;
            foreach (string group in r.all)
            {
                MatchCollection m = Regex.Matches(key, $@"\b({group})");
                if (m.Count == 0) return 0f;
                score += 1f + 0.1f * Mathf.Min(3, m.Count - 1);
            }
            // Length: prefer sources close to (but not shorter than) what is kept.
            float fit = seconds >= r.cut ? 1f / (1f + Mathf.Log(seconds / Mathf.Max(0.01f, r.cut) + 1f)) : seconds / r.cut * 0.5f;
            if (r.loop) fit = seconds >= r.cut ? 1f : 0.3f;
            return score + fit;
        }

        // ---------------------------------------------------------------- WAV in

        /// <summary>Length in seconds from the header alone, so a thirty-gigabyte folder scans in seconds.</summary>
        static float WavSeconds(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                using var br = new BinaryReader(fs);
                if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "RIFF") return 0f;
                br.ReadInt32();
                if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "WAVE") return 0f;
                int byteRate = 0;
                while (fs.Position + 8 <= fs.Length)
                {
                    string id = Encoding.ASCII.GetString(br.ReadBytes(4));
                    int size = br.ReadInt32();
                    if (id == "fmt ")
                    {
                        long start = fs.Position;
                        br.ReadInt16(); br.ReadInt16(); br.ReadInt32();
                        byteRate = br.ReadInt32();
                        fs.Position = start + size + (size & 1);
                    }
                    else if (id == "data") return byteRate > 0 ? (float)((uint)size / (double)byteRate) : 0f;
                    else fs.Position += size + (size & 1);
                }
            }
            catch { }
            return 0f;
        }

        /// <summary>Reads PCM 8/16/24/32-bit integer or 32-bit float, any channel count. Returns interleaved samples in [-1, 1].</summary>
        static bool ReadWav(string path, out float[] samples, out int channels, out int rate)
        {
            samples = null; channels = 0; rate = 0;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                int pos = 12, format = 0, bits = 0;
                while (pos + 8 <= bytes.Length)
                {
                    string id = Encoding.ASCII.GetString(bytes, pos, 4);
                    int size = BitConverter.ToInt32(bytes, pos + 4);
                    int body = pos + 8;
                    if (id == "fmt ")
                    {
                        format = BitConverter.ToInt16(bytes, body);
                        channels = BitConverter.ToInt16(bytes, body + 2);
                        rate = BitConverter.ToInt32(bytes, body + 4);
                        bits = BitConverter.ToInt16(bytes, body + 14);
                        if (format == -2 && size >= 40) format = BitConverter.ToInt16(bytes, body + 24);   // WAVE_FORMAT_EXTENSIBLE: the real tag
                    }
                    else if (id == "data")
                    {
                        size = Mathf.Min(size, bytes.Length - body);
                        int step = bits / 8;
                        if (channels <= 0 || step <= 0) return false;
                        int n = size / step;
                        samples = new float[n];
                        for (int i = 0; i < n; i++)
                        {
                            int o = body + i * step;
                            samples[i] = format == 3 && bits == 32 ? BitConverter.ToSingle(bytes, o)
                                       : bits == 8 ? (bytes[o] - 128) / 128f
                                       : bits == 16 ? BitConverter.ToInt16(bytes, o) / 32768f
                                       : bits == 24 ? ((bytes[o] | (bytes[o + 1] << 8) | ((sbyte)bytes[o + 2] << 16)) / 8388608f)
                                       : bits == 32 ? BitConverter.ToInt32(bytes, o) / 2147483648f
                                       : 0f;
                        }
                        return true;
                    }
                    pos = body + size + (size & 1);
                }
            }
            catch (Exception e) { Debug.LogWarning($"[SoundPack] {path}: {e.Message}"); }
            return false;
        }

        // ---------------------------------------------------------------- shaping

        static bool Process(string src, string dst, Rule rule)
        {
            if (!ReadWav(src, out float[] raw, out int ch, out int rate)) return false;
            int outCh = rule.mono ? 1 : Mathf.Min(2, ch);
            int frames = raw.Length / ch;

            // Down-mix (or keep the first two channels) into per-channel buffers.
            var chans = new float[outCh][];
            for (int c = 0; c < outCh; c++) chans[c] = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                if (outCh == 1)
                {
                    float sum = 0f;
                    for (int c = 0; c < ch; c++) sum += raw[i * ch + c];
                    chans[0][i] = sum / ch;
                }
                else for (int c = 0; c < outCh; c++) chans[c][i] = raw[i * ch + Mathf.Min(c, ch - 1)];
            }

            for (int c = 0; c < outCh; c++) chans[c] = Resample(chans[c], rate, Rate);
            frames = chans[0].Length;

            int start = rule.trim ? FirstSound(chans, frames) : 0;
            float keepSec = rule.loop ? rule.cut + 0.5f : rule.cut;   // a loop needs its cross-fade tail too
            int length = Mathf.Min(frames - start, Mathf.RoundToInt(keepSec * Rate));
            if (length <= 16) return false;

            var shaped = new float[outCh][];
            for (int c = 0; c < outCh; c++)
            {
                shaped[c] = new float[length];
                Array.Copy(chans[c], start, shaped[c], 0, length);
                if (rule.loop) shaped[c] = CrossfadeLoop(shaped[c], Mathf.Min(length / 4, Mathf.RoundToInt(0.5f * Rate)));
                else FadeOut(shaped[c], Mathf.Min(shaped[c].Length / 3, Mathf.RoundToInt(0.012f * Rate)));
            }

            // -3 dBFS peak: the level the mix buses were set against.
            float peak = 1e-6f;
            foreach (float[] b in shaped) foreach (float v in b) peak = Mathf.Max(peak, Mathf.Abs(v));
            float gain = 0.708f / peak;
            foreach (float[] b in shaped) for (int i = 0; i < b.Length; i++) b[i] *= gain;

            WriteWav(dst, shaped, Rate);
            return true;
        }

        static float[] Resample(float[] x, int from, int to)
        {
            if (from == to || x.Length < 2) return x;
            // Box pre-filter when coming down from a high rate: linear interpolation alone aliases a 96 kHz file.
            if (from > to)
            {
                int k = Mathf.Max(1, Mathf.RoundToInt(from / (float)to));
                if (k > 1)
                {
                    var f = new float[x.Length];
                    float acc = 0f;
                    for (int i = 0; i < x.Length; i++)
                    {
                        acc += x[i];
                        if (i >= k) acc -= x[i - k];
                        f[i] = acc / Mathf.Min(i + 1, k);
                    }
                    x = f;
                }
            }
            int n = (int)((long)x.Length * to / from);
            var y = new float[n];
            double ratio = (double)from / to;
            for (int i = 0; i < n; i++)
            {
                double p = i * ratio;
                int a = (int)p;
                float t = (float)(p - a);
                y[i] = a + 1 < x.Length ? x[a] + (x[a + 1] - x[a]) * t : x[x.Length - 1];
            }
            return y;
        }

        /// <summary>First sample louder than −40 dB below the file's own peak, backed off 2 ms so the attack survives.</summary>
        static int FirstSound(float[][] chans, int frames)
        {
            float peak = 0f;
            foreach (float[] c in chans) for (int i = 0; i < frames; i++) peak = Mathf.Max(peak, Mathf.Abs(c[i]));
            float gate = peak * 0.01f;
            for (int i = 0; i < frames; i++)
                foreach (float[] c in chans)
                    if (Mathf.Abs(c[i]) > gate) return Mathf.Max(0, i - Mathf.RoundToInt(0.002f * Rate));
            return 0;
        }

        static void FadeOut(float[] b, int n)
        {
            for (int i = 0; i < n; i++) b[b.Length - 1 - i] *= i / (float)n;
        }

        /// <summary>
        /// Makes a seamless loop: the last <paramref name="xf"/> samples are faded into the first, and the
        /// tail is dropped, so the end of the file flows into its start with no step and no click.
        /// </summary>
        static float[] CrossfadeLoop(float[] b, int xf)
        {
            if (xf < 16 || b.Length < xf * 2) return b;
            int n = b.Length - xf;
            var y = new float[n];
            Array.Copy(b, y, n);
            for (int i = 0; i < xf; i++)
            {
                float t = i / (float)xf;
                // Equal-power: keeps the level steady through the join on uncorrelated material like crowd and wind.
                y[i] = b[i] * Mathf.Sin(t * Mathf.PI * 0.5f) + b[n + i] * Mathf.Cos(t * Mathf.PI * 0.5f);
            }
            return y;
        }

        static void WriteWav(string path, float[][] chans, int rate)
        {
            int ch = chans.Length, n = chans[0].Length;
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            int data = n * ch * 2;
            bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + data); bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt ")); bw.Write(16); bw.Write((short)1); bw.Write((short)ch);
            bw.Write(rate); bw.Write(rate * ch * 2); bw.Write((short)(ch * 2)); bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(data);
            for (int i = 0; i < n; i++)
                for (int c = 0; c < ch; c++)
                    bw.Write((short)Mathf.Clamp(Mathf.RoundToInt(chans[c][i] * 32767f), -32768, 32767));
        }

        // ---------------------------------------------------------------- into the bank

        static void ConfigureImporter(string path, Rule rule)
        {
            if (!(AssetImporter.GetAtPath(path) is AudioImporter ai)) return;
            AudioImporterSampleSettings s = ai.defaultSampleSettings;
            // Long beds stream from disk; short one-shots are decompressed once and kept, which is what makes
            // a footfall fire on the frame it is asked for.
            s.loadType = rule.loop && rule.cut > 5f ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad;
            s.compressionFormat = AudioCompressionFormat.Vorbis;
            s.quality = 0.7f;
            ai.defaultSampleSettings = s;
            ai.forceToMono = rule.mono;
            ai.loadInBackground = rule.loop;
            ai.SaveAndReimport();
        }

        static void Wire(AudioBank bank, Dictionary<string, List<string>> assign)
        {
            var so = new SerializedObject(bank);
            foreach (var kv in assign)
            {
                SerializedProperty prop = so.FindProperty(kv.Key);
                if (prop == null) { Debug.LogWarning($"[SoundPack] AudioBank has no field {kv.Key}"); continue; }
                var clips = kv.Value.Select(p => AssetDatabase.LoadAssetAtPath<AudioClip>(p)).Where(c => c != null).ToList();
                if (clips.Count == 0) continue;
                if (prop.isArray)
                {
                    prop.arraySize = clips.Count;
                    for (int i = 0; i < clips.Count; i++) prop.GetArrayElementAtIndex(i).objectReferenceValue = clips[i];
                }
                else prop.objectReferenceValue = clips[0];
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bank);
            AssetDatabase.SaveAssets();
        }
    }
}
