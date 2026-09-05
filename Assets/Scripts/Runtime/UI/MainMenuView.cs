using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using PoDecath.Env;
using PoDecath.Sim;

namespace PoDecath.UI
{
    /// <summary>
    /// The development main menu: checkpoint selector, arena picker, quality tier, frame-rate target, start.
    ///
    /// The tier picker is new and is the one control here that is not a development convenience. The
    /// project ships two render pipeline assets with genuinely different budgets now — the mobile one at
    /// 0.8 render scale with a single shadow cascade, the desktop one with 4x MSAA, four cascades and
    /// screen-space ambient occlusion — and being able to switch between them while looking at the same
    /// scene is the only honest way to judge what the phone build is going to lose.
    /// </summary>
    public class MainMenuView : UiRoot
    {
        public string arenaSceneName = "Arena";

        DropdownField _policy, _arena, _tier;
        Toggle _fps60;
        Button _start;
        Label _info;
        PolicyLibrary _library;

        protected override void Build()
        {
            SessionSettings.Load();
            SessionSettings.VisitedMenu = true;
            Application.targetFrameRate = 60;

            _policy = Find<DropdownField>("policy");
            _arena = Find<DropdownField>("arena");
            _tier = Find<DropdownField>("tier");
            _fps60 = Find<Toggle>("fps60");
            _start = Find<Button>("start");
            _info = Find<Label>("info");

            _library = PolicyLibrary.Load();
            var names = new List<string>();
            if (_library != null && _library.entries.Count > 0)
            {
                foreach (PolicyEntry e in _library.entries)
                    names.Add(string.IsNullOrEmpty(e.displayName) ? (e.model != null ? e.model.name : "unnamed") : e.displayName);
            }
            else
            {
                names.Add("No checkpoint (hold default pose)");
            }

            if (_policy != null)
            {
                _policy.choices = names;
                _policy.index = Mathf.Clamp(SessionSettings.PolicyIndex, 0, names.Count - 1);
            }
            if (_arena != null)
            {
                _arena.choices = new List<string> { "Flat Track", "Stepped Platforms", "Obstacle Lane" };
                _arena.index = Mathf.Clamp((int)SessionSettings.Arena, 0, 2);
            }
            if (_tier != null)
            {
                _tier.choices = new List<string> { "Mobile (0.8 scale, 1 cascade)", "PC (MSAA 4x, SSAO, depth of field)" };
                _tier.index = (int)RenderTier.Current;
                // Applied on change rather than on start, so the effect is visible on this screen.
                _tier.RegisterValueChangedCallback(_ => RenderTier.Set((Tier)Mathf.Clamp(_tier.index, 0, 1)));
            }
            if (_fps60 != null) _fps60.value = SessionSettings.TargetFrameRate >= 60;
            if (_start != null) _start.clicked += StartSimulation;

            if (_info == null) return;
            int count = _library != null ? _library.entries.Count : 0;
            _info.text = count > 0
                ? $"{count} checkpoint(s) in Assets/Policies. Inference: Unity Inference Engine, CPU backend."
                : "Drop Isaac Lab / MuJoCo .onnx files into Assets/Policies. The rig will hold its default pose until then.";
        }

        public void StartSimulation()
        {
            SessionSettings.PolicyIndex = _policy != null ? Mathf.Max(0, _policy.index) : 0;
            SessionSettings.Arena = _arena != null ? (ArenaType)Mathf.Max(0, _arena.index) : ArenaType.FlatTrack;
            SessionSettings.TargetFrameRate = _fps60 != null && _fps60.value ? 60 : 30;
            SessionSettings.Save();
            SessionSettings.ApplyQuality();
            RenderTier.Apply();
            Time.timeScale = 1f;
            SceneManager.LoadScene(arenaSceneName);
        }
    }
}
