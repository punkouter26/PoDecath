using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Unity.Cinemachine;
using Unity.Cinemachine.TargetTracking;
using PoDecath.Sim;
using PoDecath.UI;
using PoDecath.Cam;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// One-shot project scaffolding: layer, materials, creature prefab, MainMenu and Arena scenes,
    /// build list, portrait player settings, physics stepping. Idempotent: re-running rebuilds the scenes.
    /// </summary>
    public static class PoDecathSceneBuilder
    {
        const string CreatureLayer = "Creature";
        const int CreatureLayerIndex = 8;
        const string MaterialsDir = "Assets/Materials";
        const string PrefabsDir = "Assets/Prefabs";
        const string ScenesDir = "Assets/Scenes";
        const string CreaturePrefabPath = PrefabsDir + "/Quadruped.prefab";
        const string FootPhysMatPath = MaterialsDir + "/Foot.physicMaterial";
        const string MainMenuScenePath = ScenesDir + "/MainMenu.unity";
        const string ArenaScenePath = ScenesDir + "/Arena.unity";

        const int RefWidth = 1080;
        const int RefHeight = 1920;

        static readonly Color CardColor = new Color(0.05f, 0.06f, 0.09f, 0.78f);
        static readonly Color AccentColor = new Color(0.18f, 0.75f, 0.55f, 1f);
        static readonly Color ButtonColor = new Color(0.16f, 0.18f, 0.24f, 0.95f);
        static readonly Color TextColor = new Color(0.94f, 0.95f, 0.97f, 1f);

        struct Mats
        {
            public Material floor, wall, obstacle, target, body, limb;
        }

        [MenuItem("PoDecath/Build Everything", priority = 0)]
        public static void BuildAll()
        {
            EnsureLayer(CreatureLayer, CreatureLayerIndex);
            Mats mats = EnsureMaterials();
            PhysicsMaterial footPm = EnsureFootPhysicsMaterial();
            PolicyLibraryTools.Refresh();
            GameObject prefab = BuildCreaturePrefab(mats, footPm);
            BuildMainMenuScene();
            BuildArenaScene(prefab, mats);
            ConfigureBuildSettings();
            ConfigurePlayerSettings();
            ConfigurePhysics();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorSceneManager.OpenScene(ArenaScenePath);
            Debug.Log("[PoDecath] Build Everything complete.");
        }

        [MenuItem("PoDecath/Rebuild Creature Prefab", priority = 1)]
        public static void RebuildCreaturePrefabMenu()
        {
            EnsureLayer(CreatureLayer, CreatureLayerIndex);
            BuildCreaturePrefab(EnsureMaterials(), EnsureFootPhysicsMaterial());
            AssetDatabase.SaveAssets();
        }

        // ------------------------------------------------------------------ project setup

        internal static void EnsureLayer(string name, int index)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0) return;
            var so = new SerializedObject(assets[0]);
            SerializedProperty layers = so.FindProperty("layers");
            for (int i = 0; i < layers.arraySize; i++)
                if (layers.GetArrayElementAtIndex(i).stringValue == name) return;
            SerializedProperty slot = layers.GetArrayElementAtIndex(index);
            if (string.IsNullOrEmpty(slot.stringValue)) slot.stringValue = name;
            else
            {
                for (int i = 8; i < layers.arraySize; i++)
                {
                    var p = layers.GetArrayElementAtIndex(i);
                    if (string.IsNullOrEmpty(p.stringValue)) { p.stringValue = name; break; }
                }
            }
            so.ApplyModifiedProperties();
        }

        static Mats EnsureMaterials()
        {
            PolicyLibraryTools.EnsureFolder(MaterialsDir);
            return new Mats
            {
                floor = Mat("Arena_Floor", new Color(0.20f, 0.22f, 0.26f)),
                wall = Mat("Arena_Wall", new Color(0.62f, 0.38f, 0.22f)),
                obstacle = Mat("Arena_Obstacle", new Color(0.32f, 0.48f, 0.62f)),
                target = Mat("Arena_Target", new Color(0.20f, 0.92f, 0.45f)),
                body = Mat("Creature_Body", new Color(0.92f, 0.86f, 0.72f)),
                limb = Mat("Creature_Limb", new Color(0.22f, 0.24f, 0.28f)),
            };
        }

        internal static Material Mat(string name, Color color)
        {
            string path = $"{MaterialsDir}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                Shader s = Shader.Find("Universal Render Pipeline/Lit");
                m = new Material(s != null ? s : Shader.Find("Standard"));
                m.color = color;
                AssetDatabase.CreateAsset(m, path);
            }
            return m;
        }

        internal static PhysicsMaterial EnsureFootPhysicsMaterial()
        {
            PolicyLibraryTools.EnsureFolder(MaterialsDir);
            var pm = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(FootPhysMatPath);
            if (pm == null)
            {
                pm = new PhysicsMaterial("Foot")
                {
                    staticFriction = 1f,
                    dynamicFriction = 1f,
                    bounciness = 0f,
                    frictionCombine = PhysicsMaterialCombine.Average,
                    bounceCombine = PhysicsMaterialCombine.Minimum,
                };
                AssetDatabase.CreateAsset(pm, FootPhysMatPath);
            }
            return pm;
        }

        static GameObject BuildCreaturePrefab(Mats mats, PhysicsMaterial footPm)
        {
            PolicyLibraryTools.EnsureFolder(PrefabsDir);
            int layer = LayerMask.NameToLayer(CreatureLayer);
            if (layer < 0) layer = 0;
            CreatureRig rig = QuadrupedFactory.Build("Quadruped", QuadrupedFactory.Dims.Go2, layer, footPm, mats.body, mats.limb);
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(rig.gameObject, CreaturePrefabPath);
            Object.DestroyImmediate(rig.gameObject);
            return prefab;
        }

        static void ConfigureBuildSettings()
        {
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(MainMenuScenePath, true),
                new EditorBuildSettingsScene(ArenaScenePath, true),
            };
        }

        static void ConfigurePlayerSettings()
        {
            PlayerSettings.productName = "PoDecath";
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = false;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;
            PlayerSettings.defaultScreenWidth = RefWidth;
            PlayerSettings.defaultScreenHeight = RefHeight;
        }

        static void ConfigurePhysics()
        {
            Time.fixedDeltaTime = 1f / 200f;
            Physics.defaultSolverIterations = 8;
            Physics.defaultSolverVelocityIterations = 2;
        }

        // ------------------------------------------------------------------ scenes

        static void BuildMainMenuScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.07f, 0.08f, 0.11f);
            camGo.transform.position = new Vector3(0, 1, -10);
            camGo.AddComponent<AudioListener>();

            // One UI Toolkit document. What was ninety lines of VerticalLayoutGroup, Dropdown and Text
            // construction is a UXML layout in Assets/UI/MainMenu.uxml wearing the shared stylesheet.
            CreateEventSystem();
            var menu = UiBakery.AddScreen<MainMenuView>("MainMenu", UiBakery.MainMenuUxml, 0f);
            if (menu != null) menu.arenaSceneName = "Arena";

            EditorSceneManager.SaveScene(scene, MainMenuScenePath);
        }

        static void BuildArenaScene(GameObject creaturePrefab, Mats mats)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Light
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.3f;
            light.shadows = LightShadows.Soft;
            light.color = new Color(1f, 0.96f, 0.9f);
            lightGo.transform.rotation = Quaternion.Euler(55f, -35f, 0f);

            // Camera + brain
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.66f, 0.78f);
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 80f;
            camGo.AddComponent<AudioListener>();
            var brain = camGo.AddComponent<CinemachineBrain>();
            brain.DefaultBlend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.EaseInOut, 0.6f);
            camGo.transform.position = new Vector3(-3f, 1.5f, 0f);

            // Arena
            var arenaGo = new GameObject("Arena");
            var arena = arenaGo.AddComponent<ArenaGenerator>();
            arena.floorMaterial = mats.floor;
            arena.wallMaterial = mats.wall;
            arena.obstacleMaterial = mats.obstacle;
            arena.targetMaterial = mats.target;

            // Creature
            var creatureGo = (GameObject)PrefabUtility.InstantiatePrefab(creaturePrefab);
            creatureGo.name = "Quadruped";
            creatureGo.transform.position = new Vector3(0f, 0.4f, 0f);
            var rig = creatureGo.GetComponent<CreatureRig>();
            Transform baseT = rig != null && rig.root != null ? rig.root.transform : creatureGo.transform;

            // Cinemachine cameras (portrait framing: target sits slightly below centre so the lane ahead is visible)
            CinemachineCamera chase = MakeCmCamera("CM Chase", baseT, new Vector3(-2.4f, 1.5f, 0f), BindingMode.LockToTargetWithWorldUp, 52f, new Vector2(0f, -0.12f));
            CinemachineCamera side = MakeCmCamera("CM Side", baseT, new Vector3(0.6f, 1.1f, -3.2f), BindingMode.WorldSpace, 48f, new Vector2(0f, -0.08f));
            var rigGo = new GameObject("CameraRig");
            var camRig = rigGo.AddComponent<CameraRig>();
            camRig.chaseCamera = chase;
            camRig.sideCamera = side;

            // Simulation
            var simGo = new GameObject("Simulation");
            var runner = simGo.AddComponent<PolicyRunner>();
            var command = simGo.AddComponent<VelocityCommandSource>();
            var episodes = simGo.AddComponent<EpisodeManager>();
            var bootstrap = simGo.AddComponent<ArenaBootstrap>();
            var perturb = simGo.AddComponent<TouchPerturbation>();

            runner.rig = rig;
            runner.commandSource = command;
            runner.creatureLayerName = CreatureLayer;
            episodes.runner = runner;
            episodes.rig = rig;
            episodes.commandSource = command;
            episodes.arena = arena;
            bootstrap.arena = arena;
            bootstrap.runner = runner;
            bootstrap.episodes = episodes;
            bootstrap.creature = rig;
            bootstrap.cameraRig = camRig;
            perturb.rig = rig;
            perturb.cam = cam;
            perturb.enabled = false;

            // HUD. The arena is the hands-on development scene, so it keeps slow motion and the camera
            // toggle and opens with the stats card up.
            CreateEventSystem();
            var hud = UiBakery.AddScreen<HudView>("HUD", UiBakery.HudUxml, 10f);
            if (hud != null)
            {
                hud.runner = runner;
                hud.episodes = episodes;
                hud.cameraRig = camRig;
                hud.perturbation = perturb;
                hud.handsOn = true;
                hud.statsHiddenAtStart = false;
            }
            RaceUiBuilder.AddTelemetry(null);

            EditorSceneManager.SaveScene(scene, ArenaScenePath);
        }

        static CinemachineCamera MakeCmCamera(string name, Transform target, Vector3 offset, BindingMode binding, float fov, Vector2 screenPos)
        {
            var go = new GameObject(name);
            var cm = go.AddComponent<CinemachineCamera>();
            cm.Follow = target;
            cm.LookAt = target;
            cm.Lens.FieldOfView = fov;
            cm.Lens.NearClipPlane = 0.05f;
            cm.Lens.FarClipPlane = 80f;
            cm.Priority = 10;

            var follow = go.AddComponent<CinemachineFollow>();
            follow.FollowOffset = offset;
            follow.TrackerSettings.BindingMode = binding;
            follow.TrackerSettings.PositionDamping = new Vector3(0.6f, 0.6f, 0.6f);
            follow.TrackerSettings.RotationDamping = new Vector3(0.5f, 0.5f, 0.5f);

            var composer = go.AddComponent<CinemachineRotationComposer>();
            composer.Composition.ScreenPosition = screenPos;
            composer.Damping = new Vector2(0.4f, 0.4f);
            return cm;
        }

        // ------------------------------------------------------------------ input

        /// <summary>
        /// The EventSystem. UI Toolkit draws its own panels but still takes pointer and key input
        /// through one, so every scene with a screen in it needs exactly one of these.
        /// </summary>
        internal static void CreateEventSystem()
        {
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
            go.AddComponent<StandaloneInputModule>();
#endif
        }

    }
}
