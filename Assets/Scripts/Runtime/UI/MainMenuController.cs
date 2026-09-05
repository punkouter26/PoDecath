using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using PoDecath.Sim;

namespace PoDecath.UI
{
    /// <summary>Portrait main menu: checkpoint selector, arena picker, quality toggle, start.</summary>
    public class MainMenuController : MonoBehaviour
    {
        public Dropdown policyDropdown;
        public Dropdown arenaDropdown;
        public Toggle fps60Toggle;
        public Button startButton;
        public Text infoText;
        public string arenaSceneName = "Arena";

        PolicyLibrary _library;

        void Start()
        {
            SessionSettings.Load();
            SessionSettings.VisitedMenu = true;
            Application.targetFrameRate = 60;

            _library = PolicyLibrary.Load();
            var names = new List<string>();
            if (_library != null && _library.entries.Count > 0)
            {
                foreach (var e in _library.entries)
                    names.Add(string.IsNullOrEmpty(e.displayName) ? (e.model != null ? e.model.name : "unnamed") : e.displayName);
            }
            else
            {
                names.Add("No checkpoint (hold default pose)");
            }

            if (policyDropdown != null)
            {
                policyDropdown.ClearOptions();
                policyDropdown.AddOptions(names);
                policyDropdown.value = Mathf.Clamp(SessionSettings.PolicyIndex, 0, names.Count - 1);
                policyDropdown.RefreshShownValue();
            }

            if (arenaDropdown != null)
            {
                arenaDropdown.ClearOptions();
                arenaDropdown.AddOptions(new List<string> { "Flat Track", "Stepped Platforms", "Obstacle Lane" });
                arenaDropdown.value = (int)SessionSettings.Arena;
                arenaDropdown.RefreshShownValue();
            }

            if (fps60Toggle != null) fps60Toggle.isOn = SessionSettings.TargetFrameRate >= 60;
            if (startButton != null) startButton.onClick.AddListener(StartSimulation);

            if (infoText != null)
            {
                int count = _library != null ? _library.entries.Count : 0;
                infoText.text = count > 0
                    ? $"{count} checkpoint(s) in Assets/Policies\nInference: Unity Inference Engine, CPU backend"
                    : "Drop Isaac Lab / MuJoCo .onnx files into Assets/Policies\nThe rig will hold its default pose until then.";
            }
        }

        public void StartSimulation()
        {
            SessionSettings.PolicyIndex = policyDropdown != null ? policyDropdown.value : 0;
            SessionSettings.Arena = arenaDropdown != null ? (ArenaType)arenaDropdown.value : ArenaType.FlatTrack;
            SessionSettings.TargetFrameRate = fps60Toggle != null && fps60Toggle.isOn ? 60 : 30;
            SessionSettings.Save();
            SessionSettings.ApplyQuality();
            Time.timeScale = 1f;
            SceneManager.LoadScene(arenaSceneName);
        }
    }
}
