using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// The UI Toolkit plumbing the scene builders need: the panel the screens are drawn on, and a helper
    /// that puts one document into a scene.
    ///
    /// The panel is generated rather than hand-authored, like everything else the project builds, so the
    /// reference resolution and the scaling rule live in code next to the reason for them instead of in an
    /// inspector nobody looks at.
    /// </summary>
    public static class UiBakery
    {
        const string UiDir = "Assets/UI";
        public const string PanelPath = UiDir + "/PoDecathPanel.asset";
        const string ThemePath = UiDir + "/PoDecath.tss";

        public const string HudUxml = UiDir + "/Hud.uxml";
        public const string BroadcastUxml = UiDir + "/Broadcast.uxml";
        public const string ResultsUxml = UiDir + "/Results.uxml";
        public const string SetupUxml = UiDir + "/Setup.uxml";
        public const string TelemetryUxml = UiDir + "/Telemetry.uxml";
        public const string MainMenuUxml = UiDir + "/MainMenu.uxml";
        public const string AppFrameUxml = UiDir + "/AppFrame.uxml";

        const int RefWidth = 1080;
        const int RefHeight = 1920;

        [MenuItem("PoDecath/Bake UI Panel", priority = 10)]
        public static void Bake()
        {
            PanelSettings panel = EnsurePanel();
            Selection.activeObject = panel;
            AssetDatabase.SaveAssets();
            Debug.Log($"[PoDecath] UI panel ready at {PanelPath} ({RefWidth}x{RefHeight}).");
        }

        /// <summary>
        /// The panel every screen is drawn on.
        ///
        /// One panel for all of them rather than one each, because the reference resolution and the scale
        /// mode have to agree — two panels with different scaling would put the HUD and the overlay at
        /// different sizes on the same screen. Sort order is what separates the documents, and that lives on
        /// the <see cref="UIDocument"/>, not here.
        /// </summary>
        public static PanelSettings EnsurePanel()
        {
            PolicyLibraryTools.EnsureFolder(UiDir);
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelPath);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(panel, PanelPath);
            }

            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            if (theme != null) panel.themeStyleSheet = theme;
            else Debug.LogWarning($"[PoDecath] {ThemePath} did not import as a ThemeStyleSheet; the panel will use Unity's default controls theme.");

            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(RefWidth, RefHeight);
            // Halfway between matching width and matching height. The game is designed portrait at
            // 1080x1920 but is played and captured in every aspect from a phone to a desktop game view;
            // matching width alone makes a landscape window's text enormous, matching height alone makes a
            // narrow phone's text tiny, and 0.5 is the only setting that is merely wrong at both extremes
            // rather than unusable at one.
            panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panel.match = 0.5f;
            panel.clearColor = false;
            panel.sortingOrder = 0f;

            EditorUtility.SetDirty(panel);
            return panel;
        }

        /// <summary>
        /// Adds one screen to the open scene: a GameObject with a <see cref="UIDocument"/> pointing at a
        /// UXML file, and the component that drives it.
        ///
        /// <paramref name="sortOrder"/> is the layering. Higher is nearer the viewer, and it matters:
        /// the results card has to come up over the overlay, and the diagnostics panel over everything.
        /// </summary>
        public static T AddScreen<T>(string name, string uxmlPath, float sortOrder) where T : MonoBehaviour
        {
            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            if (asset == null)
            {
                Debug.LogError($"[PoDecath] {uxmlPath} missing; screen '{name}' not built.");
                return null;
            }

            var go = new GameObject(name);
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = EnsurePanel();
            doc.visualTreeAsset = asset;
            doc.sortingOrder = sortOrder;
            return go.AddComponent<T>();
        }
    }
}
