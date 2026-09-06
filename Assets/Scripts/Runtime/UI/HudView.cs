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

        [Header("Layout")]
        [Tooltip("Broadcast scenes hide the card until it is asked for; the dev scenes leave it up.")]
        public bool statsHiddenAtStart = true;
        [Tooltip("Hands-on scenes get slow motion and a camera toggle. A broadcast scene gets neither.")]
        public bool handsOn = false;
        public string menuSceneName = "MainMenu";

        Label _model, _speed, _distance, _stability, _attempt, _lastResult;
        VisualElement _statsCard;
        Button _restart, _stats, _slowMo, _camera, _menu;

        bool _slow;
        float _nextRefresh;

        /// <summary>Whether the developer card is open. The results modal reads this before hiding it.</summary>
        public bool StatsOpen => _statsCard != null && !_statsCard.ClassListContains("hidden");

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

            if (dash != null) dash.RaceFinished += OnRaceFinished;
            if (perturbation != null) perturbation.enabled = SessionSettings.PerturbationEnabled;

            SetSlowMo(false);
            Refresh();
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
            if (!StatsOpen) return;   // nothing to rebuild while it is put away
            Refresh();
        }

        void Refresh()
        {
            if (dash != null)
            {
                RaceEvent.Athlete r = dash.Reference;
                string model = r != null && r.runner != null ? r.runner.ModelName : (r != null ? r.name : "-");
                SetText(_model, $"{(r != null ? r.name : "event")}  |  {model}");

                bool fell = r != null && r.fell;
                SetText(_speed, $"{dash.Speed:F2} m/s   {(fell ? "FELL" : dash.Status)}");
                SetText(_distance, dash.HudDistanceLine());
                SetText(_stability,
                        fell ? "0 %  (fell)" : $"{dash.Stability * 100f:F0} %",
                        fell ? new Color(1f, 0.3f, 0.25f)
                             : dash.Stability < 0.6f ? new Color(1f, 0.8f, 0.3f) : Color.white);
                SetText(_attempt, (dash.Attempt + 1).ToString());
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
