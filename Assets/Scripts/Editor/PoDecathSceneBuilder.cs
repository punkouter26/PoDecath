using System.Collections.Generic;
using System.Linq;
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
    /// One-shot project scaffolding: layer, foot physics material, the MainMenu scene,
    /// build list, portrait player settings, physics stepping. Idempotent: re-running rebuilds the scenes.
    /// </summary>
    public static class PoDecathSceneBuilder
    {
        const string CreatureLayer = "Creature";
        const int CreatureLayerIndex = 8;
        const string MaterialsDir = "Assets/Materials";
        const string PrefabsDir = "Assets/Prefabs";
        const string ScenesDir = "Assets/Scenes";
        const string FootPhysMatPath = MaterialsDir + "/Foot.physicMaterial";
        const string MainMenuScenePath = ScenesDir + "/MainMenu.unity";

        const int RefWidth = 1080;
        const int RefHeight = 1920;

        static readonly Color CardColor = new Color(0.05f, 0.06f, 0.09f, 0.78f);
        static readonly Color AccentColor = new Color(0.18f, 0.75f, 0.55f, 1f);
        static readonly Color ButtonColor = new Color(0.16f, 0.18f, 0.24f, 0.95f);
        static readonly Color TextColor = new Color(0.94f, 0.95f, 0.97f, 1f);

        [MenuItem("PoDecath/Build Everything", priority = 0)]
        public static void BuildAll()
        {
            EnsureLayer(CreatureLayer, CreatureLayerIndex);
            EnsureFootPhysicsMaterial();
            PolicyLibraryTools.Refresh();
            BuildMainMenuScene();
            ConfigureBuildSettings();
            ConfigurePlayerSettings();
            ConfigurePhysics();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorSceneManager.OpenScene(MainMenuScenePath);
            Debug.Log("[PoDecath] Build Everything complete.");
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

        /// <summary>
        /// Makes sure MainMenu is in the build list without disturbing what is already there.
        ///
        /// This used to assign the whole list, which was correct when MainMenu and Arena were the only
        /// two scenes and actively destructive afterwards: RaceUiBuilder inserts RaceSetup at index 0 and
        /// RooftopSceneBuilder appends the four rooftop scenes, so running "Build Everything" threw all
        /// five away and left a build that could not reach a race.
        /// </summary>
        static void ConfigureBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (scenes.Any(s => s.path == MainMenuScenePath)) return;
            scenes.Add(new EditorBuildSettingsScene(MainMenuScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
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
            if (menu != null) menu.playSceneName = "RaceSetup";

            EditorSceneManager.SaveScene(scene, MainMenuScenePath);
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
