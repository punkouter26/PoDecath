using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using PoDecath.Cam;
using PoDecath.Sim;

namespace PoDecath.UI
{
    /// <summary>
    /// The in-race HUD in UI Toolkit: the developer stats card, and the control bar under it.
    ///
    /// In a broadcast scene the card starts put away and the overlay is the picture; the card is what you
    /// open to judge how a policy is actually running. In the hands-on development scenes it is up from the
    /// start and the bar carries slow-motion and the camera toggle as well.
    ///
    /// Text refresh runs at 10 Hz, and stops entirely while the card is closed — there is no point building
    /// strings for something nobody is looking at.
    /// </summary>
    [DefaultExecutionOrder(128)]
    public class HudView : UiRoot
    {
        [Header("Wiring")]
        [Tooltip("When set, the HUD shows the event instead of the arena episode loop.")]
        public RaceEvent dash;
        public PolicyRunner runner;
        public CameraRig cameraRig;
        public TouchPerturbation perturbation;
        [Tooltip("Broadcast scenes: the GUST / SHOVE / SLICK buttons. Without it the chaos bar stays hidden.")]
        public ViewerChaos chaos;
        [Tooltip("Broadcast scenes: the camera picker cycles this director's viewer camera.")]
        public BroadcastDirector director;

        [Header("Layout")]
        [Tooltip("Broadcast scenes hide the card until it is asked for; the dev scenes leave it up.")]
        public bool statsHiddenAtStart = true;
        [Tooltip("Hands-on scenes get slow motion and a camera toggle. A broadcast scene gets neither.")]
        public bool handsOn = false;
        public string menuSceneName = "MAIN";

        Label _model, _speed, _distance, _stability, _attempt, _lastResult;
        VisualElement _statsCard;
        Button _restart, _stats, _slowMo, _camera, _menu;

        VisualElement _chaosBar;
        Button _gust, _shove, _slick, _cam;
        Label _gustLabel, _shoveLabel, _slickLabel, _camLabel;

        bool _slow;
        float _nextRefresh;

        /// <summary>Whether the developer card is open. The results modal reads this before hiding it.</summary>
        public bool StatsOpen => _statsCard != null && !_statsCard.ClassListContains("hidden");

        /// <summary>
        /// The top edge of everything this HUD has along the bottom of the screen (the control row, and the
        /// stats strip when it is open), in panel pixels; NaN before the first layout or while hidden.
        ///
        /// Every document here shares one PanelSettings, so they are one panel and their world
        /// coordinates are directly comparable. The broadcast overlay used to guess this height as a
        /// constant (352 px, "cannot be measured from here"); it can, and it is what the lower third now
        /// sits on, so opening STATS lifts the lower third instead of drawing the badges over it.
        /// </summary>
        public float ControlsTop
        {
            get
            {
                if (!ScreenVisible) return float.NaN;
                VisualElement bar = Root?.Q<VisualElement>("control-bar");
                if (bar == null || float.IsNaN(bar.worldBound.yMin) || bar.worldBound.height < 1f) return float.NaN;
                float top = bar.worldBound.yMin + bar.resolvedStyle.paddingTop;
                if (StatsOpen && _statsCard.worldBound.height > 1f) top = Mathf.Min(top, _statsCard.worldBound.yMin);
                return top;
            }
        }

        protected override void Build()
        {
            _statsCard = Find<VisualElement>("stats-card");
            _model = Find<Label>("model");
            _speed = Find<Label>("speed");
            _distance = Find<Label>("distance");
            _stability = Find<Label>("stability");
            _attempt = Find<Label>("attempt");
            _lastResult = Find<Label>("last-result");

            _restart = Find<Button>("restart");
            _stats = Find<Button>("stats");
            _slowMo = Find<Button>("slowmo");
            _camera = Find<Button>("camera");
            _menu = Find<Button>("menu");

            if (_restart != null) _restart.clicked += OnRestart;
            if (_stats != null) _stats.clicked += ToggleStats;
            if (_slowMo != null) _slowMo.clicked += () => SetSlowMo(!_slow);
            if (_camera != null) _camera.clicked += OnCamera;
            if (_menu != null) _menu.clicked += OnMenu;

            // Slow motion and the camera toggle are development controls; a broadcast scene has a director
            // and no reason to offer either.
            Show(_slowMo, handsOn);
            Show(_camera, handsOn && cameraRig != null);
            Show(_stats, !handsOn);
            Show(_statsCard, !statsHiddenAtStart);

            // The frame carries a MENU in the top right of every screen in the game. Where it is present
            // this one is a second button, in a second place, doing the same job -- and until both were
            // pointed at the same scene it was not even the same job. One control, one destination.
            Show(_menu, FindFirstObjectByType<AppFrameView>(FindObjectsInactive.Include) == null);

            if (dash != null) dash.RaceFinished += OnRaceFinished;
            if (perturbation != null) perturbation.enabled = SessionSettings.PerturbationEnabled;

            BuildChaosBar();
            SetSlowMo(false);
            Refresh();
        }

        /// <summary>
        /// The viewer's buttons. The three disturbances go through <see cref="ViewerChaos"/>, which owns the
        /// physics and the cooldowns; the camera picker cycles the director's viewer camera. Both are
        /// optional, and the bar is only on the picture when at least one of them is wired.
        /// </summary>
        void BuildChaosBar()
        {
            _chaosBar = Find<VisualElement>("chaos-bar");
            _gust = Find<Button>("gust");
            _shove = Find<Button>("shove");
            _slick = Find<Button>("slick");
            _cam = Find<Button>("cam");
            _gustLabel = Find<Label>("gust-label");
            _shoveLabel = Find<Label>("shove-label");
            _slickLabel = Find<Label>("slick-label");
            _camLabel = Find<Label>("cam-label");

            if (_gust != null) _gust.clicked += () => chaos?.Fire(ViewerChaos.Act.Gust);
            if (_shove != null) _shove.clicked += () => chaos?.Fire(ViewerChaos.Act.Shove);
            if (_slick != null) _slick.clicked += () => chaos?.Fire(ViewerChaos.Act.Slick);
            if (_cam != null) _cam.clicked += OnViewerCam;

            Show(_chaosBar, !handsOn && (chaos != null || director != null));
            Show(_gust, chaos != null);
            Show(_shove, chaos != null);
            Show(_slick, chaos != null);
            Show(_cam, director != null);
            RefreshChaos();
        }

        void OnViewerCam()
        {
            if (director == null) return;
            BroadcastDirector.ViewerCam v = director.CycleViewerCam();
            SetText(_camLabel, v.ToString().ToUpperInvariant());
        }

        /// <summary>Greys a button out while it cannot fire, and counts down its cooldown in its label.</summary>
        void RefreshChaos()
        {
            // Read back rather than set on press: the director's pick can change from elsewhere (a scene
            // reload, a test), and a button that names the wrong camera is worse than none.
            if (director != null) SetText(_camLabel, director.Viewer.ToString().ToUpperInvariant());
            if (chaos == null) return;
            Chaos(_gust, _gustLabel, ViewerChaos.Act.Gust, "GUST");
            Chaos(_shove, _shoveLabel, ViewerChaos.Act.Shove, "SHOVE");
            Chaos(_slick, _slickLabel, ViewerChaos.Act.Slick, "SLICK");
        }

        void Chaos(Button b, Label l, ViewerChaos.Act act, string word)
        {
            if (b == null) return;
            b.SetEnabled(chaos.CanFire(act));
            float left = chaos.CooldownLeft(act);
            SetText(l, left > 0f ? $"{word} {Mathf.CeilToInt(left)}" : word);
        }

        void OnDisable()
        {
            if (dash != null) dash.RaceFinished -= OnRaceFinished;
        }

        /// <summary>
        /// Opens or closes the developer card. In the broadcast scenes it is the second view of a race, not
        /// the first: the overlay carries the event, and this is the answer to "but how is it actually
        /// doing?".
        /// </summary>
        public void ToggleStats()
        {
            if (_statsCard == null) return;
            bool show = _statsCard.ClassListContains("hidden");
            Show(_statsCard, show);
            if (show) Refresh();
        }

        /// <summary>Used by the results card, which puts the HUD away and then puts back what it took.</summary>
        public void SetStatsVisible(bool visible) => Show(_statsCard, visible);

        protected override void Update()
        {
            base.Update();
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.1f;
            RefreshChaos();
            if (!StatsOpen) return;   // nothing to rebuild while it is put away
            Refresh();
        }

        void Refresh()
        {
            if (dash != null)
            {
                RaceEvent.Athlete r = dash.Reference;
                string model = r != null && r.runner != null ? r.runner.ModelName : (r != null ? r.name : "-");

                // Badges: the number and its unit, nothing else. The key words the card used to print
                // ("Speed", "Distance") are what the units already say, and the phase moves up to the model
                // line, which is the one place on the strip with room for a word.
                bool fell = r != null && r.fell;
                SetText(_model, $"{(r != null ? r.name : "event")}  ·  {model}  ·  {(fell ? "FELL" : dash.Status)}");
                SetText(_speed, $"{dash.Speed:F2} m/s");
                SetText(_distance, dash.HudDistanceBadge());
                SetText(_stability,
                        fell ? "fell" : $"{dash.Stability * 100f:F0}% up",
                        fell ? new Color(1f, 0.3f, 0.25f)
                             : dash.Stability < 0.6f ? new Color(1f, 0.8f, 0.3f) : Color.white);
                SetText(_attempt, $"#{dash.Attempt + 1}");
                return;
            }

            if (runner != null)
                SetText(_model, $"{runner.ModelName}  |  {(runner.config != null ? runner.config.ControlHz : 0f):F0} Hz");
        }

        void OnRaceFinished(string summary)
        {
            if (_lastResult == null) return;
            // A full field writes a result line per runner, which overruns the card. When the event ranked
            // a field, show the winner and the finisher count and leave the board to the modal.
            SetText(_lastResult, dash != null && dash.Results.Count > 1
                ? dash.HudResultLine(dash.Results)
                : "Last: " + summary);
        }

        void OnRestart()
        {
            if (dash != null) dash.RestartNow();
        }

        void SetSlowMo(bool slow)
        {
            _slow = slow;
            Time.timeScale = slow ? 0.5f : 1f;
            if (_slowMo != null) _slowMo.text = slow ? "1.0x" : "0.5x";
        }

        void OnCamera()
        {
            if (cameraRig == null) return;
            cameraRig.Toggle();
            if (_camera != null) _camera.text = cameraRig.ActiveName;
        }

        void OnMenu()
        {
            Time.timeScale = 1f;
            SceneManager.LoadScene(menuSceneName);
        }
    }
}
