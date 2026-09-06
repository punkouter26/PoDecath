using UnityEngine;
using UnityEngine.UIElements;

namespace PoDecath.UI
{
    /// <summary>
    /// The bits every UI Toolkit screen in the project needs: the document's root, the safe area, and a
    /// handful of lookups that fail loudly rather than silently.
    ///
    /// The screens were uGUI hierarchies assembled by six hundred lines of editor code, one
    /// <c>RectTransform</c> at a time. They are UXML plus one stylesheet now, which means the layout can be
    /// read, the styling is in one place for all five screens, and changing the look of a button does not
    /// require re-running a scene builder. What is left in C# is the part that was always code: what the
    /// numbers say and what happens when something is pressed.
    ///
    /// Safe area is applied here rather than by a separate fitter component because in UI Toolkit it is
    /// four padding values on one element, not an anchor dance on a RectTransform.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public abstract class UiRoot : MonoBehaviour
    {
        [Tooltip("Applies Screen.safeArea as padding on the element named 'safe'. Off for a full-bleed screen.")]
        public bool applySafeArea = true;

        protected VisualElement Root { get; private set; }
        protected UIDocument Document { get; private set; }

        /// <summary>
        /// Shows or hides the whole screen. Used by the results card, which takes the live HUD and the
        /// broadcast overlay off the picture while it is up — a feed that is still counting metres over a
        /// finished race is the clearest way to look unfinished.
        ///
        /// Toggles the element the UXML calls "root", not the document's own root. They are not the same
        /// thing: UIDocument wraps every tree in a container of its own, so setting display on that
        /// container leaves the authored root untouched — and any screen whose UXML starts life with the
        /// hidden class stays hidden however many times it is shown. That is exactly how the diagnostics
        /// panel came to report itself visible while drawing nothing at all.
        /// </summary>
        public void SetScreenVisible(bool visible)
        {
            VisualElement target = ScreenRoot;
            if (target == null) return;
            target.EnableInClassList("hidden", !visible);
            target.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>Whether the screen is currently on the picture.</summary>
        public bool ScreenVisible
        {
            get
            {
                VisualElement target = ScreenRoot;
                return target != null && !target.ClassListContains("hidden") && target.style.display != DisplayStyle.None;
            }
        }

        /// <summary>
        /// The authored root of this screen, falling back to the document's own if it has none.
        /// Not named "Screen": that shadows UnityEngine.Screen, which this class reads for the safe area.
        /// </summary>
        VisualElement ScreenRoot => Root?.Q<VisualElement>("root") ?? Root;

        VisualElement _safe;
        Rect _lastSafeArea;
        Vector2Int _lastScreen;

        protected virtual void OnEnable()
        {
            Document = GetComponent<UIDocument>();
            Root = Document != null ? Document.rootVisualElement : null;
            if (Root == null) return;

            _safe = Root.Q<VisualElement>("safe");
            ApplySafeArea();
            Build();
        }

        /// <summary>Called once the document's root exists. Cache elements and hook callbacks here.</summary>
        protected abstract void Build();

        protected virtual void Update()
        {
            if (!applySafeArea) return;
            if (_lastSafeArea == Screen.safeArea && _lastScreen.x == Screen.width && _lastScreen.y == Screen.height) return;
            ApplySafeArea();
        }

        /// <summary>
        /// Pads the safe element in by the notch and the home indicator, converted from screen pixels into
        /// the panel's own reference units so it is right at any resolution.
        ///
        /// The clamp is not paranoia: the Device Simulator and some editor views report a safe area for a
        /// different resolution than <c>Screen.width/height</c>, and a safe area that does not fit inside
        /// the screen it claims to describe is meaningless.
        /// </summary>
        void ApplySafeArea()
        {
            _lastSafeArea = Screen.safeArea;
            _lastScreen = new Vector2Int(Screen.width, Screen.height);
            if (_safe == null || Screen.width <= 0 || Screen.height <= 0) return;

            Rect sa = Screen.safeArea;
            if (sa.width <= 0f || sa.height <= 0f || sa.xMax > Screen.width + 1f || sa.yMax > Screen.height + 1f)
                sa = new Rect(0f, 0f, Screen.width, Screen.height);

            // The panel scales to a reference resolution, so the insets have to be scaled with it or a
            // 1080-wide layout would be padded by raw device pixels.
            float scale = Root.resolvedStyle.width > 1f ? Root.resolvedStyle.width / Screen.width : 1f;
            _safe.style.paddingLeft = sa.xMin * scale;
            _safe.style.paddingRight = (Screen.width - sa.xMax) * scale;
            _safe.style.paddingTop = (Screen.height - sa.yMax) * scale;
            _safe.style.paddingBottom = sa.yMin * scale;
        }

        // ---------------------------------------------------------------- lookups

        /// <summary>An element by name, with a warning naming it if it is not there.</summary>
        protected T Find<T>(string name) where T : VisualElement
        {
            T e = Root?.Q<T>(name);
            if (e == null) Debug.LogWarning($"[{GetType().Name}] no '{name}' in {(Document != null && Document.visualTreeAsset != null ? Document.visualTreeAsset.name : "document")}.", this);
            return e;
        }

        protected static void Show(VisualElement e, bool visible)
        {
            if (e == null) return;
            e.EnableInClassList("hidden", !visible);
        }

        protected static void SetText(Label label, string text)
        {
            if (label != null && label.text != text) label.text = text;
        }

        protected static void SetText(Label label, string text, Color color)
        {
            if (label == null) return;
            if (label.text != text) label.text = text;
            label.style.color = color;
        }
    }
}
