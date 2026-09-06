using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using PoDecath.Diag;

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
    /// telemetry has found, so a policy running with a mis-scaled observation is visible from the corner
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

            SetText(_title, string.IsNullOrWhiteSpace(titleText) ? Application.productName.ToUpperInvariant()
                                                                 : titleText.ToUpperInvariant());
            SetText(_version, VersionLine());

            if (_menu != null) _menu.clicked += OnMenu;
            if (_debug != null) _debug.clicked += OnDebug;

            // Already on the menu: leave the button in place so the layout is the same on every screen,
            // but disabled, because a MENU that reloads the menu is a way to lose a half-built roster.
            if (_menu != null && SceneManager.GetActiveScene().name == menuSceneName) _menu.SetEnabled(false);

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
            if (_telemetry == null) _telemetry = FindFirstObjectByType<TelemetryOverlay>(FindObjectsInactive.Include);
            if (_agents == null) _agents = FindFirstObjectByType<AgentTelemetry>(FindObjectsInactive.Include);
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
            SetText(_fps, $"{_shownFps:F0} FPS");
            if (_fps != null)
            {
                bool good = _shownFps >= targetFps * 0.95f;
                bool bad = _shownFps < targetFps * 0.66f;
                _fps.EnableInClassList("frame-fps--good", good);
                _fps.EnableInClassList("frame-fps--warn", !good && !bad);
                _fps.EnableInClassList("frame-fps--bad", bad);
            }

            if (_debug != null)
            {
                AgentTelemetry.Grade g = _agents != null ? _agents.HeadlineGrade : AgentTelemetry.Grade.Neutral;
                _debug.EnableInClassList("frame-btn--warn", g == AgentTelemetry.Grade.Warn);
                _debug.EnableInClassList("frame-btn--bad", g == AgentTelemetry.Grade.Bad);
                _debug.EnableInClassList("frame-btn--open", _telemetry != null && _telemetry.ScreenVisible);
                _debug.text = g == AgentTelemetry.Grade.Bad ? "DEBUG !" : "DEBUG";
            }
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

        void OnMenu()
        {
            // Slow motion is a HUD toggle that outlives the scene it was set in, and arriving at the menu
            // running at half speed reads as a hang.
            Time.timeScale = 1f;
            SceneManager.LoadScene(menuSceneName);
        }
    }
}
