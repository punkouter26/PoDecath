using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Unity.Cinemachine;
using Unity.Cinemachine.TargetTracking;
using Unity.InferenceEngine;
using PoDecath.Audio;
using PoDecath.Sim;
using PoDecath.UI;
using PoDecath.Cam;
using PoDecath.Env;
using PoDecath.Fx;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// Builds Assets/Scenes/Rooftop.unity: the owner's White House (glTF), a ProBuilder kart deck above it,
    /// the 100 m dash event with the roster (the reference RL athlete, plus the character models where the
    /// scene races a picked field), cameras and the portrait HUD.
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
        const string GetUpOnnxPath = "Assets/Policies/athlete_getup.onnx";

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
            RaceUiBuilder.BuildSetupScene(EnsureRoster(AssetDatabase.LoadAssetAtPath<ModelAsset>(TrackOnnxPath), true));
        }

        /// <summary>The long jump scene, then the setup menu again so it offers both events.</summary>
        [MenuItem("PoDecath/Build Long Jump Scene", priority = 5)]
        public static void BuildLongJump()
        {
            BuildScene(Mode.LongJump);
            RaceUiBuilder.BuildSetupScene(EnsureRoster(AssetDatabase.LoadAssetAtPath<ModelAsset>(TrackOnnxPath), true));
        }

        static void BuildScene(Mode mode)
        {
            bool lapMode = mode == Mode.Lap || mode == Mode.Race;   // both loop modes use the TrackPath and LapEvent
            bool raceMode = mode == Mode.Race;
            bool jumpMode = mode == Mode.LongJump;
            bool fieldMode = raceMode || jumpMode;                  // a picked field, broadcast gallery, results modal
            bool devScene = mode == Mode.Dash;                      // the only scene that keeps the hands-on HUD
            PoDecathSceneBuilder.EnsureLayer(CreatureLayer, 8);
            int creatureLayer = LayerMask.NameToLayer(CreatureLayer);
            // This used to say "no self-collision, like the MJCF", which is the opposite of what the MJCF
            // says. It is the widest of the gaps against AGENTS.md house rule 19: a layer-wide ignore stops
            // *any* two Creature colliders from touching, so a body's own parts never meet and
            // MjcfImporter.ApplyContactExcludes has nothing left to exclude. Left as it is because
            // re-enabling self-collision changes contact dynamics on every trained policy and is an owner
            // decision, not a silent fix -- see the file's own note when that decision is made.
            // Owner decision 2026-09-14: the layer-wide Creature/Creature ignore is GONE. Athletes now
            // collide with each other and with their own body parts, exactly as the training MJCF says
            // (contype/conaffinity 1 with the eleven named excludes mirrored by MjcfImporter). The old
            // ignore made that exclude list inert and let runners pass through one another. This call
            // also clears the bit baked into DynamicsManager, since it runs in the editor over a saved
            // project. Contact-trained policies are training alongside this change; older policies may
            // stumble when crowded, which is the documented cost of the switch.
            if (creatureLayer > 0) Physics.IgnoreLayerCollision(creatureLayer, creatureLayer, false);
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
            Material hurdleFrame = PoDecathSceneBuilder.Mat("Hurdle_Frame", new Color(0.9f, 0.9f, 0.92f));
            Material hurdleBar = PoDecathSceneBuilder.Mat("Hurdle_Bar", new Color(0.95f, 0.62f, 0.1f));
            PhysicsMaterial footPm = PoDecathSceneBuilder.EnsureFootPhysicsMaterial();
            PolicyLibraryTools.Refresh();
            // Surfaces before the track is built: KartTrackBuilder.Finish projects UVs at the scale each
            // material was dressed at, so the dressing has to exist by the time the first box is made.
            TextureBakery.EnsureBaked();
            VfxBakery.EnsureBaked();
            LookBakery.BakeProfiles();
            LookBakery.BakeSky();
            LookBakery.BakeHdriSkies();
            LookBakery.BakeWindowMaterial();
            // The whole sound set is generated, so it is baked here rather than assumed to be on disk.
            // The reference is picked up again after the new scene is opened, not kept from here.
            AudioBakery.BakeBank();

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

            List<AthleteDefinition> roster = EnsureRoster(onnx, fieldMode);

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Opening a scene unloads unused assets, so the bank is resolved on this side of it. A
            // reference held across NewScene still serialises correctly but compares equal to null, which
            // silently skips every guard that depends on it. See AudioBakery.LoadBank.
            AudioBank audio = AudioBakery.LoadBank();
            VfxBank vfx = VfxBakery.LoadBank();

            var lightGo = new GameObject("Sun");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(48f, -40f, 0f);

            // The look: one global Volume carrying the graded profile for whichever tier is running, the
            // sun above, the procedural sky behind it and the fog that ties them together. Everything the
            // component needs is resolved on this side of NewScene, because opening a scene unloads unused
            // assets and a stale wrapper serialises fine while comparing equal to null (see AudioBakery).
            var volumeGo = new GameObject("Global Volume");
            var volume = volumeGo.AddComponent<UnityEngine.Rendering.Volume>();
            volume.isGlobal = true;
            volume.priority = 0f;
            var look = volumeGo.AddComponent<SceneLook>();
            look.sun = light;
            look.volume = volume;
            look.pcProfile = LookBakery.LoadProfile(mobile: false);
            look.mobileProfile = LookBakery.LoadProfile(mobile: true);
            look.skyMaterial = AssetDatabase.LoadAssetAtPath<Material>(LookBakery.SkyMaterialPath);
            look.timeOfDay = SceneLook.TimeOfDay.Afternoon;
            look.skyAfternoon = AssetDatabase.LoadAssetAtPath<Material>(LookBakery.SkyAfternoonPath);
            look.skyGoldenHour = AssetDatabase.LoadAssetAtPath<Material>(LookBakery.SkyGoldenHourPath);
            look.skyNight = AssetDatabase.LoadAssetAtPath<Material>(LookBakery.SkyNightPath);
            look.windowLitMaterial = AssetDatabase.LoadAssetAtPath<Material>(LookBakery.WindowLitPath);
            volume.sharedProfile = look.pcProfile;
            // Applied here as well as at runtime: sky, ambient, fog and the sun's colour and intensity are
            // per-scene RenderSettings, so they have to be written before the scene is saved or the scene
            // on disk carries Unity's defaults and only looks right once something has pressed play.
            look.Apply(force: true);

            // White House
            var wh = PrefabUtility.InstantiatePrefab(whAsset) as GameObject;
            if (wh == null) wh = Object.Instantiate(whAsset);
            wh.name = "WhiteHouse";
            Bounds building = new Bounds();
            Bounds residence = new Bounds();
            bool first = true, firstRes = true;
            float groundY = 0f; bool haveGround = false;
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
                if (ground) { groundY = haveGround ? Mathf.Min(groundY, r.bounds.min.y) : r.bounds.min.y; haveGround = true; }
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
            // Decimated copies of the building, one LODGroup per part. On the mobile tier LOD0 is never
            // drawn at all (RenderTier sets maximumLODLevel), which is what brings the model under budget.
            int lodGroups = BuildingLodBuilder.Attach(wh);
            Debug.Log($"[PoDecath] White House LOD groups: {lodGroups}");
            foreach (var t in wh.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = true;
            look.building = wh;

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

            // The world beyond the roof: a skyline ring, the obelisk to the south, a treeline round the
            // grounds. Static, one mesh each, and far enough out that the fog does most of the work.
            SurroundingsBuilder.Build(building, haveGround ? groundY : building.min.y, vfx);

            // Furniture on the loop: pennants on the rail, four floodlight masts (lit only for the night
            // preset) and the shimmer over the straights (PC tier, afternoon only). The tape and the
            // crowd come later, once the event and the mix exist for them to read.
            var sceneryGo = new GameObject("TrackScenery");
            if (vfx != null)
            {
                var bunting = sceneryGo.AddComponent<Bunting>();
                bunting.path = path;
                bunting.material = vfx.pennant;

                var haze = sceneryGo.AddComponent<HeatHaze>();
                haze.path = path;
                haze.material = vfx.haze;
                look.heatHaze = haze;
            }
            var floods = sceneryGo.AddComponent<Floodlights>();
            floods.path = path;
            floods.cookie = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/Fx_soft.png");
            floods.mastMaterial = column;
            look.floodlights = floods;

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
            RaceEvent dash;
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
                lapEvent.secondsPerLap = 60f;   // LapEvent.Awake turns this and the lap count into maxRaceSeconds
                dash = lapEvent;
                dash.raceDistance = path.LapLength;
            }
            else
            {
                var eventGo = new GameObject($"RaceEvent{Mathf.RoundToInt(track.dashLength)}m");
                dash = eventGo.AddComponent<RaceEvent>();
                dash.raceDistance = track.dashLength;
            }
            dash.startLine = track.straightStart;
            dash.direction = dir;
            dash.laneSpacing = track.laneSpacing;

            if (raceMode)
            {
                // A field of eight decides its own outcome; the modal ends the race instead of an auto-restart.
                var lapEvent = (LapEvent)dash;
                lapEvent.maxLanes = 2;        // +-0.54 m lanes: the offsets that survive the 8.8 m bend
                lapEvent.rowSpacing = 1.5f;   // 4 rows x 1.5 m = 4.5 m, inside the 22.4 m straight
                lapEvent.secondsPerLap = 60f;   // 100 m gets 60 s, the 400 m 240 s, the 1500 m the 600 s cap
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
            spawner.numberRunners = fieldMode;      // "Matt RL 1", "Grandma 2", ... so a full field has distinct names
            spawner.audioBank = audio;              // every athlete gets its own footsteps
            spawner.vfxBank = vfx;                  // and its trail, blob shadow and foot dust
            // The get-up policy, if one has been trained. Absent, every athlete behaves exactly as before:
            // a fall is a DNF. Present, a fallen runner switches to it and tries to rejoin the race.
            spawner.getUpModel = AssetDatabase.LoadAssetAtPath<ModelAsset>(GetUpOnnxPath);
            if (spawner.getUpModel == null)
                Debug.Log($"[PoDecath] No {GetUpOnnxPath}; falls stay DNFs until the get-up policy is trained.");

            // Hurdles: only on the loop, and only when the picker asked for them. HurdleSet builds and
            // resets them itself; in the other lap events it costs one disabled component and nothing else.
            if (raceMode)
            {
                var hurdlesGo = new GameObject("Hurdles");
                var hurdles = hurdlesGo.AddComponent<HurdleSet>();
                hurdles.path = path;
                hurdles.race = dash;
                hurdles.frameMaterial = hurdleFrame;
                hurdles.barMaterial = hurdleBar;
                hurdles.clatter = audio != null ? audio.hurdleClatter : null;
                hurdles.clip = audio != null ? audio.hurdleClip : null;
            }

            // Screens. UI Toolkit documents on one shared panel, layered by sorting order; the layout and
            // the styling are in Assets/UI, not in this file.
            PoDecathSceneBuilder.CreateEventSystem();
            HudView hud = RaceUiBuilder.BuildHud(dash, camRig, handsOn: devScene);

            BroadcastDirector director = fieldMode ? RaceUiBuilder.BuildBroadcastAndResults(dash, path, camRig, hud, pit) : null;
            if (!fieldMode) RaceUiBuilder.AddFrameAndTelemetry(dash);   // the dev scenes get the frame and panel too

            // Focus follows the gallery. Without this the depth of field in the PC profile is authored at a
            // fixed 12 m, which is right for one shot in seven; with it, a close-up racks onto the athlete
            // and the city behind goes soft, and a stadium wide stops down until the whole roof is sharp.
            var focus = camGo.AddComponent<CinematicFocus>();
            focus.volume = volume;
            focus.director = director;
            focus.race = dash;
            focus.view = cam;

            // One probe over the deck. The building is white marble and glass and the track furniture is
            // metal; with nothing to reflect they all fall back to a flat sky colour and read as plastic.
            var probeGo = new GameObject("Rooftop Reflection Probe");
            probeGo.transform.position = new Vector3(track.center.x, track.deckTopY + 6f, track.center.z);
            var probe = probeGo.AddComponent<ReflectionProbe>();
            probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
            probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.OnAwake;   // the sky does not move mid-race
            probe.timeSlicingMode = UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.AllFacesAtOnce;
            probe.resolution = 128;
            probe.size = new Vector3(120f, 40f, 90f);
            probe.boxProjection = true;
            probe.cullingMask = ~0;
            probe.farClipPlane = 800f;

            // The picture, alongside the mix below. The library builds and pools every particle system in
            // code at the tier's budget; RaceVfx is what decides when one goes off, off the same race
            // state the overlay and the audio read.
            if (vfx != null)
            {
                var vfxGo = new GameObject("RaceVfx");
                var library = vfxGo.AddComponent<VfxLibrary>();
                library.bank = vfx;
                var raceVfx = vfxGo.AddComponent<RaceVfx>();
                raceVfx.race = dash;
                raceVfx.pit = pit;
            }

            // The tape across the line. Not for the long jump: nothing crosses a line there.
            if (vfx != null && vfx.tape != null && !jumpMode)
            {
                var tape = new GameObject("FinishTape").AddComponent<FinishTape>();
                tape.race = dash;
                tape.path = lapMode ? path : null;
                tape.material = vfx.tape;
            }

            // The camera's own reaction to an impact. Its own object because it moves its transform to the
            // impact point before firing, and nothing else in the scene should be dragged along with it.
            // Built in every scene: the listeners are already on the cameras either way, and a scene with
            // listeners and no source is a scene where impacts silently do nothing.
            new GameObject("CameraShake").AddComponent<CameraShake>();

            // The mix. Footsteps belong to the athletes; this is the crowd, the gun, the bell and the cuts.
            //
            // Three objects rather than one, because they are three different kinds of sound source. The
            // crowd is a ring of 3D emitters round the deck, so it swings behind the camera on a cut. The
            // cue pool is 3D one-shots fired from where things happen — the gun behind the grid, the bell at
            // the line, the thud in the pit. RaceAudio itself only owns what genuinely has no position: the
            // countdown, the stings, and the wind.
            if (audio != null)
            {
                var audioGo = new GameObject("RaceAudio");

                var ring = audioGo.AddComponent<CrowdRing>();
                ring.bank = audio;
                ring.path = path;

                audioGo.AddComponent<SpatialCue>();

                var raceAudio = audioGo.AddComponent<RaceAudio>();
                raceAudio.bank = audio;
                raceAudio.race = dash;
                raceAudio.director = director;
                raceAudio.crowd = ring;
                raceAudio.pit = pit;
                raceAudio.drama = director != null ? director.drama : null;

                // The crowd you can see, on the roofs outside the rail. It reads the level the ring plays at.
                if (vfx != null && vfx.crowd != null)
                {
                    var stands = new GameObject("CrowdStands").AddComponent<CrowdStands>();
                    stands.path = path;
                    stands.material = vfx.crowd;
                    stands.audioMix = raceAudio;
                }

                // The commentary. It lives on the audio object because it ducks the crowd under every line
                // and RaceAudio is what ticks the mix; without that the duck would never release.
                //
                // Only in the scenes that carry a broadcast. The dev scenes are one athlete being watched
                // in silence on purpose, and a voice calling a race with one runner in it has nothing to
                // say. See SpeechSynth for which platforms actually produce a voice: the caption is drawn
                // on all of them, the voice only on Android and Windows.
                if (fieldMode)
                {
                    var speech = audioGo.AddComponent<SpeechSynth>();
                    var commentary = audioGo.AddComponent<Commentary>();
                    commentary.race = dash;
                    commentary.drama = director != null ? director.drama : null;
                    commentary.voice = speech;

                    // The overlay was built before the mix existed, so the caption band is wired from here.
                    var overlay = Object.FindFirstObjectByType<PoDecath.UI.BroadcastView>();
                    if (overlay != null) overlay.commentary = commentary;
                }

                // The room, on the listener: the reverb of a stone courtyard and the dullness of distance.
                var acoustics = camGo.AddComponent<ListenerAcoustics>();
                acoustics.race = dash;
                acoustics.director = director;
                acoustics.occluders = creatureLayer >= 0 ? ~(1 << creatureLayer) : ~0;

                // The diagnostics panel reports what the crowd is doing, and it was built before the mix.
                var telemetry = Object.FindFirstObjectByType<PoDecath.Diag.TelemetryOverlay>();
                if (telemetry != null) telemetry.audioMix = raceAudio;
            }

            // Baked lighting: static flags, lightmap UVs on the track, a probe ring and the settings asset.
            // The bake itself is PoDecath/Bake Lighting, minutes of GPU time, run on demand.
            LightingBakery.Prepare(wh, light, path, track.root);
            look.Apply(force: true);

            string scenePath = jumpMode ? LongJumpScenePath : raceMode ? RaceScenePath : (lapMode ? LapScenePath : ScenePath);
            EditorSceneManager.SaveScene(scene, scenePath);
            AddToBuild(scenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PoDecath] Rooftop scene built: {scenePath}");
        }

        /// <summary>
        /// The development scenes' chase/side pair. <paramref name="horizontalFov"/> is the angle across
        /// the screen; PortraitLens turns it into the vertical one the lens actually wants, for whatever
        /// shape the display is. Same reason as the broadcast gallery: a fixed vertical angle is a
        /// telephoto on a phone held upright.
        /// </summary>
        static CinemachineCamera MakeCam(string name, Vector3 offset, BindingMode binding, float horizontalFov, Vector2 screenPos)
        {
            var go = new GameObject(name);
            var cm = go.AddComponent<CinemachineCamera>();
            var lens = go.AddComponent<PortraitLens>();
            lens.horizontalFov = horizontalFov;
            cm.Lens.FieldOfView = horizontalFov; cm.Lens.NearClipPlane = 0.1f; cm.Lens.FarClipPlane = 1500f;
            cm.Priority = 10;
            var follow = go.AddComponent<CinemachineFollow>();
            follow.FollowOffset = offset;
            follow.TrackerSettings.BindingMode = binding;
            follow.TrackerSettings.PositionDamping = new Vector3(0.5f, 0.5f, 0.5f);
            follow.TrackerSettings.RotationDamping = new Vector3(0.5f, 0.5f, 0.5f);
            var comp = go.AddComponent<CinemachineRotationComposer>();
            comp.Composition.ScreenPosition = screenPos;
            comp.Damping = new Vector2(0.3f, 0.3f);
            CameraShake.AddListener(cm);   // the dev scenes' chase pair flinches at impacts too
            return cm;
        }

        static List<AthleteDefinition> EnsureRoster(ModelAsset onnx, bool includeCharacters)
        {
            PolicyLibraryTools.EnsureFolder(AthletesDir);
            var list = new List<AthleteDefinition>();
            list.Add(Def("Matt RL", AthleteKind.ReferenceRL, onnx));
            // Everyone in Assets/Models/Characters. Same rig, same policy, their own skin and skeleton;
            // the setup menu offers one counter per entry, so this is what the owner picks a field from.
            //
            // Only for the scenes that race a picked field. Rooftop and RooftopLap are the development
            // scenes -- one runner round the loop, watched -- and they spawn their whole roster because
            // nothing has chosen one for them, so handing them the characters would quietly turn the
            // hands-off showcase into a nine-runner race.
            if (!includeCharacters) return list;
            var report = new System.Text.StringBuilder();
            List<AthleteDefinition> characters = AthleteRosterBuilder.BuildCharacters(onnx, report);
            list.AddRange(characters);
            if (characters.Count > 0)
                Debug.Log($"[PoDecath] Roster: {characters.Count} character model(s).\n{report}");
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
