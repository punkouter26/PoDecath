using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using PoDecath.Cam;
using PoDecath.Sim;

namespace PoDecath.UI
{
    /// <summary>
    /// The in-race HUD in UI Toolkit: one row of controls along the bottom, the viewer's disturbances in a
    /// row that CHAOS pops up above it, and the developer stats.
    ///
    /// Where there is a broadcast overlay the stats are not drawn here: STATS puts the developer line on the
    /// overlay's card for whoever is on camera (<see cref="StatsOn"/>), and this HUD's own badge strip is
    /// for the hands-on development scenes, which have no overlay and leave it up from the start.
    ///
    /// Text refresh runs at 10 Hz, and stops entirely while the strip is closed — there is no point building
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
        [Tooltip("Broadcast scenes hide the stats until they are asked for; the dev scenes leave them up.")]
        public bool statsHiddenAtStart = true;
        [Tooltip("Hands-on scenes get slow motion and a camera toggle. A broadcast scene gets neither.")]
        public bool handsOn = false;
        public string menuSceneName = "MAIN";

        [Header("Timing")]
        [Tooltip("Seconds the CHAOS row stays up after it was opened or last used.")]
        public float chaosOpenSeconds = 6f;
        [Tooltip("Seconds RESTART waits for its second tap during a race.")]
        public float restartConfirmSeconds = 3f;

        Label _model, _speed, _distance, _stability, _attempt, _lastResult;
        VisualElement _statsCard;
        Button _restart, _stats, _slowMo, _camera, _menu;
        Label _restartLabel;

        VisualElement _chaosBar, _chaosPop;
        Button _chaos, _gust, _shove, _slick, _cam;
        Label _gustLabel, _shoveLabel, _slickLabel, _camLabel;

        bool _slow;
        bool _statsOn;
        bool _overlayCarriesStats;
        float _chaosCloseAt = -1f;
        float _restartArmedUntil = -1f;
        float _nextRefresh;

        /// <summary>
        /// Whether STATS is on. With a broadcast overlay in the scene that is a line on its card, not this
        /// HUD's strip; the results card reads it to put back what it put away.
        /// </summary>
        public bool StatsOn => _statsOn;

        /// <summary>Whether this HUD's own badge strip is on the screen.</summary>
        public bool StatsOpen => _statsOn && !_overlayCarriesStats;

        /// <summary>
        /// The top edge of everything this HUD has along the bottom of the screen (the control row, the
        /// CHAOS row when it is up, and the stats strip when it is open), in panel pixels; NaN before the
        /// first layout or while hidden.
        ///
        /// Every document here shares one PanelSettings, so they are one panel and their world
        /// coordinates are directly comparable. The broadcast card sits on this, so opening CHAOS lifts
        /// the card instead of drawing the buttons over it.
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
                if (ChaosOpen && _chaosPop.worldBound.height > 1f) top = Mathf.Min(top, _chaosPop.worldBound.yMin);
                return top;
            }
        }

        bool ChaosOpen => _chaosPop != null && !_chaosPop.ClassListContains("hidden");

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
            _restartLabel = Find<Label>("restart-label");
            _stats = Find<Button>("stats");
            _slowMo = Find<Button>("slowmo");
            _camera = Find<Button>("camera");
            _menu = Find<Button>("menu");

            if (_restart != null) _restart.clicked += OnRestart;
            if (_stats != null) _stats.clicked += ToggleStats;
            if (_slowMo != null) _slowMo.clicked += () => SetSlowMo(!_slow);
            if (_camera != null) _camera.clicked += OnCamera;
            if (_menu != null) _menu.clicked += OnMenu;

            // A broadcast scene has a card for the athlete on camera, and the developer line goes there.
            _overlayCarriesStats = !handsOn && FindFirstObjectByType<BroadcastView>(FindObjectsInactive.Include) != null;

            // Slow motion and the camera toggle are development controls; a broadcast scene has a director
            // and no reason to offer either.
            Show(_slowMo, handsOn);
            Show(_camera, handsOn && cameraRig != null);
            Show(_stats, !handsOn);
            _statsOn = !statsHiddenAtStart;
            Show(_statsCard, StatsOpen);

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
        /// physics and the cooldowns, and live in the row CHAOS pops up; the camera picker cycles the
        /// director's viewer camera. Both are optional, and the group is only on the picture when at least
        /// one of them is wired.
        /// </summary>
        void BuildChaosBar()
        {
            _chaosBar = Find<VisualElement>("chaos-bar");
            _chaosPop = Find<VisualElement>("chaos-pop");
            _chaos = Find<Button>("chaos");
            _gust = Find<Button>("gust");
            _shove = Find<Button>("shove");
            _slick = Find<Button>("slick");
            _cam = Find<Button>("cam");
            _gustLabel = Find<Label>("gust-label");
            _shoveLabel = Find<Label>("shove-label");
            _slickLabel = Find<Label>("slick-label");
            _camLabel = Find<Label>("cam-label");

            if (_chaos != null) _chaos.clicked += () => SetChaosOpen(!ChaosOpen);
            if (_gust != null) _gust.clicked += () => Fire(ViewerChaos.Act.Gust);
            if (_shove != null) _shove.clicked += () => Fire(ViewerChaos.Act.Shove);
            if (_slick != null) _slick.clicked += () => Fire(ViewerChaos.Act.Slick);
            if (_cam != null) _cam.clicked += OnViewerCam;

            Show(_chaosBar, !handsOn && (chaos != null || director != null));
            Show(_chaos, chaos != null);
            Show(_cam, director != null);
            SetChaosOpen(false);
            RefreshChaos();
        }

        /// <summary>Opens or closes the disturbance row. It closes itself a few seconds after its last use.</summary>
        public void SetChaosOpen(bool open)
        {
            Show(_chaosPop, open && chaos != null);
            _chaos?.EnableInClassList("btn--on", open && chaos != null);
            _chaosCloseAt = open ? Time.unscaledTime + chaosOpenSeconds : -1f;
        }

        void Fire(ViewerChaos.Act act)
        {
            chaos?.Fire(act);
            _chaosCloseAt = Time.unscaledTime + chaosOpenSeconds;
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
        /// Turns the developer stats on or off. In the broadcast scenes it is the second view of a race, not
        /// the first: the overlay carries the event, and this is the answer to "but how is it actually
        /// doing?".
        /// </summary>
        public void ToggleStats() => SetStatsVisible(!_statsOn);

        /// <summary>Used by the results card, which puts the HUD away and then puts back what it took.</summary>
        public void SetStatsVisible(bool visible)
        {
            _statsOn = visible;
            Show(_statsCard, StatsOpen);
            _stats?.EnableInClassList("btn--on", visible);
            if (StatsOpen) Refresh();
        }

        protected override void Update()
        {
            base.Update();
            if (_chaosCloseAt > 0f && Time.unscaledTime > _chaosCloseAt) SetChaosOpen(false);
            if (_restartArmedUntil > 0f && Time.unscaledTime > _restartArmedUntil) ArmRestart(false);

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

        /// <summary>
        /// During a race the first tap arms RESTART ("SURE?") and only a second tap inside a few seconds
        /// throws the race away. Between races there is nothing to lose, and one tap restarts.
        /// </summary>
        void OnRestart()
        {
            if (dash == null) return;
            bool live = dash.Current == RaceEvent.Phase.Running || dash.Current == RaceEvent.Phase.Countdown;
            if (live && _restartArmedUntil < 0f) { ArmRestart(true); return; }
            ArmRestart(false);
            dash.RestartNow();
        }

        void ArmRestart(bool armed)
        {
            _restartArmedUntil = armed ? Time.unscaledTime + restartConfirmSeconds : -1f;
            SetText(_restartLabel, armed ? "SURE?" : "RESTART");
            _restart?.EnableInClassList("btn--armed", armed);
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
