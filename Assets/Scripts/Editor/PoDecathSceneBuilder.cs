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
    /// One-shot project scaffolding: layer, foot physics material, the policy library,
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
        const string RetiredMainMenuPath = ScenesDir + "/MainMenu.unity";
        const string SetupScenePath = ScenesDir + "/MAIN.unity";

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
            ConfigureBuildSettings();
            ConfigurePlayerSettings();
            ConfigurePhysics();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            if (System.IO.File.Exists(SetupScenePath)) EditorSceneManager.OpenScene(SetupScenePath);
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
        /// Takes the retired developer menu out of the build list, and leaves everything else alone.
        ///
        /// MainMenu was an evaluation screen whose three controls were a quality tier, a frame-rate target and
        /// a checkpoint picker that nothing read. The first two are the SETTINGS page of the diagnostics
        /// sheet now (DEBUG, bottom-left, on every screen); the scene is gone (2026-09-29, UI consolidation).
        /// This used to assign the whole list, which was actively destructive once RaceUiBuilder and
        /// RooftopSceneBuilder added their scenes, so it only ever removes.
        /// </summary>
        static void ConfigureBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (scenes.RemoveAll(s => s.path == RetiredMainMenuPath) > 0)
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
