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
        public RaceEvent race;
        [Tooltip("Optional; adds what the crowd is doing.")]
        public RaceAudio audioMix;
        [Tooltip("The agent sampler behind the AGENTS page. Found in the scene if this is left empty.")]
        public AgentTelemetry agents;

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
        string _exportNote = "";
        float _exportNoteUntil;
        readonly Dictionary<string, Label> _values = new Dictionary<string, Label>();

        // AGENTS page. The cards are kept and rewritten rather than rebuilt, for the same reason the FRAME
        // rows are: a diagnostic that allocates a hundred VisualElements a second is measuring itself.
        VisualElement _livePage, _frameSection, _settingsPage, _agentList, _agentDetail, _agentCardHost;
        Label _tierNote, _settingsInfo;
        Button _tabLive, _tabSettings, _export, _agentBack;
        Button _tierMobile, _tierPc, _fps30, _fps60;
        readonly List<AgentRow> _agentRows = new List<AgentRow>();
        AgentCard _detail;
        int _selected = -1;   // the athlete whose full card is open, or -1 for the list

        /// <summary>
        /// The two pages of the sheet. LIVE is the athletes and, under them, the device; they were two pages
        /// (AGENTS and FRAME) and the first was half empty.
        /// </summary>
        public enum Page { Live, Settings }
        Page _page = Page.Live;

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

            _livePage = Find<VisualElement>("live-page");
            _frameSection = Find<VisualElement>("frame-section");
            _settingsPage = Find<VisualElement>("settings-page");
            _agentList = Find<VisualElement>("agents");
            _agentDetail = Find<VisualElement>("agent-detail");
            _agentCardHost = Find<VisualElement>("agent-card");
            _agentBack = Find<Button>("agent-back");
            _tabSettings = Find<Button>("tab-settings");
            _tabLive = Find<Button>("tab-live");
            _export = Find<Button>("export");

            if (_tabLive != null) _tabLive.clicked += () => ShowPage(Page.Live);
            if (_tabSettings != null) _tabSettings.clicked += () => ShowPage(Page.Settings);
            if (_agentBack != null) _agentBack.clicked += () => Select(-1);
            if (_agentCardHost != null) { _detail = new AgentCard(); _agentCardHost.Add(_detail.root); }
            if (_export != null) _export.clicked += OnExport;

            _frameMs = new float[Mathf.Max(30, history)];
            if (_graph != null) _graph.generateVisualContent += DrawGraph;

            StartRecorders();
            BuildRows();
            BuildSettings();
            ShowPage(Page.Live);
            SetScreenVisible(startOpen);
        }

        /// <summary>
        /// Switches between the live readings and the settings. LIVE is the landing page, athletes first:
        /// this project's frame rate has been fine for weeks, and what costs it time is a policy nobody can
        /// see the shape of.
        /// </summary>
        void ShowPage(Page page)
        {
            _page = page;
            Show(_livePage, page == Page.Live);
            Show(_settingsPage, page == Page.Settings);
            _tabLive?.EnableInClassList("tab--on", page == Page.Live);
            _tabSettings?.EnableInClassList("tab--on", page == Page.Settings);
            _nextRefresh = 0f;
            if (page == Page.Settings) RefreshSettings();
        }

        /// <summary>Opens the sheet on a given page.</summary>
        public void Open(Page page)
        {
            ShowPage(page);
            SetScreenVisible(true);
        }

        // ---------------------------------------------------------------- the settings page

        void BuildSettings()
        {
            _tierMobile = Find<Button>("tier-mobile");
            _tierPc = Find<Button>("tier-pc");
            _fps30 = Find<Button>("fps-30");
            _fps60 = Find<Button>("fps-60");
            _tierNote = Find<Label>("tier-note");
            _settingsInfo = Find<Label>("settings-info");

            // Applied on the tap, not on a later START, so the effect is visible behind the sheet.
            if (_tierMobile != null) _tierMobile.clicked += () => { RenderTier.Set(Tier.Mobile); RefreshSettings(); };
            if (_tierPc != null) _tierPc.clicked += () => { RenderTier.Set(Tier.PC); RefreshSettings(); };
            if (_fps30 != null) _fps30.clicked += () => SetFrameRate(30);
            if (_fps60 != null) _fps60.clicked += () => SetFrameRate(60);
        }

        void SetFrameRate(int fps)
        {
            SessionSettings.TargetFrameRate = fps;
            SessionSettings.Save();
            SessionSettings.ApplyQuality();
            RefreshSettings();
        }

        void RefreshSettings()
        {
            bool mobile = RenderTier.Current == Tier.Mobile;
            _tierMobile?.EnableInClassList("event-btn--selected", mobile);
            _tierPc?.EnableInClassList("event-btn--selected", !mobile);
            _fps30?.EnableInClassList("event-btn--selected", SessionSettings.TargetFrameRate < 60);
            _fps60?.EnableInClassList("event-btn--selected", SessionSettings.TargetFrameRate >= 60);
            SetText(_tierNote, mobile
                ? "Mobile: 0.8 render scale, one shadow cascade, blob shadows, the building's lighter model."
                : "PC: full resolution, MSAA 4x, soft shadows, SSAO, depth of field.");
            PolicyLibrary library = PolicyLibrary.Load();
            int count = library != null ? library.entries.Count : 0;
            SetText(_settingsInfo, $"{count} policy checkpoint(s) in the build, run on the Unity Inference Engine "
                                 + "(CPU). Each athlete runs its own; the roster decides which.");
        }

        void OnExport()
        {
            AgentTelemetry sampler = Sampler();
            string path = sampler != null ? sampler.WriteSessionSummary("manual") : null;
            // Said on the summary line for a few seconds, then the line goes back to the field.
            _exportNote = sampler == null ? "Nothing to export: no agent sampler in this scene."
                        : string.IsNullOrEmpty(path) ? "Export failed; see the log."
                        : $"Written to {path}";
            _exportNoteUntil = Time.unscaledTime + 6f;
            _nextRefresh = 0f;
        }

        /// <summary>
        /// The sampler, found late if it was not wired. Same reason as the frame's own late lookup: the
        /// sampler is a separate object and scene component order is not something this can depend on.
        /// </summary>
        AgentTelemetry Sampler()
        {
            if (agents == null) agents = FindAnyObjectByType<AgentTelemetry>(FindObjectsInactive.Include);
            return agents;
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
            AddRow("Thermal");
            if (SystemInfo.batteryLevel >= 0f) AddRow("Battery");
        }

        void AddRow(string key)
        {
            // A cell in a four-across grid, the number over its name, rather than a full-width row: the
            // counters share the LIVE page with the athletes now, and seventeen rows would not fit under them.
            var row = new VisualElement();
            row.AddToClassList("frame-cell");
            var v = new Label("-");
            v.AddToClassList("telemetry-value");
            var k = new Label(key);
            k.AddToClassList("telemetry-key");
            row.Add(v);
            row.Add(k);
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
            if (_page == Page.Settings)
            {
                RefreshSettings();
                SetText(_footer, $"{RenderTier.Current} tier  ·  {QualitySettings.names[QualitySettings.GetQualityLevel()]} quality  ·  "
                               + $"{Screen.width}x{Screen.height}  ·  {Application.targetFrameRate} fps target  ·  DEBUG or F3 closes");
                return;
            }
            RefreshAgents();
            RefreshFrame();
        }

        /// <summary>The device half of the LIVE page: every frame counter, graded against the budget.</summary>
        void RefreshFrame()
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
            Set("Thermal", ThermalGovernor.Summary, ThermalGovernor.SummaryGrade);
            if (SystemInfo.batteryLevel >= 0f)
                Set("Battery", $"{SystemInfo.batteryLevel * 100f:F0}%  {SystemInfo.batteryStatus}", SystemInfo.batteryLevel < 0.2f ? "warn" : null);
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
            foreach (RaceEvent.Athlete a in race.Athletes)
            {
                if (a.IsRL) rl++;
                if (a.fell) down++;
            }
            Set("Athletes", $"{total}  ({rl} RL, {down} down)");
        }

        static int CountAudibleSources()
        {
            AudioSource[] all = FindObjectsByType<AudioSource>();
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

        // ---------------------------------------------------------------- the agents page

        /// <summary>
        /// Rewrites the summary line and either the list (one line per athlete) or the one athlete's full card
        /// that is open.
        ///
        /// Rows are matched to agents by position and only created when the field grows, so a restart does
        /// not rebuild them, and the open card follows its athlete by index across a restart too.
        /// </summary>
        void RefreshAgents()
        {
            AgentTelemetry sampler = Sampler();
            if (_agentList == null) return;

            if (sampler == null)
            {
                Summary("No agent sampler in this scene. Rebuild it from PoDecath/Build Everything.", AgentTelemetry.Grade.Neutral);
                for (int i = 0; i < _agentRows.Count; i++) Show(_agentRows[i].root, false);
                Select(-1);
                return;
            }

            IReadOnlyList<AgentTelemetry.Agent> field = sampler.Agents;
            if (_selected >= field.Count) Select(-1);

            bool detail = _selected >= 0;
            Show(_agentList, !detail);
            Show(_agentDetail, detail);
            // One athlete's card is most of the sheet; the frame counters step aside for it and come back
            // with BACK. Both at once squeezed the card's trend over its own numbers.
            Show(_frameSection, !detail);

            // The field in one sentence. When the worst finding is one athlete's, the sentence is on that
            // athlete's card and its line carries a "!"; this only says how many lines to look at.
            int flagged = 0;
            foreach (AgentTelemetry.Agent a in field) if (a.grade == AgentTelemetry.Grade.Bad) flagged++;
            Summary(sampler.HeadlineGrade == AgentTelemetry.Grade.Bad && flagged > 0
                        ? $"{field.Count} athletes, last {sampler.HistorySeconds:F0} s  ·  {flagged} marked ! need a look: tap one for what to change."
                        : $"{field.Count} athletes, last {sampler.HistorySeconds:F0} s  ·  {sampler.Headline}",
                    sampler.HeadlineGrade);

            if (detail)
            {
                _detail?.Write(field[_selected], sampler.HistorySeconds);
                return;
            }

            bool compact = field.Count > 8;

            while (_agentRows.Count < field.Count)
            {
                int index = _agentRows.Count;
                var row = new AgentRow(() => Select(index));
                _agentList.Add(row.root);
                _agentRows.Add(row);
            }

            for (int i = 0; i < _agentRows.Count; i++)
            {
                bool used = i < field.Count;
                Show(_agentRows[i].root, used);
                _agentRows[i].root.EnableInClassList("agent-row--compact", compact);
                if (used) _agentRows[i].Write(field[i]);
            }
        }

        /// <summary>The line under the tabs: an export's result for a few seconds, otherwise the field summary.</summary>
        void Summary(string text, AgentTelemetry.Grade grade)
        {
            bool note = Time.unscaledTime < _exportNoteUntil && _exportNote.Length > 0;
            SetText(_title, note ? _exportNote : text);
            GradeElement(_title, "sheet-summary", note ? AgentTelemetry.Grade.Neutral : grade);
        }

        /// <summary>Opens one athlete's full card, or goes back to the list with -1.</summary>
        void Select(int index)
        {
            _selected = index;
            _nextRefresh = 0f;
        }

        /// <summary>
        /// One athlete on one line: grade, name, speed, falls, uprightness and clamping, the four numbers the
        /// full card's diagnosis leans on hardest. Sixteen of these fit the sheet; the full card is a tap away.
        /// </summary>
        sealed class AgentRow
        {
            internal readonly Button root;
            readonly Label _flag, _name, _speed, _falls, _upright, _clamped;

            internal AgentRow(System.Action onTap)
            {
                root = new Button(onTap);
                root.AddToClassList("agent-row");
                _flag = Cell("agent-row-flag");
                _name = Cell("agent-row-name");
                _speed = Cell("agent-row-cell");
                _falls = Cell("agent-row-cell");
                _upright = Cell("agent-row-cell");
                _clamped = Cell("agent-row-cell");
            }

            Label Cell(string cls)
            {
                var l = new Label("-");
                l.AddToClassList(cls);
                root.Add(l);
                return l;
            }

            internal void Write(AgentTelemetry.Agent a)
            {
                // "!" where the athlete's card has something to say that needs acting on; its sentence is
                // one tap away rather than across the top of the sheet in three lines.
                SetText(_flag, a.grade == AgentTelemetry.Grade.Bad ? "!" : "");
                SetText(_name, a.name);
                SetText(_speed, $"{a.speed:F1} m/s");
                SetText(_falls, a.falls == 1 ? "1 fall" : $"{a.falls} falls");
                SetText(_upright, $"{a.uprightMean:F2} up");
                SetText(_clamped, a.isRL ? $"{a.clampFrac * 100f:F0}% clamp" : "-");
                GradeElement(root, "agent-row", a.grade);
                GradeElement(_falls, "metric-value", a.falls == 0 ? AgentTelemetry.Grade.Good : AgentTelemetry.Grade.Warn);
                GradeElement(_upright, "metric-value", a.uprightMean > 0.9f ? AgentTelemetry.Grade.Good
                                                    : a.uprightMean > 0.7f ? AgentTelemetry.Grade.Warn
                                                                           : AgentTelemetry.Grade.Bad);
            }
        }

        /// <summary>Puts the good/warn/bad modifier of one base class on an element, and takes the others off.</summary>
        static void GradeElement(VisualElement e, string baseClass, AgentTelemetry.Grade grade)
        {
            if (e == null) return;
            e.EnableInClassList(baseClass + "--good", grade == AgentTelemetry.Grade.Good);
            e.EnableInClassList(baseClass + "--warn", grade == AgentTelemetry.Grade.Warn);
            e.EnableInClassList(baseClass + "--bad", grade == AgentTelemetry.Grade.Bad);
        }

        /// <summary>
        /// One athlete's card: name, the ONNX driving it, nine numbers, ninety seconds of speed and
        /// uprightness, and the sentence saying what to change.
        ///
        /// The nine numbers are in a fixed order meant to be read top to bottom as a diagnosis rather than
        /// scanned as a dashboard. The first row is what the athlete did, the second is how well it stayed
        /// on its feet, the third is whether the policy was being run inside the envelope it was trained
        /// in - so a bad number low on the card explains the bad numbers above it, never the other way
        /// round. It is the same worst-cause-first ordering AgentTelemetry.Diagnose uses, laid out in
        /// space instead of in time.
        /// </summary>
        sealed class AgentCard
        {
            internal readonly VisualElement root;

            readonly Label _name, _model, _verdict, _legend;
            readonly VisualElement _trend;
            readonly Label[] _values = new Label[12];

            AgentTelemetry.Agent _agent;

            // Fixed, because the card is scanned rather than read: the eye learns where FALLS is and stops
            // looking at the caption.
            static readonly string[] Keys =
            {
                "SPEED m/s", "PEAK m/s", "DIST m",
                "FALLS", "UPRIGHT", "CONTROL Hz",
                "OBS CLIP", "CLAMPED", "JITTER",
                // Effort, from EffortMeter: the mean and worst joint torque as a fraction of the drive
                // limits the rig was actually configured with, and the mechanical power that implies.
                // Three cells rather than one because the mean and the peak say different things — a body
                // averaging a fair load with nothing saturating is fine, and one joint pinned while the
                // rest idle is a joint limit that does not match what this checkpoint was trained against.
                "STRAIN", "PEAK JOINT", "WATTS",
            };

            internal AgentCard()
            {
                root = new VisualElement();
                root.AddToClassList("agent");

                _name = new Label("-");
                _name.AddToClassList("agent-name");
                root.Add(_name);

                _model = new Label("-");
                _model.AddToClassList("agent-model");
                root.Add(_model);

                var grid = new VisualElement();
                grid.AddToClassList("metric-grid");
                for (int i = 0; i < _values.Length; i++)
                {
                    var cell = new VisualElement();
                    cell.AddToClassList("metric");
                    var v = new Label("-");
                    v.AddToClassList("metric-value");
                    var k = new Label(Keys[i]);
                    k.AddToClassList("metric-key");
                    cell.Add(v);
                    cell.Add(k);
                    grid.Add(cell);
                    _values[i] = v;
                }
                root.Add(grid);

                _trend = new VisualElement();
                _trend.AddToClassList("agent-trend");
                _trend.generateVisualContent += DrawTrend;
                root.Add(_trend);

                _legend = new Label("");
                _legend.AddToClassList("agent-legend");
                root.Add(_legend);

                _verdict = new Label("");
                _verdict.AddToClassList("agent-verdict");
                root.Add(_verdict);
            }

            internal void Write(AgentTelemetry.Agent a, float historySeconds)
            {
                _agent = a;

                SetText(_name, a.name);
                SetText(_model, ModelLine(a));

                Set(0, $"{a.speed:F2}");
                Set(1, $"{a.speedPeak:F2}");
                Set(2, $"{a.distance:F1}");

                Set(3, a.falls.ToString(), a.falls == 0 ? AgentTelemetry.Grade.Good : AgentTelemetry.Grade.Warn);
                Set(4, $"{a.uprightMean:F2}", a.uprightMean > 0.9f ? AgentTelemetry.Grade.Good
                                            : a.uprightMean > 0.7f ? AgentTelemetry.Grade.Warn
                                                                   : AgentTelemetry.Grade.Bad);

                if (!a.isRL || a.configHz <= 0f) Set(5, "-");
                else Set(5, $"{a.controlHz:F0}/{a.configHz:F0}",
                         a.controlHz >= a.configHz * 0.9f ? AgentTelemetry.Grade.Good
                       : a.controlHz >= a.configHz * 0.75f ? AgentTelemetry.Grade.Warn
                                                           : AgentTelemetry.Grade.Bad);

                if (!a.isRL)
                {
                    Set(6, "-"); Set(7, "-"); Set(8, "-");
                }
                else
                {
                    Set(6, $"{a.obsClipFrac * 100f:F1}%", a.obsClipFrac > 0.02f ? AgentTelemetry.Grade.Bad
                                                        : a.obsClipFrac > 0.005f ? AgentTelemetry.Grade.Warn
                                                                                 : AgentTelemetry.Grade.Good);
                    Set(7, $"{a.clampFrac * 100f:F1}%", a.clampFrac > 0.10f ? AgentTelemetry.Grade.Bad
                                                      : a.clampFrac > 0.03f ? AgentTelemetry.Grade.Warn
                                                                            : AgentTelemetry.Grade.Good);
                    Set(8, $"{a.jitter:F2}", a.jitter > 0.35f ? AgentTelemetry.Grade.Warn : AgentTelemetry.Grade.Good);
                }

                if (a.watts <= 0f && a.effort <= 0f)
                {
                    // No EffortMeter on this athlete — a scene built before the meters existed, or one
                    // built with them switched off. Blank rather than three convincing zeroes.
                    Set(9, "-"); Set(10, "-"); Set(11, "-");
                }
                else
                {
                    Set(9, $"{a.effort * 100f:F0}%");
                    Set(10, $"{a.peakSaturation * 100f:F0}%",
                            a.peakSaturation > 0.96f ? AgentTelemetry.Grade.Warn : AgentTelemetry.Grade.Good);
                    Set(11, $"{a.watts:F0}");
                }

                SetText(_legend, $"last {historySeconds:F0} s   ·   green = speed, full height is this "
                               + $"athlete's {Mathf.Max(a.speedPeak, 0.01f):F2} m/s peak   ·   grey = uprightness, "
                               + "1.0 at the top is standing");
                SetText(_verdict, a.verdict);
                GradeElement(root, "agent", a.grade);
                _trend.MarkDirtyRepaint();
            }

            static string ModelLine(AgentTelemetry.Agent a)
            {
                if (!a.isRL) return a.activeModel;
                string recovery = string.IsNullOrEmpty(a.recoveryModel) ? "no get-up model" : $"get-up: {a.recoveryModel}";
                return $"{a.activeModel}   ·   obs {a.obsSize} / act {a.actSize}   ·   {a.inferMs:F2} ms   ·   {recovery}";
            }

            void Set(int i, string value, AgentTelemetry.Grade grade = AgentTelemetry.Grade.Neutral)
            {
                SetText(_values[i], value);
                GradeElement(_values[i], "metric-value", grade);
            }

            /// <summary>
            /// Speed and uprightness over the history window, oldest on the left.
            ///
            /// Both on one graph rather than two, on purpose: the finding this panel exists to make easy is
            /// "it slows down, and then it falls over", and that is a shape you see in one picture and have
            /// to reconstruct from two. Speed is scaled to this athlete's own peak so a slow one is still
            /// legible; uprightness is scaled 0..1 absolutely, because unlike speed it means something at a
            /// fixed height - 1.0 is standing, and the grey line falling off the bottom is the fall.
            /// </summary>
            void DrawTrend(MeshGenerationContext ctx)
            {
                Rect r = ctx.visualElement.contentRect;
                if (_agent == null || _agent.historyCount < 2 || r.width <= 1f || r.height <= 1f) return;

                int n = _agent.historyCount;
                float step = r.width / (n - 1);
                var painter = ctx.painter2D;

                // Uprightness first and underneath: it is the context the speed line is read against.
                painter.strokeColor = new Color(1f, 1f, 1f, 0.45f);
                painter.lineWidth = 1.5f;
                painter.BeginPath();
                for (int i = 0; i < n; i++)
                {
                    float u = Mathf.Clamp01(_agent.Sample(_agent.uprightHistory, i));
                    var p = new Vector2(i * step, r.height * (1f - u));
                    if (i == 0) painter.MoveTo(p); else painter.LineTo(p);
                }
                painter.Stroke();

                float peak = Mathf.Max(_agent.speedPeak, 0.5f);
                painter.strokeColor = new Color(0.28f, 0.9f, 0.68f, 0.95f);
                painter.lineWidth = 2f;
                painter.BeginPath();
                for (int i = 0; i < n; i++)
                {
                    float v = Mathf.Clamp01(_agent.Sample(_agent.speedHistory, i) / peak);
                    var p = new Vector2(i * step, r.height * (1f - v));
                    if (i == 0) painter.MoveTo(p); else painter.LineTo(p);
                }
                painter.Stroke();
            }
        }

    }
}
