using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Unity.Cinemachine;
using Unity.Cinemachine.TargetTracking;
using Unity.InferenceEngine;
using PoDecath.Sim;
using PoDecath.UI;
using PoDecath.Cam;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// Builds Assets/Scenes/Rooftop.unity: the owner's White House (glTF), a ProBuilder kart deck above it,
    /// the 100 m dash event with the roster (RED heuristic, GREEN reference RL), cameras and the portrait HUD.
    /// </summary>
    public static class RooftopSceneBuilder
    {
        const string ScenePath = "Assets/Scenes/Rooftop.unity";
        const string WhiteHousePath = "Assets/Models/WhiteHouse.glb";
        const string SkinPath = "Assets/Models/Athlete_Matt.glb";
        const string MjcfPath = "Assets/Models/athlete.xml";
        const string PolicyJsonPath = "Assets/Models/athlete_policy_config.json";
        const string OnnxPath = "Assets/Policies/athlete_run.onnx";
        const string AthletesDir = "Assets/Athletes";
        const string CreatureLayer = "Creature";

        const string LapScenePath = "Assets/Scenes/RooftopLap.unity";
        const string RaceScenePath = "Assets/Scenes/RooftopRace.unity";
        const string LongJumpScenePath = "Assets/Scenes/RooftopLongJump.unity";
        const string TrackOnnxPath = "Assets/Policies/athlete_track.onnx";

        /// <summary>
        /// Dash = the straight, Lap = one runner round the loop, Race = the picked field round the loop,
        /// LongJump = the picked field on the runway inside the loop. The lap is 100.1 m, so the menu's
        /// "100 m" IS the lap race; the dash on the straight is the development scene only.
        /// </summary>
        public enum Mode { Dash, Lap, Race, LongJump }

        [MenuItem("PoDecath/Build Rooftop Scene", priority = 2)]
        public static void Build() => BuildScene(Mode.Dash);

        [MenuItem("PoDecath/Build Rooftop Lap Scene", priority = 3)]
        public static void BuildLap() => BuildScene(Mode.Lap);

        [MenuItem("PoDecath/Build Race Scenes", priority = 4)]
        public static void BuildRace()
        {
            BuildScene(Mode.Race);
            RaceUiBuilder.BuildSetupScene(EnsureRoster(AssetDatabase.LoadAssetAtPath<ModelAsset>(TrackOnnxPath)));
        }

        /// <summary>The long jump scene, then the setup menu again so it offers both events.</summary>
        [MenuItem("PoDecath/Build Long Jump Scene", priority = 5)]
        public static void BuildLongJump()
        {
            BuildScene(Mode.LongJump);
            RaceUiBuilder.BuildSetupScene(EnsureRoster(AssetDatabase.LoadAssetAtPath<ModelAsset>(TrackOnnxPath)));
        }

        static void BuildScene(Mode mode)
        {
            bool lapMode = mode == Mode.Lap || mode == Mode.Race;   // both loop modes use the TrackPath and LapEvent
            bool raceMode = mode == Mode.Race;
            bool jumpMode = mode == Mode.LongJump;
            bool fieldMode = raceMode || jumpMode;                  // a picked field, broadcast gallery, results modal
            PoDecathSceneBuilder.EnsureLayer(CreatureLayer, 8);
            int creatureLayer = LayerMask.NameToLayer(CreatureLayer);
            if (creatureLayer > 0) Physics.IgnoreLayerCollision(creatureLayer, creatureLayer, true);   // no self-collision, like the MJCF
            PolicyLibraryTools.EnsureFolder("Assets/Materials");
            Material asphalt = PoDecathSceneBuilder.Mat("Track_Asphalt", new Color(0.16f, 0.16f, 0.18f));
            Material barrier = PoDecathSceneBuilder.Mat("Track_Barrier", new Color(0.85f, 0.12f, 0.12f));
            Material lineMat = PoDecathSceneBuilder.Mat("Track_Line", new Color(0.95f, 0.95f, 0.95f));
            Material column = PoDecathSceneBuilder.Mat("Track_Column", new Color(0.72f, 0.72f, 0.7f));
            Material infield = PoDecathSceneBuilder.Mat("Infield_Deck", new Color(0.24f, 0.31f, 0.24f));
            Material runway = PoDecathSceneBuilder.Mat("LongJump_Runway", new Color(0.62f, 0.25f, 0.17f));
            Material sand = PoDecathSceneBuilder.Mat("LongJump_Sand", new Color(0.87f, 0.79f, 0.6f));
            Material foulMat = PoDecathSceneBuilder.Mat("LongJump_Foul", new Color(0.1f, 0.1f, 0.11f));
            Material debugMat = PoDecathSceneBuilder.Mat("Athlete_Debug", new Color(0.8f, 0.8f, 0.82f));
            PhysicsMaterial footPm = PoDecathSceneBuilder.EnsureFootPhysicsMaterial();
            PolicyLibraryTools.Refresh();

            var whAsset = AssetDatabase.LoadAssetAtPath<GameObject>(WhiteHousePath);
            if (whAsset == null) { Debug.LogError($"[PoDecath] {WhiteHousePath} not found or not imported by glTFast."); return; }
            var skinAsset = AssetDatabase.LoadAssetAtPath<GameObject>(SkinPath);
            var mjcf = AssetDatabase.LoadAssetAtPath<TextAsset>(MjcfPath);
            var policyJson = AssetDatabase.LoadAssetAtPath<TextAsset>(PolicyJsonPath);
            var onnx = AssetDatabase.LoadAssetAtPath<ModelAsset>(OnnxPath);
            if (lapMode)
            {
                var trackOnnx = AssetDatabase.LoadAssetAtPath<ModelAsset>(TrackOnnxPath);
                if (trackOnnx != null) onnx = trackOnnx;   // lap-trained policy when available, else the sprint policy + carrot
            }
            if (mjcf == null) Debug.LogWarning($"[PoDecath] {MjcfPath} missing; run training/rig_to_mjcf.py and copy models/athlete.xml here.");

            List<AthleteDefinition> roster = EnsureRoster(onnx);

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1.4f; light.shadows = LightShadows.Soft;
            light.color = new Color(1f, 0.97f, 0.92f);
            lightGo.transform.rotation = Quaternion.Euler(48f, -40f, 0f);

            // White House
            var wh = PrefabUtility.InstantiatePrefab(whAsset) as GameObject;
            if (wh == null) wh = Object.Instantiate(whAsset);
            wh.name = "WhiteHouse";
            Bounds building = new Bounds();
            Bounds residence = new Bounds();
            bool first = true, firstRes = true;
            foreach (var r in wh.GetComponentsInChildren<MeshRenderer>(true))
            {
                string n = r.gameObject.name;
                bool isResidence = n.Contains("Residence");
                bool structural = isResidence || n.Contains("Wings") || n.Contains("Portico");
                bool ground = n.Contains("Ground");
                if (structural || ground)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null && r.GetComponent<MeshCollider>() == null)
                        r.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
                }
                if (n.Contains("Tree") || ground || n.Contains("Flag")) continue;
                if (first) { building = r.bounds; first = false; } else building.Encapsulate(r.bounds);
                if (isResidence)
                {
                    if (firstRes) { residence = r.bounds; firstRes = false; } else residence.Encapsulate(r.bounds);
                }
            }
            if (first) building = new Bounds(Vector3.zero, new Vector3(150f, 23f, 45f));
            if (firstRes) residence = building;
            Debug.Log($"[PoDecath] White House bounds centre {building.center} size {building.size}");
            foreach (var t in wh.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = true;

            // The track goes on the residence roof only, so the wings stay clear.
            // Length comes straight off the bounds less the cornice overhang. Depth is a constant
            // because the Residence mesh bounds also contain the south bow, which reaches 4.4 m
            // further south than the roof does; the north edge of the bounds is clean, so the roof
            // centre is derived from it.
            const float cornice = 0.62f;      // cornice projection past the wall face
            const float RoofDepth = 25.9f;    // Executive Residence is 168 x 85 ft on plan
            float roofLen = residence.size.x - 2f * cornice;
            float roofDepth = Mathf.Min(RoofDepth, residence.size.z - 2f * cornice);
            Vector2 roofPlan = new Vector2(roofLen, roofDepth);
            Vector2 roofCentre = new Vector2(residence.center.x, residence.min.z + cornice + roofDepth * 0.5f);
            Debug.Log($"[PoDecath] residence roof {roofPlan.x:F1} x {roofPlan.y:F1} m " +
                      $"({roofPlan.x * roofPlan.y:F0} m2) centred at {roofCentre}");

            // Track
            KartTrackBuilder.TrackInfo track = KartTrackBuilder.Build(
                null, residence, asphalt, barrier, lineMat, column, roofPlan, roofCentre, 0.40f);
            Vector3 dir = (track.straightEnd - track.straightStart).normalized;

            // Centre-line description shared with training/envs/run_track.py
            var pathGo = new GameObject("TrackPath");
            var path = pathGo.AddComponent<TrackPath>();
            pathGo.transform.position = track.root.position;
            pathGo.transform.rotation = track.root.rotation;
            path.halfLength = track.halfLength;
            path.radius = track.radius;
            path.deckWidth = track.width;
            path.deckTopY = track.deckTopY;

            // Long jump: infield deck, runway and pit inside the loop, plus its runtime description.
            LongJumpPit pit = null;
            if (jumpMode)
            {
                LongJumpBuilder.PitInfo info = LongJumpBuilder.Build(null, track, infield, runway, sand, lineMat, foulMat, column);
                var pitGo = new GameObject("LongJumpPit");
                pit = pitGo.AddComponent<LongJumpPit>();
                pitGo.transform.position = info.root.position;   // the loop centre, offset off the flagpole
                pitGo.transform.rotation = info.root.rotation;
                pit.runwayStartX = info.runwayStartX;
                pit.takeoffX = info.takeoffX;
                pit.boardDepth = info.boardDepth;
                pit.pitNearX = info.pitNearX;
                pit.pitFarX = info.pitFarX;
                pit.runwayWidth = info.runwayWidth;
                pit.pitWidth = info.pitWidth;
                pit.surfaceY = info.surfaceY;
                pit.sandY = info.sandY;
            }

            // Camera + brain + Cinemachine
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.backgroundColor = new Color(0.55f, 0.7f, 0.9f);
            cam.nearClipPlane = 0.1f; cam.farClipPlane = 1500f;
            camGo.AddComponent<AudioListener>();
            var brain = camGo.AddComponent<CinemachineBrain>();
            brain.DefaultBlend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.EaseInOut, 0.6f);
            camGo.transform.position = track.straightStart + Vector3.up * 3f - dir * 6f;

            CinemachineCamera chase = MakeCam("CM Chase", new Vector3(-4.5f, 2.2f, 0f), BindingMode.LockToTargetWithWorldUp, 50f, new Vector2(0f, -0.1f));
            CinemachineCamera side = MakeCam("CM Side", new Vector3(0.5f, 1.6f, -6f), BindingMode.WorldSpace, 45f, new Vector2(0f, -0.05f));
            var rigGo = new GameObject("CameraRig");
            var camRig = rigGo.AddComponent<CameraRig>();
            camRig.chaseCamera = chase; camRig.sideCamera = side;

            // Event + spawner
            // The dash scales with the track: on the residence roof the straight is far shorter
            // than 100 m, so the race distance and lane pitch come from the track that got built.
            DashEvent dash;
            if (jumpMode)
            {
                var eventGo = new GameObject($"LongJumpEvent{Mathf.RoundToInt(pit.RunwayLength)}m");
                var jumpEvent = eventGo.AddComponent<LongJumpEvent>();
                jumpEvent.pit = pit;
                jumpEvent.attemptsEach = 3;
                jumpEvent.maxRaceSeconds = 600f;   // unused by the jump, but nothing should ever trip it
                dash = jumpEvent;
                dash.raceDistance = pit.RunwayLength;
            }
            else if (lapMode)
            {
                var eventGo = new GameObject($"LapEvent{Mathf.RoundToInt(path.LapLength)}m");
                var lapEvent = eventGo.AddComponent<LapEvent>();
                lapEvent.path = path;
                lapEvent.laps = 1;
                lapEvent.startS = 0f;
                lapEvent.lookahead = 9f;   // 6 m (training value) made athletes fall at the first bend; 9 m gave 5/5 clean laps
                lapEvent.maxRaceSeconds = 90f;
                dash = lapEvent;
                dash.raceDistance = path.LapLength;
            }
            else
            {
                var eventGo = new GameObject($"DashEvent{Mathf.RoundToInt(track.dashLength)}m");
                dash = eventGo.AddComponent<DashEvent>();
                dash.raceDistance = track.dashLength;
            }
            dash.startLine = track.straightStart;
            dash.direction = dir;
            dash.laneSpacing = track.laneSpacing;

            if (raceMode)
            {
                // A field of eight decides its own outcome; the modal ends the race instead of an auto-restart.
                var lapEvent = (LapEvent)dash;
                lapEvent.columns = 2;      // +-0.54 m lanes: the offsets that survive the 8.8 m bend
                lapEvent.rowSpacing = 1.5f;   // 8 rows x 1.5 m = 10.5 m, inside the 22.4 m straight
                lapEvent.maxRaceSeconds = 120f;
                dash.autoRestart = false;
            }

            var spawnerGo = new GameObject("AthleteSpawner");
            var spawner = spawnerGo.AddComponent<AthleteSpawner>();
            spawner.defaultMjcf = mjcf;
            spawner.defaultPolicyJson = policyJson;
            spawner.defaultSkin = skinAsset;
            spawner.roster = roster;
            spawner.dash = dash;
            spawner.cameraRig = camRig;
            spawner.footMaterial = footPm;
            spawner.debugVisualMaterial = debugMat;
            spawner.creatureLayerName = CreatureLayer;
            spawner.includeHeuristic = !lapMode;   // lap scene starts with only the RL athlete; flip the checkbox to add the RED pacer
            spawner.numberRunners = fieldMode;      // "Matt RL 1", "Matt Isaac 2", ... so a full field has distinct names

            // HUD
            PoDecathSceneBuilder.CreateEventSystem();
            Canvas canvas = PoDecathSceneBuilder.CreateCanvas("HUDCanvas");
            RectTransform safe = PoDecathSceneBuilder.CreateSafeArea(canvas.transform);
            PoDecathSceneBuilder.BuildHud(safe, null, null, camRig, null, handsOff: fieldMode || lapMode);   // stats card + Restart only
            var hud = safe.GetComponent<GameplayHUD>();
            hud.dash = dash;
            hud.menuSceneName = "MainMenu";

            if (fieldMode) RaceUiBuilder.BuildBroadcastAndResults(dash, path, camRig, safe, hud, pit);

            string scenePath = jumpMode ? LongJumpScenePath : raceMode ? RaceScenePath : (lapMode ? LapScenePath : ScenePath);
            EditorSceneManager.SaveScene(scene, scenePath);
            AddToBuild(scenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PoDecath] Rooftop scene built: {scenePath}");
        }

        static CinemachineCamera MakeCam(string name, Vector3 offset, BindingMode binding, float fov, Vector2 screenPos)
        {
            var go = new GameObject(name);
            var cm = go.AddComponent<CinemachineCamera>();
            cm.Lens.FieldOfView = fov; cm.Lens.NearClipPlane = 0.1f; cm.Lens.FarClipPlane = 1500f;
            cm.Priority = 10;
            var follow = go.AddComponent<CinemachineFollow>();
            follow.FollowOffset = offset;
            follow.TrackerSettings.BindingMode = binding;
            follow.TrackerSettings.PositionDamping = new Vector3(0.5f, 0.5f, 0.5f);
            follow.TrackerSettings.RotationDamping = new Vector3(0.5f, 0.5f, 0.5f);
            var comp = go.AddComponent<CinemachineRotationComposer>();
            comp.Composition.ScreenPosition = screenPos;
            comp.Damping = new Vector2(0.3f, 0.3f);
            return cm;
        }

        static List<AthleteDefinition> EnsureRoster(ModelAsset onnx)
        {
            PolicyLibraryTools.EnsureFolder(AthletesDir);
            var list = new List<AthleteDefinition>();
            list.Add(Def("Heuristic Sprinter", AthleteKind.Heuristic, null));
            list.Add(Def("Matt RL", AthleteKind.ReferenceRL, onnx));
            // Isaac Lab twin of the same body: YELLOW, only appears once training has exported it.
            var isaacOnnx = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Policies/athlete_isaac.onnx");
            if (isaacOnnx != null)
            {
                var isaac = Def("Matt Isaac", AthleteKind.CustomRL, isaacOnnx);
                isaac.customTint = new Color(1f, 0.85f, 0.1f, 1f);
                EditorUtility.SetDirty(isaac);
                list.Add(isaac);
            }
            return list;
        }

        static AthleteDefinition Def(string name, AthleteKind kind, ModelAsset model)
        {
            string path = $"{AthletesDir}/{name.Replace(' ', '_')}.asset";
            var def = AssetDatabase.LoadAssetAtPath<AthleteDefinition>(path);
            if (def == null)
            {
                def = ScriptableObject.CreateInstance<AthleteDefinition>();
                def.displayName = name; def.kind = kind;
                AssetDatabase.CreateAsset(def, path);
            }
            if (model != null) def.model = model;
            if (kind == AthleteKind.Heuristic) { def.topSpeed = 9.2f; def.accelSeconds = 3.2f; }
            EditorUtility.SetDirty(def);
            return def;
        }

        static void AddToBuild(string path)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            foreach (var s in scenes) if (s.path == path) return;
            scenes.Add(new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
