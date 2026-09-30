using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using PoDecath.Diag;
using PoDecath.Sim;
using UnityEngine.InputSystem;

namespace PoDecath.UI
{
    /// <summary>
    /// The frame that is on every screen: game name top-left, frame rate top-centre, MENU top-right,
    /// DEBUG bottom-left, version bottom-right.
    ///
    /// It is one document in every scene rather than five elements repeated in five UXML files, which is
    /// what makes "the same place on every screen" a property of the build instead of a thing somebody
    /// has to keep remembering. The scene builders add it; nothing else has to know it exists.
    ///
    /// Two of the five are live. The frame rate is sampled here rather than read off the diagnostics
    /// overlay, because the overlay is closed almost all of the time and a frame counter that only works
    /// while the profiler is open is not a frame counter. And DEBUG wears the worst grade the agent
    /// telemetry has found as a dot in its corner, so a policy running with a mis-scaled observation is visible from the corner
    /// of the screen without opening anything — which is the difference between a diagnostic somebody
    /// checks and a diagnostic somebody remembers to check.
    /// </summary>
    [DefaultExecutionOrder(160)]
    public class AppFrameView : UiRoot
    {
        /// <summary>
        /// The height of one frame row in reference pixels, matching <c>.frame-row</c> in Theme.uss.
        ///
        /// The clearance itself is applied in USS, by the <c>--frame-clear</c> token that
        /// <c>.setup</c>, <c>.control-bar</c> and <c>.lower-third</c> pad themselves by; a C# constant
        /// cannot reach a stylesheet. This is here for code that has to reason about the frame's height —
        /// and as the number to change in both places together, because they cannot check each other.
        /// </summary>
        public const float RowHeight = 112f;

        [Header("Content")]
        [Tooltip("Top-left. Empty means Application.productName.")]
        public string titleText = "";
        [Tooltip("Scene MENU goes to. The setup menu is the game's entry point.")]
        public string menuSceneName = "MAIN";

        [Header("Behaviour")]
        [Tooltip("Frames averaged for the counter. 30 is about half a second and does not flicker.")]
        public int fpsWindow = 30;
        [Tooltip("The budget the counter is graded against: green at or above, amber to two thirds, red under.")]
        public float targetFps = 60f;

        Label _title, _fps, _version;
        Button _menu, _debug;
        string _titleWord;
        bool _onMenu;

        [Tooltip("How long STOP has to be held to end the demo loop. A tap only says so: a kiosk is in a public place.")]
        public float stopHoldSeconds = 1.2f;
        [Tooltip("How long MENU in a live race, and Back on the menu, wait for the second tap that confirms them.")]
        public float confirmSeconds = 3f;

        float _menuArmedUntil = -1f, _quitArmedUntil = -1f, _noteUntil = -1f, _nextTouchNote;
        float _menuDownAt;
        bool _menuHeld, _stoppedByHold, _fpsHinted;
        string _note;
        RaceEvent _race;
        VisualElement _touchRoot;
        VisualElement _debugDot;

        TelemetryOverlay _telemetry;
        AgentTelemetry _agents;

        float _fpsAccum;
        int _fpsFrames;
        float _shownFps;
        float _nextText;

        protected override void Build()
        {
            _title = Find<Label>("title");
            _fps = Find<Label>("fps");
            _version = Find<Label>("version");
            _menu = Find<Button>("menu");
            _debug = Find<Button>("debug");

            _titleWord = string.IsNullOrWhiteSpace(titleText) ? Application.productName.ToUpperInvariant()
                                                              : titleText.ToUpperInvariant();
            SetText(_title, _titleWord);
            SetText(_version, VersionLine());

            if (_menu != null)
            {
                _menu.clicked += OnMenu;
                // STOP is held, not tapped, while the demo runs: the press is timed from here, and Update ends
                // the loop once it has been held long enough, without waiting for the finger to lift.
                _menu.RegisterCallback<PointerDownEvent>(_ => { _menuDownAt = Time.unscaledTime; _menuHeld = true; }, TrickleDown.TrickleDown);
                _menu.RegisterCallback<PointerUpEvent>(_ => _menuHeld = false, TrickleDown.TrickleDown);
                _menu.RegisterCallback<PointerLeaveEvent>(_ => _menuHeld = false);
            }
            // Any touch anywhere, on any screen of this panel: during the demo it says how to take over.
            _touchRoot = Root.panel?.visualTree;
            _touchRoot?.RegisterCallback<PointerDownEvent>(OnAnyTouch, TrickleDown.TrickleDown);
            if (_debug != null)
            {
                _debug.clicked += OnDebug;
                // The grade is a dot in the button's corner rather than a "!" in its word, so the word stays
                // one width and the colour does the reporting.
                _debugDot = new VisualElement { pickingMode = PickingMode.Ignore };
                _debugDot.AddToClassList("frame-dot");
                _debug.Add(_debugDot);
                _debug.AddToClassList("frame-btn--dotted");
            }
            // The frame rate is a door as well as a reading: a tap opens the diagnostics, whose LIVE page is
            // the one place a phone can see draw calls, memory and the 1% low.
            if (_fps != null)
            {
                _fps.pickingMode = PickingMode.Position;
                _fps.RegisterCallback<ClickEvent>(_ => OnFps());
            }

            // Already on the menu, the corner is DEMO: a MENU that reloads the menu would only be a way to
            // lose a half-built roster, and the slot was a greyed-out button. While the demo runs it is STOP on
            // every screen. Words set in DemoLabels, every refresh.
            _onMenu = SceneManager.GetActiveScene().name == menuSceneName;
            DemoLabels();

            Rewire();
        }

        /// <summary>
        /// Finds the diagnostics panel and the agent sampler. Deferred out of Build and retried, because
        /// the frame's document can come up before theirs — UIDocument order is the scene's order — and a
        /// DEBUG button that decided on the first frame that there was nothing to open stays broken for
        /// the whole session.
        /// </summary>
        void Rewire()
        {
            if (_telemetry == null) _telemetry = FindAnyObjectByType<TelemetryOverlay>(FindObjectsInactive.Include);
            if (_agents == null) _agents = FindAnyObjectByType<AgentTelemetry>(FindObjectsInactive.Include);
            if (_race == null) _race = FindAnyObjectByType<RaceEvent>();
        }

        void OnDisable()
        {
            // The panel outlives this scene (one PanelSettings for the whole game), so the touch hook must go with it.
            _touchRoot?.UnregisterCallback<PointerDownEvent>(OnAnyTouch, TrickleDown.TrickleDown);
            _touchRoot = null;
        }

        /// <summary>
        /// Version and build number, which is the whole reason this is in the corner of every screenshot:
        /// a screenshot of a build nobody can identify is not evidence of anything.
        /// </summary>
        static string VersionLine()
        {
            string v = Application.version;
            return string.IsNullOrEmpty(v) ? "dev" : $"v{v}";
        }

        protected override void Update()
        {
            base.Update();

            float now = Time.unscaledTime;
            if (DemoMode.Active && _menuHeld && now - _menuDownAt >= stopHoldSeconds)
            {
                _menuHeld = false;
                _stoppedByHold = true;   // the release that follows is not a MENU tap
                StopDemo();
            }
            if (_menuArmedUntil > 0f && now > _menuArmedUntil) { _menuArmedUntil = -1f; DemoLabels(); }
            if (_noteUntil > 0f && now > _noteUntil) { _noteUntil = -1f; DemoLabels(); }
            // Android's Back arrives as Escape.
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) OnBack();

            _fpsAccum += Time.unscaledDeltaTime;
            _fpsFrames++;
            if (_fpsFrames >= Mathf.Max(5, fpsWindow))
            {
                _shownFps = _fpsAccum > 0f ? _fpsFrames / _fpsAccum : 0f;
                _fpsAccum = 0f;
                _fpsFrames = 0;
            }

            if (Time.unscaledTime < _nextText) return;
            _nextText = Time.unscaledTime + 0.25f;

            Rewire();
            DemoLabels();
            SetText(_fps, Chip(_shownFps));
            if (!_fpsHinted && _onMenu && _shownFps > 0f)
                _fpsHinted = Hints.Seen("fps") || Hints.ShowOnce("fps", _fps, "Tap the frame rate for live stats");
            if (_fps != null)
            {
                // Grey until there is a reading: a red "--" on the menu read as a fault when nothing was wrong.
                bool measured = _shownFps > 0f;
                bool good = measured && _shownFps >= targetFps * 0.95f;
                bool bad = measured && _shownFps < targetFps * 0.66f;
                _fps.EnableInClassList("frame-fps--good", good);
                _fps.EnableInClassList("frame-fps--warn", measured && !good && !bad);
                _fps.EnableInClassList("frame-fps--bad", bad);
            }

            if (_debug != null)
            {
                AgentTelemetry.Grade g = _agents != null ? _agents.HeadlineGrade : AgentTelemetry.Grade.Neutral;
                _debugDot?.EnableInClassList("frame-dot--good", g == AgentTelemetry.Grade.Good);
                _debugDot?.EnableInClassList("frame-dot--warn", g == AgentTelemetry.Grade.Warn);
                _debugDot?.EnableInClassList("frame-dot--bad", g == AgentTelemetry.Grade.Bad);
                _debug.EnableInClassList("frame-btn--open", _telemetry != null && _telemetry.ScreenVisible);
            }
        }

        /// <summary>
        /// The device chip: frame rate and, on a device that reports one, the battery, which is the
        /// cheapest thermal proxy there is without a vendor plug-in: a race that costs 3% is a race that is
        /// cooking the phone. The frame time it used to print as well is one tap away, on the LIVE page it
        /// opens, and without it the chip is half as wide.
        /// </summary>
        static string Chip(float fps)
        {
            string s = fps > 0f ? $"{fps:F0} FPS" : "-- FPS";
            float battery = SystemInfo.batteryLevel;
            if (battery >= 0f) s += $"  ·  {battery * 100f:F0}%";
            return s;
        }

        void OnDebug()
        {
            Rewire();
            if (_telemetry == null)
            {
                Debug.LogWarning("[AppFrameView] No TelemetryOverlay in this scene; DEBUG has nothing to open.", this);
                return;
            }
            _telemetry.SetScreenVisible(!_telemetry.ScreenVisible);
        }

        void OnFps()
        {
            Rewire();
            if (_telemetry == null) return;
            if (_telemetry.ScreenVisible) _telemetry.SetScreenVisible(false);
            else _telemetry.Open(TelemetryOverlay.Page.Live);
        }

        /// <summary>
        /// The corner button's word and the title chip, from the demo's state: DEMO on the menu, STOP while the
        /// demo runs (with the chip saying which event of how many, and counting the results card down to the
        /// next one), MENU otherwise.
        /// </summary>
        void DemoLabels()
        {
            bool armed = _menuArmedUntil > 0f;
            if (_menu != null) _menu.text = DemoMode.Active ? (_menuHeld ? "HOLD" : "STOP") : armed ? "SURE?" : _onMenu ? "DEMO" : "MENU";
            _menu?.EnableInClassList("frame-btn--demo", DemoMode.Active || armed);
            if (_noteUntil > 0f) { SetText(_title, _note); return; }
            if (!DemoMode.Active) { SetText(_title, _titleWord); return; }
            float next = DemoMode.SecondsToNext;
            SetText(_title, next >= 0f ? $"DEMO · {DemoMode.Next.label} IN {Mathf.CeilToInt(next)}"
                                       : $"DEMO {DemoMode.Index + 1}/{DemoMode.Count} · {DemoMode.Current.label}");
        }

        /// <summary>
        /// The corner button. On the menu: DEMO starts the kiosk loop. During the loop: STOP, which ends it
        /// only when held (see Update); a tap just says so. In a live race: MENU asks SURE? first, the way
        /// RESTART does, because it throws away more than RESTART. Otherwise: back to the menu.
        /// </summary>
        void OnMenu()
        {
            if (_stoppedByHold) { _stoppedByHold = false; return; }
            if (DemoMode.Active) { Note("HOLD STOP TO END DEMO"); return; }
            if (_onMenu)
            {
                var setup = FindAnyObjectByType<SetupView>();
                if (setup != null) setup.StartDemo();
                else Debug.LogWarning("[AppFrameView] DEMO pressed on a menu with no SetupView.", this);
                return;
            }

            bool live = _race != null && (_race.Current == RaceEvent.Phase.Running || _race.Current == RaceEvent.Phase.Countdown);
            if (live && _menuArmedUntil < 0f)
            {
                _menuArmedUntil = Time.unscaledTime + confirmSeconds;
                DemoLabels();
                return;
            }
            _menuArmedUntil = -1f;
            SceneLoader.Load(menuSceneName, "Menu");
        }

        /// <summary>
        /// Where the frame's two rows are, in panel units: the bottom of the top row, the top of the bottom
        /// row, and the panel's height. The highlight clip crops to the picture between them. False before layout.
        /// </summary>
        public bool FrameRows(out float topRowBottom, out float bottomRowTop, out float panelHeight)
        {
            VisualElement top = Root?.Q("frame-top"), bottom = Root?.Q("frame-bottom");
            topRowBottom = top != null ? top.worldBound.yMax : float.NaN;
            bottomRowTop = bottom != null ? bottom.worldBound.yMin : float.NaN;
            panelHeight = Root?.panel != null ? Root.panel.visualTree.worldBound.height : float.NaN;
            return !float.IsNaN(topRowBottom) && !float.IsNaN(bottomRowTop) && !float.IsNaN(panelHeight) && topRowBottom < bottomRowTop;
        }

        /// <summary>Ends the demo: on the menu it stays there, anywhere else the menu comes back with that field. Also what the demo check calls.</summary>
        public void StopDemo()
        {
            DemoMode.Stop();
            if (_onMenu) DemoLabels();
            else SceneLoader.Load(menuSceneName, "Menu");
        }

        /// <summary>A line in the title chip for a few seconds: how to take over the demo, or that Back needs a second press.</summary>
        void Note(string text)
        {
            _note = text;
            _noteUntil = Time.unscaledTime + confirmSeconds;
            DemoLabels();
        }

        void OnAnyTouch(PointerDownEvent e)
        {
            if (!DemoMode.Active || Time.unscaledTime < _nextTouchNote) return;
            if (e.target is VisualElement v && _menu != null && (v == _menu || _menu.Contains(v))) return;
            _nextTouchNote = Time.unscaledTime + 20f;
            Note("HOLD STOP TO TAKE OVER");
        }

        /// <summary>
        /// The phone's Back, which did nothing anywhere. Closes whatever is open first (diagnostics, the chaos
        /// buttons, a turned-over tile, the replay), then behaves as MENU (so a live race asks SURE? first), and
        /// on the menu asks for a second press before leaving the app. During the demo it only says how to
        /// take over: a kiosk's Back is not a way to end it.
        /// </summary>
        public void OnBack()
        {
            Rewire();
            if (_telemetry != null && _telemetry.ScreenVisible) { _telemetry.SetScreenVisible(false); return; }
            var hud = FindAnyObjectByType<HudView>();
            if (hud != null && hud.ChaosOpen) { hud.SetChaosOpen(false); return; }
            var setup = FindAnyObjectByType<SetupView>();
            if (setup != null && setup.CloseCards()) return;
            var results = FindAnyObjectByType<ResultsView>();
            if (results != null && results.StopWatching()) return;
            if (DemoMode.Active) { Note("HOLD STOP TO END DEMO"); return; }
            if (!_onMenu) { OnMenu(); return; }

            if (_quitArmedUntil > Time.unscaledTime) { Application.Quit(); return; }
            _quitArmedUntil = Time.unscaledTime + confirmSeconds;
            Note("BACK AGAIN TO LEAVE");
        }
    }
}
