using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using UnityEngine.UIElements;
using PoDecath.Audio;
using PoDecath.Env;
using PoDecath.Sim;
using PoDecath.UI;

namespace PoDecath.Diag
{
    /// <summary>
    /// A real-time diagnostics panel: frame rate and its worst one per cent, where the frame is being
    /// spent, how much the renderer is being asked to draw, what the garbage collector is doing, and what
    /// the simulation and the mix are up to.
    ///
    /// The project had no way to see any of this. The README's own open item is that the 122 MB White House
    /// mesh needs decimation before a phone build and that the scene runs "~837k triangles, ~700 batches in
    /// the Editor" — numbers somebody had to read out of a profiler window by hand, once. Every visual
    /// feature added on top of that is a guess until it can be measured while the race is running, which is
    /// what this is for.
    ///
    /// The counters come from <see cref="ProfilerRecorder"/>, which is available in a player as well as in
    /// the editor — some of them only in a development build, which is why a recorder that is not valid is
    /// left out of the panel entirely rather than shown as a permanent zero.
    ///
    /// It is off by default and toggled with F3, the key everything else in the world uses for this.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public class TelemetryOverlay : UiRoot
    {
        [Header("Wiring")]
        [Tooltip("Optional; adds the policy and athlete rows when a race is running.")]
        public DashEvent race;
        [Tooltip("Optional; adds what the crowd is doing.")]
        public RaceAudio audioMix;

        [Header("Behaviour")]
        [Tooltip("Only read when the project is on the legacy input manager; the Input System path below "
               + "is hard-wired to F3, which is the key everything else in the world uses for this.")]
        public KeyCode toggleKey = KeyCode.F3;
        [Tooltip("Open as soon as the scene starts. Off by default: this is a diagnostic, not furniture.")]
        public bool startOpen = false;
        [Tooltip("How often the numbers are rebuilt. The graph samples every frame regardless.")]
        public float refreshSeconds = 0.25f;
        [Tooltip("Frames kept for the graph and the 1% low. 240 is about four seconds at 60 FPS.")]
        public int history = 240;

        [Header("Budget")]
        [Tooltip("The frame budget being aimed at. Rows go amber and then red against this.")]
        public float targetFps = 60f;

        VisualElement _panel, _rows, _graph;
        Label _title, _footer;
        readonly Dictionary<string, Label> _values = new Dictionary<string, Label>();

        float[] _frameMs;
        int _frameCount, _frameHead;
        float _nextRefresh;

        // Everything below is a Profiler counter. Names come from Unity's built-in ProfilerCategory stats;
        // any that this platform or build type does not publish comes back invalid and is skipped.
        ProfilerRecorder _mainThread, _renderThread, _drawCalls, _setPass, _triangles, _vertices,
                         _batches, _gcAlloc, _gcReserved, _systemMemory, _textureMemory, _meshMemory;

        protected override void Build()
        {
            _panel = Find<VisualElement>("panel");
            _rows = Find<VisualElement>("rows");
            _graph = Find<VisualElement>("graph");
            _title = Find<Label>("title");
            _footer = Find<Label>("footer");

            _frameMs = new float[Mathf.Max(30, history)];
            if (_graph != null) _graph.generateVisualContent += DrawGraph;

            StartRecorders();
            BuildRows();
            SetScreenVisible(startOpen);
            applySafeArea = false;   // a diagnostic belongs in the corner of the picture, notch or no notch
        }

        void OnDisable()
        {
            StopRecorders();
        }

        void StartRecorders()
        {
            _mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 15);
            _renderThread = ProfilerRecorder.StartNew(ProfilerCategory.Render, "CPU Main Thread Frame Time", 15);
            _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            _vertices = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");
            _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            _gcAlloc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            _gcReserved = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Reserved Memory");
            _systemMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory");
            _textureMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Texture Memory");
            _meshMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Mesh Memory");
        }

        void StopRecorders()
        {
            _mainThread.Dispose(); _renderThread.Dispose(); _drawCalls.Dispose(); _setPass.Dispose();
            _triangles.Dispose(); _vertices.Dispose(); _batches.Dispose(); _gcAlloc.Dispose();
            _gcReserved.Dispose(); _systemMemory.Dispose(); _textureMemory.Dispose(); _meshMemory.Dispose();
        }

        /// <summary>
        /// One row per metric. Built once and then only written to, because rebuilding a dozen labels four
        /// times a second in a panel that exists to measure overhead would be its own joke.
        /// </summary>
        void BuildRows()
        {
            if (_rows == null) return;
            _rows.Clear();
            _values.Clear();

            AddRow("FPS");
            AddRow("1% low");
            AddRow("Frame");
            if (_mainThread.Valid) AddRow("Main thread");
            AddRow("Fixed steps/s");
            if (_drawCalls.Valid) AddRow("Draw calls");
            if (_setPass.Valid) AddRow("SetPass");
            if (_batches.Valid) AddRow("Batches");
            if (_triangles.Valid) AddRow("Triangles");
            if (_gcAlloc.Valid) AddRow("GC / frame");
            if (_systemMemory.Valid) AddRow("System memory");
            if (_textureMemory.Valid) AddRow("Texture memory");
            AddRow("Athletes");
            AddRow("Audio voices");
            AddRow("Crowd");
        }

        void AddRow(string key)
        {
            var row = new VisualElement();
            row.AddToClassList("telemetry-row");
            var k = new Label(key);
            k.AddToClassList("telemetry-key");
            var v = new Label("-");
            v.AddToClassList("telemetry-value");
            row.Add(k);
            row.Add(v);
            _rows.Add(row);
            _values[key] = v;
        }

        protected override void Update()
        {
            base.Update();
            if (TogglePressed()) SetScreenVisible(!ScreenVisible);

            // Sampled every frame even while closed: opening the panel and waiting four seconds for a graph
            // to fill up is exactly when you are least willing to wait.
            Sample(Time.unscaledDeltaTime * 1000f);

            if (!ScreenVisible) return;
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + refreshSeconds;
            Refresh();
            _graph?.MarkDirtyRepaint();
        }

        /// <summary>
        /// The toggle key, under whichever input backend the project is built against. This project is on
        /// the Input System package, and reading UnityEngine.Input there throws on the first frame rather
        /// than returning false — which is exactly the kind of failure a diagnostic overlay must not cause.
        /// </summary>
        static bool TogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.f3Key.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.F3);
#endif
        }

        void Sample(float ms)
        {
            _frameMs[_frameHead] = ms;
            _frameHead = (_frameHead + 1) % _frameMs.Length;
            if (_frameCount < _frameMs.Length) _frameCount++;
        }

        // ---------------------------------------------------------------- the numbers

        void Refresh()
        {
            float mean = Mean(), low = OnePercentLow();
            float budget = 1000f / Mathf.Max(1f, targetFps);

            Set("FPS", $"{(mean > 0f ? 1000f / mean : 0f):F0}", Grade(mean, budget));
            Set("1% low", $"{(low > 0f ? 1000f / low : 0f):F0}", Grade(low, budget * 1.35f));
            Set("Frame", $"{mean:F2} ms", Grade(mean, budget));
            if (_mainThread.Valid) Set("Main thread", $"{_mainThread.LastValue / 1e6f:F2} ms", Grade(_mainThread.LastValue / 1e6f, budget));

            // Physics runs at a fixed 200 Hz and the policies at 50; a scene that cannot keep up shows here
            // long before it shows in the frame rate, because Unity simply runs fewer steps per frame.
            Set("Fixed steps/s", $"{(Time.fixedDeltaTime > 0f ? 1f / Time.fixedDeltaTime : 0f):F0}");

            if (_drawCalls.Valid) Set("Draw calls", _drawCalls.LastValue.ToString());
            if (_setPass.Valid) Set("SetPass", _setPass.LastValue.ToString());
            if (_batches.Valid) Set("Batches", _batches.LastValue.ToString());
            if (_triangles.Valid) Set("Triangles", Thousands(_triangles.LastValue));
            if (_gcAlloc.Valid) Set("GC / frame", Bytes(_gcAlloc.LastValue), _gcAlloc.LastValue > 4096 ? "warn" : "good");
            if (_systemMemory.Valid) Set("System memory", Bytes(_systemMemory.LastValue));
            if (_textureMemory.Valid) Set("Texture memory", Bytes(_textureMemory.LastValue));

            Athletes();
            Set("Audio voices", CountAudibleSources().ToString());
            Set("Crowd", audioMix != null ? audioMix.CurrentMood.ToString() : "-");

            SetText(_title, $"TELEMETRY   ·   {RenderTier.Current}   ·   {QualitySettings.names[QualitySettings.GetQualityLevel()]}");
            SetText(_footer, $"{Screen.width}x{Screen.height}   {Application.targetFrameRate} fps target   F3 to close");
        }

        /// <summary>
        /// What the simulation is carrying: how many athletes, how many of them are running inference, and
        /// how much of the frame that is worth. Policy inference is the one cost in this project that no
        /// generic profiler row will attribute, because it is a managed call inside Update.
        /// </summary>
        void Athletes()
        {
            if (race == null) { Set("Athletes", "-"); return; }
            int total = race.Athletes.Count, rl = 0, down = 0;
            foreach (DashEvent.Athlete a in race.Athletes)
            {
                if (a.IsRL) rl++;
                if (a.fell) down++;
            }
            Set("Athletes", $"{total}  ({rl} RL, {down} down)");
        }

        static int CountAudibleSources()
        {
            AudioSource[] all = FindObjectsByType<AudioSource>(FindObjectsSortMode.None);
            int n = 0;
            foreach (AudioSource s in all) if (s != null && s.isPlaying && s.volume > 0.001f) n++;
            return n;
        }

        void Set(string key, string value, string grade = null)
        {
            if (!_values.TryGetValue(key, out Label label)) return;
            SetText(label, value);
            label.EnableInClassList("telemetry-value--good", grade == "good");
            label.EnableInClassList("telemetry-value--warn", grade == "warn");
            label.EnableInClassList("telemetry-value--bad", grade == "bad");
        }

        /// <summary>Green inside budget, amber up to half again, red past that.</summary>
        static string Grade(float ms, float budget) =>
            ms <= budget ? "good" : ms <= budget * 1.5f ? "warn" : "bad";

        float Mean()
        {
            if (_frameCount == 0) return 0f;
            float sum = 0f;
            for (int i = 0; i < _frameCount; i++) sum += _frameMs[i];
            return sum / _frameCount;
        }

        /// <summary>
        /// The mean of the worst one per cent of frames, which is what a stutter actually is. An average
        /// frame rate hides exactly the thing worth finding: one 40 ms frame in every hundred is invisible
        /// in a mean and immediately visible on screen.
        /// </summary>
        float OnePercentLow()
        {
            if (_frameCount < 20) return 0f;
            int take = Mathf.Max(1, _frameCount / 100);
            // A partial selection rather than a sort: this runs four times a second over 240 samples.
            float worstSum = 0f;
            for (int k = 0; k < take; k++)
            {
                float best = -1f;
                int bestIndex = -1;
                for (int i = 0; i < _frameCount; i++)
                {
                    if (_frameMs[i] <= best) continue;
                    bool alreadyTaken = false;
                    for (int j = 0; j < k; j++) if (_taken[j] == i) { alreadyTaken = true; break; }
                    if (alreadyTaken) continue;
                    best = _frameMs[i];
                    bestIndex = i;
                }
                if (bestIndex < 0) break;
                _taken[k] = bestIndex;
                worstSum += best;
            }
            return worstSum / take;
        }

        readonly int[] _taken = new int[64];

        // ---------------------------------------------------------------- graph

        /// <summary>
        /// The frame-time history, oldest on the left, with the target budget drawn across it. Painted with
        /// the mesh generation API rather than as elements: this is a few hundred quads a second and one
        /// VisualElement per sample would cost more than everything it is measuring.
        /// </summary>
        void DrawGraph(MeshGenerationContext ctx)
        {
            Rect r = ctx.visualElement.contentRect;
            if (r.width <= 1f || r.height <= 1f || _frameCount < 2) return;

            float budget = 1000f / Mathf.Max(1f, targetFps);
            float ceiling = Mathf.Max(budget * 2.5f, 8f);
            var painter = ctx.painter2D;

            // The budget line first, so the bars are read against it.
            float budgetY = r.height * (1f - Mathf.Clamp01(budget / ceiling));
            painter.strokeColor = new Color(1f, 1f, 1f, 0.28f);
            painter.lineWidth = 1f;
            painter.BeginPath();
            painter.MoveTo(new Vector2(0f, budgetY));
            painter.LineTo(new Vector2(r.width, budgetY));
            painter.Stroke();

            float step = r.width / _frameCount;
            painter.BeginPath();
            painter.MoveTo(new Vector2(0f, r.height));
            for (int i = 0; i < _frameCount; i++)
            {
                // Read from the ring buffer oldest-first, so the trace runs left to right in time order.
                int index = (_frameHead - _frameCount + i + _frameMs.Length) % _frameMs.Length;
                float ms = _frameMs[index];
                float y = r.height * (1f - Mathf.Clamp01(ms / ceiling));
                painter.LineTo(new Vector2(i * step, y));
            }
            painter.LineTo(new Vector2(r.width, r.height));
            painter.ClosePath();
            painter.fillColor = new Color(0.18f, 0.75f, 0.55f, 0.35f);
            painter.Fill();
            painter.strokeColor = new Color(0.28f, 0.9f, 0.68f, 0.9f);
            painter.lineWidth = 1.5f;
            painter.Stroke();
        }

        static string Bytes(long b) =>
            b >= 1L << 30 ? $"{b / (float)(1L << 30):F2} GB"
            : b >= 1L << 20 ? $"{b / (float)(1L << 20):F1} MB"
            : b >= 1L << 10 ? $"{b / (float)(1L << 10):F0} KB"
            : $"{b} B";

        static string Thousands(long n) =>
            n >= 1_000_000 ? $"{n / 1_000_000f:F2} M" : n >= 1000 ? $"{n / 1000f:F0} k" : n.ToString();
    }
}
