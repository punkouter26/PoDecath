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

            CreateEventSystem();
            Canvas canvas = CreateCanvas("MenuCanvas");
            RectTransform safe = CreateSafeArea(canvas.transform);

            RectTransform panel = CreatePanel("MenuPanel", safe, new Color(0, 0, 0, 0));
            Stretch(panel, new Vector2(60, 120), new Vector2(-60, -160));
            var vlg = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 26;
            vlg.childAlignment = TextAnchor.MiddleCenter;
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.padding = new RectOffset(24, 24, 24, 24);

            Text title = CreateText("Title", panel, "PoDecath", 84, TextAnchor.MiddleCenter, FontStyle.Bold);
            SetPreferredHeight(title.gameObject, 140);
            title.color = AccentColor;
            Text subtitle = CreateText("Subtitle", panel, "Physics creature evaluation\nIsaac Lab / MuJoCo policies on Unity ArticulationBody", 30, TextAnchor.MiddleCenter);
            SetPreferredHeight(subtitle.gameObject, 110);
            subtitle.color = new Color(0.7f, 0.74f, 0.8f);

            AddSpacer(panel, 30);
            Text polLabel = CreateText("PolicyLabel", panel, "Policy checkpoint", 32, TextAnchor.MiddleLeft, FontStyle.Bold);
            SetPreferredHeight(polLabel.gameObject, 50);
            Dropdown policyDd = CreateDropdown("PolicyDropdown", panel);

            Text arenaLabel = CreateText("ArenaLabel", panel, "Arena", 32, TextAnchor.MiddleLeft, FontStyle.Bold);
            SetPreferredHeight(arenaLabel.gameObject, 50);
            Dropdown arenaDd = CreateDropdown("ArenaDropdown", panel);

            AddSpacer(panel, 10);
            Toggle fps = CreateToggle("Fps60Toggle", panel, "Target 60 FPS (off = 30 FPS)");

            AddSpacer(panel, 30);
            Button start = CreateButton("StartButton", panel, "START EVALUATION", 40, AccentColor);
            SetPreferredHeight(start.gameObject, 170);

            AddSpacer(panel, 10);
            Text info = CreateText("InfoText", panel, "", 26, TextAnchor.MiddleCenter);
            SetPreferredHeight(info.gameObject, 100);
            info.color = new Color(0.6f, 0.65f, 0.7f);

            var ctrlGo = new GameObject("MainMenu");
            var ctrl = ctrlGo.AddComponent<MainMenuController>();
            ctrl.policyDropdown = policyDd;
            ctrl.arenaDropdown = arenaDd;
            ctrl.fps60Toggle = fps;
            ctrl.startButton = start;
            ctrl.infoText = info;
            ctrl.arenaSceneName = "Arena";

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

            // HUD
            CreateEventSystem();
            Canvas canvas = CreateCanvas("HUDCanvas");
            RectTransform safe = CreateSafeArea(canvas.transform);
            BuildHud(safe, runner, episodes, camRig, perturb);

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

        // ------------------------------------------------------------------ HUD

        internal static void BuildHud(RectTransform safe, PolicyRunner runner, EpisodeManager episodes, CameraRig camRig, TouchPerturbation perturb, bool handsOff = false)
        {
            // Top status card
            RectTransform card = CreatePanel("StatusCard", safe, CardColor);
            card.anchorMin = new Vector2(0, 1);
            card.anchorMax = new Vector2(1, 1);
            card.pivot = new Vector2(0.5f, 1);
            card.offsetMin = new Vector2(36, -370);
            card.offsetMax = new Vector2(-36, -36);
            var cardLayout = card.gameObject.AddComponent<VerticalLayoutGroup>();
            cardLayout.padding = new RectOffset(28, 28, 22, 22);
            cardLayout.spacing = 6;
            cardLayout.childControlHeight = true;
            cardLayout.childControlWidth = true;
            cardLayout.childForceExpandHeight = false;
            cardLayout.childForceExpandWidth = true;

            Text model = CreateText("ModelText", card, "model", 26, TextAnchor.MiddleLeft);
            model.color = AccentColor;
            SetPreferredHeight(model.gameObject, 40);
            Text speed = CreateText("SpeedText", card, "Speed", 40, TextAnchor.MiddleLeft, FontStyle.Bold);
            SetPreferredHeight(speed.gameObject, 58);
            Text dist = CreateText("DistanceText", card, "Distance", 32, TextAnchor.MiddleLeft);
            SetPreferredHeight(dist.gameObject, 48);
            Text stab = CreateText("StabilityText", card, "Stability", 32, TextAnchor.MiddleLeft);
            SetPreferredHeight(stab.gameObject, 48);
            Text ep = CreateText("EpisodeText", card, "Episode", 32, TextAnchor.MiddleLeft);
            SetPreferredHeight(ep.gameObject, 48);
            Text last = CreateText("LastResultText", card, "Last: -", 26, TextAnchor.MiddleLeft);
            last.color = new Color(0.72f, 0.76f, 0.82f);
            SetPreferredHeight(last.gameObject, 40);

            // Bottom control bar
            RectTransform bar = CreatePanel("ControlBar", safe, CardColor);
            bar.anchorMin = new Vector2(0, 0);
            bar.anchorMax = new Vector2(1, 0);
            bar.pivot = new Vector2(0.5f, 0);
            bar.offsetMin = new Vector2(36, 36);
            bar.offsetMax = new Vector2(-36, 330);
            var barLayout = bar.gameObject.AddComponent<VerticalLayoutGroup>();
            barLayout.padding = new RectOffset(20, 20, 18, 18);
            barLayout.spacing = 14;
            barLayout.childControlHeight = true;
            barLayout.childControlWidth = true;
            barLayout.childForceExpandHeight = false;
            barLayout.childForceExpandWidth = true;

            RectTransform row = CreatePanel("ButtonRow", bar, new Color(0, 0, 0, 0));
            SetPreferredHeight(row.gameObject, 150);
            var rowLayout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 14;
            rowLayout.childControlHeight = true;
            rowLayout.childControlWidth = true;
            rowLayout.childForceExpandHeight = true;
            rowLayout.childForceExpandWidth = true;

            Button restart = CreateButton("RestartButton", row, "Restart", handsOff ? 40 : 30, handsOff ? AccentColor : ButtonColor);
            Button slow = null, camera = null, menu = null;
            Toggle flick = null;
            if (!handsOff)
            {
                slow = CreateButton("SlowMoButton", row, "0.5x", 30, ButtonColor);
                camera = CreateButton("CameraButton", row, "Cam: Chase", 30, ButtonColor);
                menu = CreateButton("MenuButton", row, "Menu", 30, ButtonColor);
                flick = CreateToggle("PerturbToggle", bar, "Touch-flick perturbation (swipe mid-screen)");
                SetPreferredHeight(flick.gameObject, 80);
            }
            else
            {
                // Hands-off: one thumb-height Restart button, nothing else
                bar.offsetMax = new Vector2(-36, 226);
            }

            var hud = safe.gameObject.AddComponent<GameplayHUD>();
            hud.episodes = episodes;
            hud.runner = runner;
            hud.cameraRig = camRig;
            hud.perturbation = perturb;
            hud.modelText = model;
            hud.speedText = speed;
            hud.distanceText = dist;
            hud.stabilityText = stab;
            hud.episodeText = ep;
            hud.lastResultText = last;
            hud.restartButton = restart;
            hud.slowMoButton = slow;
            hud.cameraButton = camera;
            hud.menuButton = menu;
            hud.perturbToggle = flick;
            hud.slowMoLabel = slow != null ? slow.GetComponentInChildren<Text>() : null;
            hud.cameraLabel = camera != null ? camera.GetComponentInChildren<Text>() : null;
            hud.menuSceneName = "MainMenu";
        }

        // ------------------------------------------------------------------ uGUI helpers

        static DefaultControls.Resources UiResources() => new DefaultControls.Resources
        {
            standard = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"),
            background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd"),
            inputField = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/InputFieldBackground.psd"),
            knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd"),
            checkmark = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd"),
            dropdown = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/DropdownArrow.psd"),
            mask = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UIMask.psd"),
        };

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

        internal static Canvas CreateCanvas(string name)
        {
            var go = new GameObject(name);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(RefWidth, RefHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        internal static RectTransform CreateSafeArea(Transform parent)
        {
            var go = new GameObject("SafeArea", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            Stretch(rt, Vector2.zero, Vector2.zero);
            go.AddComponent<SafeAreaFitter>();
            return rt;
        }

        internal static RectTransform CreatePanel(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = color.a > 0.01f;
            if (color.a > 0.01f)
            {
                img.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd");
                img.type = Image.Type.Sliced;
            }
            var rt = go.GetComponent<RectTransform>();
            Stretch(rt, Vector2.zero, Vector2.zero);
            return rt;
        }

        internal static void Stretch(RectTransform rt, Vector2 offsetMin, Vector2 offsetMax)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
        }

        internal static Text CreateText(string name, Transform parent, string content, int size, TextAnchor anchor, FontStyle style = FontStyle.Normal)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = content;
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = anchor;
            t.color = TextColor;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }

        internal static Button CreateButton(string name, Transform parent, string label, int fontSize, Color color)
        {
            GameObject go = DefaultControls.CreateButton(UiResources());
            go.name = name;
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            var btn = go.GetComponent<Button>();
            var colors = btn.colors;
            colors.highlightedColor = Color.Lerp(color, Color.white, 0.15f);
            colors.pressedColor = Color.Lerp(color, Color.black, 0.25f);
            btn.colors = colors;
            var text = go.GetComponentInChildren<Text>();
            text.text = label;
            text.fontSize = fontSize;
            text.fontStyle = FontStyle.Bold;
            text.color = TextColor;
            return btn;
        }

        static Toggle CreateToggle(string name, Transform parent, string label)
        {
            GameObject go = DefaultControls.CreateToggle(UiResources());
            go.name = name;
            go.transform.SetParent(parent, false);
            var toggle = go.GetComponent<Toggle>();
            var bg = go.transform.Find("Background") as RectTransform;
            if (bg != null)
            {
                bg.sizeDelta = new Vector2(56, 56);
                bg.anchoredPosition = new Vector2(10, 0);
                var check = bg.Find("Checkmark") as RectTransform;
                if (check != null) check.sizeDelta = new Vector2(44, 44);
            }
            var text = go.GetComponentInChildren<Text>();
            if (text != null)
            {
                text.text = label;
                text.fontSize = 28;
                text.color = TextColor;
                var trt = text.rectTransform;
                trt.offsetMin = new Vector2(80, 0);
            }
            SetPreferredHeight(go, 70);
            return toggle;
        }

        static Dropdown CreateDropdown(string name, Transform parent)
        {
            GameObject go = DefaultControls.CreateDropdown(UiResources());
            go.name = name;
            go.transform.SetParent(parent, false);
            var dd = go.GetComponent<Dropdown>();
            var img = go.GetComponent<Image>();
            img.color = ButtonColor;
            dd.captionText.fontSize = 32;
            dd.captionText.color = TextColor;
            dd.itemText.fontSize = 30;
            dd.itemText.color = TextColor;
            var arrow = go.transform.Find("Arrow") as RectTransform;
            if (arrow != null) arrow.sizeDelta = new Vector2(48, 48);

            RectTransform template = dd.template;
            if (template != null)
            {
                template.sizeDelta = new Vector2(0, 420);
                var tImg = template.GetComponent<Image>();
                if (tImg != null) tImg.color = new Color(0.12f, 0.13f, 0.17f, 0.98f);
                var item = template.Find("Viewport/Content/Item") as RectTransform;
                if (item != null) item.sizeDelta = new Vector2(0, 84);
                var content = template.Find("Viewport/Content") as RectTransform;
                if (content != null) content.sizeDelta = new Vector2(0, 84);
                var itemBg = template.Find("Viewport/Content/Item/Item Background")?.GetComponent<Image>();
                if (itemBg != null) itemBg.color = new Color(0.16f, 0.18f, 0.24f, 1f);
                var itemCheck = template.Find("Viewport/Content/Item/Item Checkmark") as RectTransform;
                if (itemCheck != null) itemCheck.sizeDelta = new Vector2(40, 40);
            }
            SetPreferredHeight(go, 110);
            return dd;
        }

        internal static void AddSpacer(Transform parent, float height)
        {
            var go = new GameObject("Spacer", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            SetPreferredHeight(go, height);
        }

        internal static void SetPreferredHeight(GameObject go, float height)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;
        }
    }
}
