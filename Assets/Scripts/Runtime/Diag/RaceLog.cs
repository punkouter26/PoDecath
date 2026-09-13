using System;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using PoDecath.Env;
using PoDecath.Sim;

namespace PoDecath.Diag
{
    /// <summary>
    /// One JSON file per race: what device ran it, at what tier, how the frame held up while it was
    /// running, and who finished where.
    ///
    /// The F3 overlay shows the frame rate while somebody is looking at it; this keeps it. A phone build
    /// that was "fine when I tried it" becomes a file with a mean, a 1% low and a worst frame that can be
    /// put next to the editor's <c>scene_sweep.json</c> and next to the run before it. Sampling is a ring
    /// of frame times from the gun to the last finisher, so a long 1500 m does not grow it without bound.
    ///
    /// Written to <c>Application.persistentDataPath/races/</c> on every platform, and in the editor also
    /// to <c>training/logs/races/</c> beside the sweep, so nothing has to be pulled off a device to be read.
    /// </summary>
    [DefaultExecutionOrder(205)]
    public class RaceLog : MonoBehaviour
    {
        public RaceEvent race;
        [Tooltip("Frame samples kept. 6000 is ten minutes at 10 Hz, or 100 s at 60 Hz; the ring wraps.")]
        public int samples = 6000;

        [Tooltip("Frames in this first window after the gun are recorded separately instead of counted. "
               + "The first attempt in a scene starts moments after load, so shader warm-up, policy "
               + "initialisation and asset loading all land in it, and they are not frames the device will "
               + "ever render again. 0 counts everything from the gun.")]
        public float warmUpSeconds = 1.5f;

        [Tooltip("A frame longer than this is treated as a stall rather than a slow frame: an editor pause, "
               + "an alt-tab, a breakpoint or an asset import. Counted and reported, never averaged, "
               + "because no phone produces a one second frame and letting one into the sample makes the "
               + "1% low meaningless.")]
        public float stallCeilingMs = 1000f;

        float[] _ms;
        int _count, _head;
        float _worst;
        float _warmUpWorst;
        float _stallWorst;
        int _stalls;
        float _recordStart;
        long _drawSum, _triSum;
        int _renderSamples;
        float _batteryStart = -1f;
        bool _recording;
        ProfilerRecorder _draw, _tris;

        void Awake()
        {
            _ms = new float[Mathf.Max(100, samples)];
        }

        void OnEnable()
        {
            if (race == null) race = FindFirstObjectByType<RaceEvent>();
            if (race == null) { enabled = false; return; }
            race.RaceStarted += OnStarted;
            race.RaceComplete += OnComplete;
            _draw = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _tris = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
        }

        void OnDisable()
        {
            if (race != null)
            {
                race.RaceStarted -= OnStarted;
                race.RaceComplete -= OnComplete;
            }
            _draw.Dispose();
            _tris.Dispose();
        }

        void OnStarted()
        {
            _count = _head = 0;
            _worst = 0f;
            _warmUpWorst = 0f;
            _stallWorst = 0f;
            _stalls = 0;
            _recordStart = Time.unscaledTime;
            _drawSum = _triSum = 0;
            _renderSamples = 0;
            _batteryStart = SystemInfo.batteryLevel;
            _recording = true;
        }

        /// <summary>
        /// One frame. Three buckets, and which one a frame lands in is the difference between a useful
        /// record and a misleading one.
        ///
        /// The measured example that put this here: a 100 m race logged 46 FPS mean, a 1% low of 2 FPS and
        /// a worst frame of 3,456 ms, while the frame counter on screen sat at 59 FPS and 17.1 ms
        /// throughout. Nothing in the race took three and a half seconds. Recording begins on the gun, the
        /// first attempt in a scene starts moments after it loads, and the whole of shader warm-up and
        /// policy initialisation was being averaged in as gameplay.
        ///
        /// Nothing is thrown away, because a load stall is worth knowing about; it is reported on its own
        /// line instead of being allowed to define the headline.
        /// </summary>
        void Update()
        {
            if (!_recording) return;
            float ms = Time.unscaledDeltaTime * 1000f;

            if (warmUpSeconds > 0f && Time.unscaledTime - _recordStart < warmUpSeconds)
            {
                if (ms > _warmUpWorst) _warmUpWorst = ms;
                return;
            }

            if (stallCeilingMs > 0f && ms > stallCeilingMs)
            {
                _stalls++;
                if (ms > _stallWorst) _stallWorst = ms;
                return;
            }

            _ms[_head] = ms;
            _head = (_head + 1) % _ms.Length;
            if (_count < _ms.Length) _count++;
            if (ms > _worst) _worst = ms;
            if (_draw.Valid && _tris.Valid && (_renderSamples == 0 || Time.frameCount % 10 == 0))
            {
                _drawSum += _draw.LastValue;
                _triSum += _tris.LastValue;
                _renderSamples++;
            }
        }

        void OnComplete(System.Collections.Generic.List<RaceEvent.RaceResult> results)
        {
            if (!_recording) return;
            _recording = false;
            try
            {
                string json = Build(results);
                string name = $"race_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                string dir = Path.Combine(Application.persistentDataPath, "races");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, name), json);
#if UNITY_EDITOR
                string projectDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "training", "logs", "races");
                Directory.CreateDirectory(projectDir);
                File.WriteAllText(Path.Combine(projectDir, name), json);
#endif
                string aside = _stalls > 0
                    ? $"  ({_stalls} stall(s) over {stallCeilingMs:F0} ms excluded, worst {_stallWorst:F0} ms; warm-up worst {_warmUpWorst:F0} ms)"
                    : $"  (warm-up worst {_warmUpWorst:F0} ms, not counted)";
                Debug.Log($"[RaceLog] {name}: {Fps(Mean()):F0} FPS mean, {Fps(OnePercentLow()):F0} FPS 1% low, "
                        + $"worst frame {_worst:F1} ms.{aside}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RaceLog] not written: {e.Message}");
            }
        }

        string Build(System.Collections.Generic.List<RaceEvent.RaceResult> results)
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            float mean = Mean(), low = OnePercentLow();
            int laps = race is LapEvent lap ? lap.laps : 0;
            sb.Append("{");
            sb.AppendFormat(ci, "\"written\":\"{0:o}\",", DateTime.Now);
            sb.AppendFormat(ci, "\"scene\":\"{0}\",", Esc(gameObject.scene.name));
            sb.AppendFormat(ci, "\"event\":\"{0}\",", Esc(race.GetType().Name));
            sb.AppendFormat(ci, "\"laps\":{0},", laps);
            sb.AppendFormat(ci, "\"distance_m\":{0:F1},", race.raceDistance);
            sb.AppendFormat(ci, "\"race_seconds\":{0:F2},", race.RaceTime);
            sb.AppendFormat(ci, "\"tier\":\"{0}\",", RenderTier.Current);
            sb.AppendFormat(ci, "\"quality\":\"{0}\",", Esc(QualitySettings.names[QualitySettings.GetQualityLevel()]));
            sb.AppendFormat(ci, "\"device\":\"{0}\",", Esc(SystemInfo.deviceModel));
            sb.AppendFormat(ci, "\"gpu\":\"{0}\",", Esc(SystemInfo.graphicsDeviceName));
            sb.AppendFormat(ci, "\"os\":\"{0}\",", Esc(SystemInfo.operatingSystem));
            sb.AppendFormat(ci, "\"screen\":\"{0}x{1}\",", Screen.width, Screen.height);
            sb.AppendFormat(ci, "\"target_fps\":{0},", Application.targetFrameRate);
            sb.AppendFormat(ci, "\"frames\":{0},", _count);
            sb.AppendFormat(ci, "\"fps_mean\":{0:F1},", Fps(mean));
            sb.AppendFormat(ci, "\"fps_1pct_low\":{0:F1},", Fps(low));
            sb.AppendFormat(ci, "\"frame_ms_mean\":{0:F2},", mean);
            sb.AppendFormat(ci, "\"frame_ms_worst\":{0:F2},", _worst);
            sb.AppendFormat(ci, "\"frame_ms_worst_warmup\":{0:F2},", _warmUpWorst);
            sb.AppendFormat(ci, "\"warmup_seconds\":{0:F2},", warmUpSeconds);
            sb.AppendFormat(ci, "\"stalls_excluded\":{0},", _stalls);
            sb.AppendFormat(ci, "\"frame_ms_worst_stall\":{0:F2},", _stallWorst);
            sb.AppendFormat(ci, "\"draw_calls_mean\":{0},", _renderSamples > 0 ? _drawSum / _renderSamples : 0);
            sb.AppendFormat(ci, "\"triangles_mean\":{0},", _renderSamples > 0 ? _triSum / _renderSamples : 0);
            sb.AppendFormat(ci, "\"battery_start\":{0:F2},", _batteryStart);
            sb.AppendFormat(ci, "\"battery_end\":{0:F2},", SystemInfo.batteryLevel);
            sb.Append("\"results\":[");
            for (int i = 0; i < results.Count; i++)
            {
                RaceEvent.RaceResult r = results[i];
                if (i > 0) sb.Append(",");
                sb.Append("{");
                sb.AppendFormat(ci, "\"rank\":{0},\"number\":{1},\"name\":\"{2}\",\"kind\":\"{3}\",", r.rank, r.number, Esc(r.name), r.kind);
                sb.AppendFormat(ci, "\"time\":{0:F2},\"distance\":{1:F2},\"finished\":{2},\"fell\":{3},\"status\":\"{4}\"",
                                r.time, r.distance, r.finished ? "true" : "false", r.fell ? "true" : "false", Esc(r.Status));
                sb.Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static float Fps(float ms) => ms > 0f ? 1000f / ms : 0f;

        float Mean()
        {
            if (_count == 0) return 0f;
            float sum = 0f;
            for (int i = 0; i < _count; i++) sum += _ms[i];
            return sum / _count;
        }

        float OnePercentLow()
        {
            if (_count < 20) return _worst;
            int take = Mathf.Max(1, _count / 100);
            var copy = new float[_count];
            Array.Copy(_ms, copy, _count);
            Array.Sort(copy);
            float sum = 0f;
            for (int i = 0; i < take; i++) sum += copy[_count - 1 - i];
            return sum / take;
        }

        static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
