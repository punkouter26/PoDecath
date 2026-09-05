using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using PoDecath.Sim;
using PoDecath.Cam;

namespace PoDecath.UI
{
    /// <summary>
    /// Portrait HUD. Top status card: model, speed, distance, stability, episode. Bottom bar:
    /// restart, slow-mo 0.5x, camera toggle, menu, and the touch-flick perturbation toggle.
    /// Text refresh runs at 10 Hz to keep string allocations negligible.
    /// </summary>
    public class GameplayHUD : MonoBehaviour
    {
        [Header("Wiring")]
        public EpisodeManager episodes;
        [Tooltip("When set, the HUD shows the 100 m dash instead of the arena episode loop.")]
        public DashEvent dash;
        public PolicyRunner runner;
        public CameraRig cameraRig;
        public TouchPerturbation perturbation;

        [Header("Status card")]
        public Text modelText;
        public Text speedText;
        public Text distanceText;
        public Text stabilityText;
        public Text episodeText;
        public Text lastResultText;

        [Header("Control bar")]
        public Button restartButton;
        public Button slowMoButton;
        public Button cameraButton;
        public Button menuButton;
        public Toggle perturbToggle;
        public Text slowMoLabel;
        public Text cameraLabel;
        public string menuSceneName = "MainMenu";

        bool _slow;
        float _nextRefresh;

        void Start()
        {
            if (restartButton != null) restartButton.onClick.AddListener(OnRestart);
            if (slowMoButton != null) slowMoButton.onClick.AddListener(OnSlowMo);
            if (cameraButton != null) cameraButton.onClick.AddListener(OnCamera);
            if (menuButton != null) menuButton.onClick.AddListener(OnMenu);
            if (perturbToggle != null)
            {
                perturbToggle.isOn = SessionSettings.PerturbationEnabled;
                perturbToggle.onValueChanged.AddListener(OnPerturb);
            }
            if (perturbation != null) perturbation.enabled = SessionSettings.PerturbationEnabled;
            if (episodes != null) episodes.EpisodeEnded += OnEpisodeEnded;
            if (dash != null) dash.RaceFinished += OnRaceFinished;
            SetSlowMo(false);
            RefreshLabels();
        }

        void OnDestroy()
        {
            if (episodes != null) episodes.EpisodeEnded -= OnEpisodeEnded;
            if (dash != null) dash.RaceFinished -= OnRaceFinished;
        }

        void OnRaceFinished(string summary)
        {
            if (lastResultText == null) return;
            // A full field writes a result line per runner, which overruns the status card. When the event
            // ranked a field, show the winner and the finisher count and leave the board to the modal.
            if (dash != null && dash.Results.Count > 1)
            {
                lastResultText.text = dash.HudResultLine(dash.Results);
                return;
            }
            lastResultText.text = "Last: " + summary;
        }

        void Update()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.1f;
            RefreshLabels();
        }

        void RefreshLabels()
        {
            if (dash != null)
            {
                var r = dash.Reference;
                if (modelText != null)
                {
                    string model = r != null && r.runner != null ? r.runner.ModelName : (r != null ? r.name : "-");
                    modelText.text = $"{(r != null ? r.name : "100 m dash")}  |  {model}";
                }
                bool fell = r != null && r.fell;
                if (speedText != null) speedText.text = $"Speed  {dash.Speed:F2} m/s   {(fell ? "FELL" : dash.Status)}";
                if (distanceText != null) distanceText.text = dash.HudDistanceLine();
                if (stabilityText != null)
                {
                    stabilityText.text = fell ? "Stability  0 %  (fell)" : $"Stability  {dash.Stability * 100f:F0} %";
                    stabilityText.color = fell ? new Color(1f, 0.3f, 0.25f) : (dash.Stability < 0.6f ? new Color(1f, 0.8f, 0.3f) : Color.white);
                }
                if (episodeText != null) episodeText.text = $"Attempt  {dash.Attempt + 1}";
                return;
            }
            if (runner != null && modelText != null)
                modelText.text = $"{runner.ModelName}  |  {(runner.config != null ? runner.config.ControlHz : 0f):F0} Hz";
            if (episodes == null) return;
            if (speedText != null) speedText.text = $"Speed  {episodes.Speed:F2} m/s";
            if (distanceText != null) distanceText.text = $"Distance  {episodes.Distance:F1} m   Time  {episodes.EpisodeTime:F1} s";
            if (stabilityText != null) stabilityText.text = $"Stability  {episodes.Stability * 100f:F0} %";
            if (episodeText != null) episodeText.text = $"Episode  {episodes.EpisodeIndex + 1}";
        }

        void OnEpisodeEnded(EpisodeResult r)
        {
            if (lastResultText != null)
                lastResultText.text = $"Last: {r.reason}  {r.distance:F1} m in {r.duration:F1} s";
        }

        void OnRestart()
        {
            if (dash != null) dash.RestartNow();
            else if (episodes != null) episodes.RestartNow();
        }

        void OnSlowMo() => SetSlowMo(!_slow);

        void SetSlowMo(bool slow)
        {
            _slow = slow;
            Time.timeScale = slow ? 0.5f : 1f;
            if (slowMoLabel != null) slowMoLabel.text = slow ? "1.0x" : "0.5x";
        }

        void OnCamera()
        {
            if (cameraRig != null) cameraRig.Toggle();
            if (cameraLabel != null && cameraRig != null) cameraLabel.text = "Cam: " + cameraRig.ActiveName;
        }

        void OnMenu()
        {
            Time.timeScale = 1f;
            SceneManager.LoadScene(menuSceneName);
        }

        void OnPerturb(bool on)
        {
            SessionSettings.PerturbationEnabled = on;
            if (perturbation != null) perturbation.enabled = on;
        }
    }
}
