using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Unity.Cinemachine;
using PoDecath.Sim;
using PoDecath.UI;
using PoDecath.Cam;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// The bits that only the multi-runner race scene needs: the broadcast camera gallery, the results
    /// modal, and the setup menu that picks the field. Split out of <see cref="RooftopSceneBuilder"/>,
    /// which already carries the environment and track construction.
    /// </summary>
    public static class RaceUiBuilder
    {
        const string SetupScenePath = "Assets/Scenes/RaceSetup.unity";

        static readonly Color CardColor = new Color(0.05f, 0.06f, 0.09f, 0.9f);
        static readonly Color DimColor = new Color(0.02f, 0.03f, 0.05f, 0.92f);
        static readonly Color AccentColor = new Color(0.18f, 0.75f, 0.55f, 1f);
        static readonly Color ButtonColor = new Color(0.16f, 0.18f, 0.24f, 0.95f);
        static readonly Color MutedColor = new Color(0.66f, 0.71f, 0.78f, 1f);

        // ---------------------------------------------------------------- race scene

        /// <summary>
        /// Replaces the single-athlete chase rig with the broadcast gallery and adds the results modal.
        /// Called after the spawner is wired, so the director can be pointed straight at the event.
        /// </summary>
        public static void BuildBroadcastAndResults(DashEvent dash, TrackPath path, CameraRig camRig, RectTransform safe, GameplayHUD hud,
                                                    LongJumpPit pit = null)
        {
            // The chase/side pair follows one athlete; with a whole field the director owns the camera.
            if (camRig != null)
            {
                if (camRig.chaseCamera != null) Object.DestroyImmediate(camRig.chaseCamera.gameObject);
                if (camRig.sideCamera != null) Object.DestroyImmediate(camRig.sideCamera.gameObject);
                Object.DestroyImmediate(camRig.gameObject);
            }
            if (hud != null) hud.cameraRig = null;

            var dirGo = new GameObject("BroadcastDirector");
            var dir = dirGo.AddComponent<BroadcastDirector>();
            dir.race = dash;
            dir.path = path;
            dir.pit = pit;   // long jump: the director cuts the runway instead of the loop
            dir.startLineCam = ShotCam("CM Shot StartLine", 40f);
            dir.offTheGunCam = ShotCam("CM Shot OffTheGun", 34f);
            dir.railCam = ShotCam("CM Shot Rail", 38f);
            dir.bendCam = ShotCam("CM Shot Bend", 42f);
            dir.wideCam = ShotCam("CM Shot Wide", 52f);
            dir.headOnCam = ShotCam("CM Shot HeadOn", 32f);
            dir.finishCam = ShotCam("CM Shot Finish", 36f);

            BuildResultsModal(dash, safe);
        }

        /// <summary>
        /// A broadcast position: fixed lens, aimed by Cinemachine, placed each frame by the director.
        /// No CinemachineFollow, so nothing fights the director for the transform.
        /// </summary>
        static CinemachineCamera ShotCam(string name, float fov)
        {
            var go = new GameObject(name);
            var cm = go.AddComponent<CinemachineCamera>();
            cm.Lens.FieldOfView = fov;
            cm.Lens.NearClipPlane = 0.1f;
            cm.Lens.FarClipPlane = 1500f;
            cm.Priority = 10;
            var comp = go.AddComponent<CinemachineRotationComposer>();
            comp.Composition.ScreenPosition = new Vector2(0f, -0.02f);
            comp.Damping = new Vector2(0.35f, 0.35f);   // a little lag, like a real operator
            return cm;
        }

        static void BuildResultsModal(DashEvent dash, RectTransform safe)
        {
            // Whatever is already under the safe area is the live HUD; it gets hidden behind the results.
            var hud = new List<GameObject>();
            for (int i = 0; i < safe.childCount; i++) hud.Add(safe.GetChild(i).gameObject);

            // The component lives on an always-active root so its Start runs; only the dim layer toggles.
            RectTransform root = PoDecathSceneBuilder.CreatePanel("ResultsModal", safe, new Color(0, 0, 0, 0));
            var modal = root.gameObject.AddComponent<RaceResultsModal>();
            modal.race = dash;
            modal.setupSceneName = "RaceSetup";
            modal.hideWhileShown = hud;

            RectTransform dim = PoDecathSceneBuilder.CreatePanel("Dim", root, DimColor);
            modal.panel = dim.gameObject;

            RectTransform card = PoDecathSceneBuilder.CreatePanel("Card", dim, CardColor);
            PoDecathSceneBuilder.Stretch(card, new Vector2(38, 110), new Vector2(-38, -110));
            var vlg = card.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 5;
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlHeight = true; vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false; vlg.childForceExpandWidth = true;
            vlg.padding = new RectOffset(22, 22, 20, 20);

            Text title = PoDecathSceneBuilder.CreateText("Title", card, "RESULTS", 62, TextAnchor.MiddleCenter, FontStyle.Bold);
            PoDecathSceneBuilder.SetPreferredHeight(title.gameObject, 84);
            title.color = AccentColor;
            modal.titleText = title;

            Text subtitle = PoDecathSceneBuilder.CreateText("Subtitle", card, "", 28, TextAnchor.MiddleCenter);
            PoDecathSceneBuilder.SetPreferredHeight(subtitle.gameObject, 40);
            subtitle.color = MutedColor;
            modal.subtitleText = subtitle;

            PoDecathSceneBuilder.AddSpacer(card, 8);

            for (int i = 0; i < RaceRoster.MaxRunners; i++)
            {
                RectTransform row = PoDecathSceneBuilder.CreatePanel($"Row{i + 1}", card, new Color(1f, 1f, 1f, 0.05f));
                PoDecathSceneBuilder.SetPreferredHeight(row.gameObject, 62);
                var hlg = row.gameObject.AddComponent<HorizontalLayoutGroup>();
                hlg.spacing = 10;
                hlg.childAlignment = TextAnchor.MiddleLeft;
                hlg.childControlHeight = true; hlg.childControlWidth = true;
                hlg.childForceExpandHeight = true; hlg.childForceExpandWidth = false;
                hlg.padding = new RectOffset(14, 14, 0, 0);

                Text rank = PoDecathSceneBuilder.CreateText("Rank", row, "-", 30, TextAnchor.MiddleCenter, FontStyle.Bold);
                SetWidth(rank.gameObject, 58);
                Text name = PoDecathSceneBuilder.CreateText("Name", row, "", 28, TextAnchor.MiddleLeft, FontStyle.Bold);
                SetFlexibleWidth(name.gameObject);
                Text time = PoDecathSceneBuilder.CreateText("Time", row, "", 26, TextAnchor.MiddleRight);
                SetWidth(time.gameObject, 290);

                modal.rankTexts.Add(rank);
                modal.nameTexts.Add(name);
                modal.timeTexts.Add(time);
            }

            PoDecathSceneBuilder.AddSpacer(card, 12);

            RectTransform buttons = PoDecathSceneBuilder.CreatePanel("Buttons", card, new Color(0, 0, 0, 0));
            PoDecathSceneBuilder.SetPreferredHeight(buttons.gameObject, 124);
            var bhlg = buttons.gameObject.AddComponent<HorizontalLayoutGroup>();
            bhlg.spacing = 20;
            bhlg.childAlignment = TextAnchor.MiddleCenter;
            bhlg.childControlHeight = true; bhlg.childControlWidth = true;
            bhlg.childForceExpandHeight = true; bhlg.childForceExpandWidth = true;

            modal.againButton = PoDecathSceneBuilder.CreateButton("RaceAgain", buttons, "RACE AGAIN", 36, AccentColor);
            modal.changeButton = PoDecathSceneBuilder.CreateButton("ChangeRunners", buttons, "CHANGE RUNNERS", 32, ButtonColor);

            // Saved hidden. RaceResultsModal.Start hides it too, but a full field takes tens of seconds to
            // build its rigs before any Start runs, and an empty results card should not be what fills the
            // screen for that whole time.
            dim.gameObject.SetActive(false);
        }

        // ---------------------------------------------------------------- setup scene

        /// <summary>One event the setup menu can offer.</summary>
        public struct EventEntry
        {
            public string label, sceneName, hint;
            public EventEntry(string label, string sceneName, string hint) { this.label = label; this.sceneName = sceneName; this.hint = hint; }
        }

        // The lap is 100.1 m, so it is the 100 m; the 20 m dash on the straight is not offered here.
        static readonly EventEntry LapRaceEntry = new EventEntry("LAP RACE", "RooftopRace",
            $"Pick 1 to {RaceRoster.MaxRunners} runners. One 100 m lap of the rooftop track.");
        static readonly EventEntry LongJumpEntry = new EventEntry("LONG JUMP", "RooftopLongJump",
            $"Pick 1 to {RaceRoster.MaxRunners} jumpers. Three rounds each on the infield runway; best mark wins.");

        /// <summary>
        /// Builds the menu that picks the event and the field. Every roster entry is offered, the RED
        /// heuristic bot included (house rule: every event has one), each with its own counter. Only events
        /// whose scene exists are offered, so building the race scenes alone still gives a menu without the
        /// long jump on it.
        /// </summary>
        public static void BuildSetupScene(List<AthleteDefinition> roster)
        {
            var choices = new List<AthleteDefinition>();
            foreach (AthleteDefinition d in roster)
                if (d != null) choices.Add(d);
            if (choices.Count == 0) { Debug.LogError("[PoDecath] No athlete definitions to race."); return; }
            var events = new List<EventEntry>();
            foreach (EventEntry e in new[] { LapRaceEntry, LongJumpEntry })
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>($"Assets/Scenes/{e.sceneName}.unity") != null) events.Add(e);

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.07f, 0.08f, 0.11f);
            camGo.transform.position = new Vector3(0, 1, -10);
            camGo.AddComponent<AudioListener>();

            PoDecathSceneBuilder.CreateEventSystem();
            Canvas canvas = PoDecathSceneBuilder.CreateCanvas("SetupCanvas");
            RectTransform safe = PoDecathSceneBuilder.CreateSafeArea(canvas.transform);

            RectTransform panel = PoDecathSceneBuilder.CreatePanel("SetupPanel", safe, new Color(0, 0, 0, 0));
            PoDecathSceneBuilder.Stretch(panel, new Vector2(50, 120), new Vector2(-50, -140));
            var vlg = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 22;
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlHeight = true; vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false; vlg.childForceExpandWidth = true;
            vlg.padding = new RectOffset(22, 22, 22, 22);

            Text title = PoDecathSceneBuilder.CreateText("Title", panel, "EVENT SETUP", 76, TextAnchor.MiddleCenter, FontStyle.Bold);
            PoDecathSceneBuilder.SetPreferredHeight(title.gameObject, 120);
            title.color = AccentColor;

            Text hint = PoDecathSceneBuilder.CreateText("Hint", panel, "", 28, TextAnchor.MiddleCenter);
            PoDecathSceneBuilder.SetPreferredHeight(hint.gameObject, 90);
            hint.color = MutedColor;

            PoDecathSceneBuilder.AddSpacer(panel, 20);

            var ctrlGo = new GameObject("RaceSetup");
            var ctrl = ctrlGo.AddComponent<RaceSetupController>();
            ctrl.hintText = hint;
            ctrl.selectedEventColor = AccentColor;
            ctrl.unselectedEventColor = ButtonColor;

            // Every event on offer goes to the controller — that is where START reads the scene name and
            // the hint from, whether or not there is anything to pick between.
            foreach (EventEntry e in events)
                ctrl.events.Add(new RaceSetupController.EventChoice { label = e.label, sceneName = e.sceneName, hint = e.hint });

            // The picker is only that list's UI: one button per event, side by side above the field.
            if (events.Count > 1)
            {
                RectTransform picker = PoDecathSceneBuilder.CreatePanel("EventPicker", panel, new Color(0, 0, 0, 0));
                PoDecathSceneBuilder.SetPreferredHeight(picker.gameObject, 150);
                var phlg = picker.gameObject.AddComponent<HorizontalLayoutGroup>();
                phlg.spacing = 16;
                phlg.childAlignment = TextAnchor.MiddleCenter;
                phlg.childControlHeight = true; phlg.childControlWidth = true;
                phlg.childForceExpandHeight = true; phlg.childForceExpandWidth = true;
                for (int i = 0; i < events.Count; i++)
                    ctrl.events[i].button = PoDecathSceneBuilder.CreateButton($"Event_{events[i].sceneName}", picker, events[i].label, 34, ButtonColor);
                PoDecathSceneBuilder.AddSpacer(panel, 6);
            }

            foreach (AthleteDefinition def in choices)
            {
                RectTransform row = PoDecathSceneBuilder.CreatePanel($"Row_{def.name}", panel, CardColor);
                PoDecathSceneBuilder.SetPreferredHeight(row.gameObject, 150);
                var hlg = row.gameObject.AddComponent<HorizontalLayoutGroup>();
                hlg.spacing = 16;
                hlg.childAlignment = TextAnchor.MiddleCenter;
                hlg.childControlHeight = true; hlg.childControlWidth = true;
                hlg.childForceExpandHeight = false; hlg.childForceExpandWidth = false;
                hlg.padding = new RectOffset(24, 24, 18, 18);

                Text name = PoDecathSceneBuilder.CreateText("Name", row, def.displayName, 36, TextAnchor.MiddleLeft, FontStyle.Bold);
                SetFlexibleWidth(name.gameObject);
                name.color = def.Tint;   // GREEN reference bot, custom tint for the variations

                Button minus = PoDecathSceneBuilder.CreateButton("Minus", row, "-", 48, ButtonColor);
                SetWidth(minus.gameObject, 120); SetHeight(minus.gameObject, 110);
                minus.gameObject.AddComponent<RepeatButton>();
                Text count = PoDecathSceneBuilder.CreateText("Count", row, "0", 52, TextAnchor.MiddleCenter, FontStyle.Bold);
                SetWidth(count.gameObject, 110);
                Button plus = PoDecathSceneBuilder.CreateButton("Plus", row, "+", 48, ButtonColor);
                SetWidth(plus.gameObject, 120); SetHeight(plus.gameObject, 110);
                plus.gameObject.AddComponent<RepeatButton>();

                ctrl.rows.Add(new RaceSetupController.RunnerRow
                {
                    definition = def, minusButton = minus, plusButton = plus, countText = count, nameText = name,
                });
            }

            PoDecathSceneBuilder.AddSpacer(panel, 16);
            Text total = PoDecathSceneBuilder.CreateText("Total", panel, "", 40, TextAnchor.MiddleCenter, FontStyle.Bold);
            PoDecathSceneBuilder.SetPreferredHeight(total.gameObject, 70);
            ctrl.totalText = total;

            PoDecathSceneBuilder.AddSpacer(panel, 20);
            Button start = PoDecathSceneBuilder.CreateButton("StartButton", panel, "START", 42, AccentColor);
            PoDecathSceneBuilder.SetPreferredHeight(start.gameObject, 170);
            ctrl.startButton = start;

            EditorSceneManager.SaveScene(scene, SetupScenePath);
            MakeFirstSceneInBuild(SetupScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PoDecath] Race setup scene built: {SetupScenePath}");
        }

        // ---------------------------------------------------------------- layout helpers

        static void SetWidth(GameObject go, float width)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.minWidth = width;
            le.flexibleWidth = 0f;
        }

        static void SetHeight(GameObject go, float height)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;
        }

        static void SetFlexibleWidth(GameObject go)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;
        }

        /// <summary>
        /// Puts the scene at build index 0, which is what a built player loads on launch. Race setup is
        /// the entry point of the game, so it has to displace whatever was first (the old MainMenu).
        /// </summary>
        static void MakeFirstSceneInBuild(string path)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            scenes.RemoveAll(s => s.path == path);
            scenes.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
