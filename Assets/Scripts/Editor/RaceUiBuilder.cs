using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Cinemachine;
using PoDecath.Diag;
using PoDecath.Sim;
using PoDecath.UI;
using PoDecath.Cam;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// The bits that only the multi-runner race scene needs: the broadcast camera gallery, the screens
    /// that go over it, and the setup menu that picks the field. Split out of
    /// <see cref="RooftopSceneBuilder"/>, which already carries the environment and track construction.
    ///
    /// The screens are UI Toolkit documents now. What used to be four hundred lines assembling
    /// <c>RectTransform</c>s, <c>Image</c>s and <c>Text</c>s one at a time — with every colour, size and
    /// margin as a literal in this file — is a UXML layout and one shared stylesheet in <c>Assets/UI/</c>.
    /// This file's job is down to what it should always have been: put the right components in the scene
    /// and wire them to each other.
    /// </summary>
    public static class RaceUiBuilder
    {
        const string SetupScenePath = "Assets/Scenes/MAIN.unity";

        // Sort order: the overlay is the picture, the HUD sits over it, the results card over both, and
        // the diagnostics panel over everything, because its whole job is to be readable while you are
        // looking at something else.
        const float OverlayOrder = 0f;
        const float HudOrder = 10f;
        const float ResultsOrder = 20f;
        const float TelemetryOrder = 30f;

        // ---------------------------------------------------------------- race scene

        /// <summary>
        /// Replaces the single-athlete chase rig with the broadcast gallery, then adds the overlay, the
        /// results card and the diagnostics panel over it.
        /// </summary>
        public static BroadcastDirector BuildBroadcastAndResults(RaceEvent dash, TrackPath path, CameraRig camRig,
                                                                 HudView hud, LongJumpPit pit = null)
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

            var overlay = UiBakery.AddScreen<BroadcastView>("BroadcastOverlay", UiBakery.BroadcastUxml, OverlayOrder);
            if (overlay != null)
            {
                overlay.race = dash;
                overlay.director = dir;
            }

            var results = UiBakery.AddScreen<ResultsView>("ResultsCard", UiBakery.ResultsUxml, ResultsOrder);
            if (results != null)
            {
                results.race = dash;
                results.hud = hud;
                results.overlay = overlay;
                // Wired here rather than left to the field's default. This is the results card's way
                // back to the menu, and a default only applies to a component the moment it is created:
                // rename the scene and every already-built scene keeps pointing at a path that no longer
                // exists, silently, until someone presses the button. Naming the entry scene in one
                // place -- SetupScenePath -- keeps them in step.
                results.setupSceneName = System.IO.Path.GetFileNameWithoutExtension(SetupScenePath);
            }

            AddTelemetry(dash);
            return dir;
        }

        /// <summary>
        /// The diagnostics panel. It is in every scene the game ships, closed, on F3 — a performance
        /// overlay that has to be added by hand before it can be used is one that never gets used.
        /// </summary>
        public static TelemetryOverlay AddTelemetry(RaceEvent dash)
        {
            var telemetry = UiBakery.AddScreen<TelemetryOverlay>("Telemetry", UiBakery.TelemetryUxml, TelemetryOrder);
            if (telemetry == null) return null;
            telemetry.race = dash;
            telemetry.audioMix = Object.FindFirstObjectByType<PoDecath.Audio.RaceAudio>();
            return telemetry;
        }

        /// <summary>The HUD document: the developer stats card and the control bar.</summary>
        public static HudView BuildHud(RaceEvent dash, CameraRig camRig, bool handsOn)
        {
            var hud = UiBakery.AddScreen<HudView>("HUD", UiBakery.HudUxml, HudOrder);
            if (hud == null) return null;
            hud.dash = dash;
            hud.cameraRig = camRig;
            hud.handsOn = handsOn;
            hud.statsHiddenAtStart = !handsOn;   // a broadcast scene opens on the overlay, not on the card
            hud.menuSceneName = "MainMenu";
            return hud;
        }

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

        // ---------------------------------------------------------------- setup scene

        struct EventEntry
        {
            public string label, sceneName, hint;
            public int laps;
            public bool hurdles;

            public EventEntry(string label, string sceneName, int laps, bool hurdles, string hint)
            {
                this.label = label; this.sceneName = sceneName; this.hint = hint;
                this.laps = laps; this.hurdles = hurdles;
            }
        }

        /// <summary>
        /// The events on offer. Everything on the loop is the same scene and the same
        /// <see cref="LapEvent"/> — the lap count is what makes one a 100 m and another a 1500 m, and it
        /// travels through <see cref="SessionSettings"/> as the scene loads. The rooftop lap is 100.1 m,
        /// so it is the 100 m; the 20 m dash on the straight stays a development scene and is not offered.
        /// </summary>
        static readonly EventEntry[] Catalogue =
        {
            new EventEntry("100 M", "RooftopRace", 1, false,
                $"Pick 1 to {RaceRoster.MaxRunners} runners. One 100 m lap of the rooftop track."),
            new EventEntry("400 M", "RooftopRace", 4, false,
                "Four laps of the roof, 400 m. Bell on the last one."),
            new EventEntry("1500 M", "RooftopRace", 15, false,
                "Fifteen laps, 1500 m. Around six minutes, and nobody gets up from a fall yet."),
            new EventEntry("HURDLES", "RooftopRace", 1, true,
                "One lap over hurdles that fall over when they are hit. No policy has been trained to "
              + "jump one, so this is a running race through furniture."),
            new EventEntry("LONG JUMP", "RooftopLongJump", 0, false,
                $"Pick 1 to {RaceRoster.MaxRunners} jumpers. Three rounds each on the infield runway; best mark wins."),
        };

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
            foreach (EventEntry e in Catalogue)
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>($"Assets/Scenes/{e.sceneName}.unity") != null) events.Add(e);

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.06f, 0.09f);
            camGo.transform.position = new Vector3(0, 1, -10);
            camGo.AddComponent<AudioListener>();

            var setup = UiBakery.AddScreen<SetupView>("SetupMenu", UiBakery.SetupUxml, HudOrder);
            if (setup != null)
            {
                foreach (EventEntry e in events)
                    setup.events.Add(new SetupView.EventChoice
                    {
                        label = e.label,
                        sceneName = e.sceneName,
                        hint = e.hint,
                        laps = e.laps,
                        hurdles = e.hurdles,
                    });
                foreach (AthleteDefinition d in choices)
                    setup.rows.Add(new SetupView.RunnerRow { definition = d });
            }

            // The diagnostics panel belongs here too: the menu is where a frame rate problem caused by the
            // UI itself would otherwise be invisible.
            AddTelemetry(null);

            EditorSceneManager.SaveScene(scene, SetupScenePath);
            MakeFirstSceneInBuild(SetupScenePath);
            Debug.Log($"[PoDecath] Setup menu built: {events.Count} event(s), {choices.Count} athlete type(s).");
        }

        /// <summary>The setup menu is what the game opens on, so it has to be build index 0.</summary>
        static void MakeFirstSceneInBuild(string path)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            scenes.RemoveAll(s => s.path == path);
            scenes.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
