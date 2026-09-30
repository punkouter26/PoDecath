using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using PoDecath.Cam;
using PoDecath.Env;
using PoDecath.Sim;

namespace PoDecath.Fx
{
    /// <summary>
    /// The finish-line gantry's two live faces: a video screen over the line showing the race, and the
    /// timing clock on the beam.
    ///
    /// The model is <c>Assets/Models/FinishGantry.glb</c>, built from nothing by
    /// <c>training/tools/finish_gantry.py</c> in Blender to real sizes: legs standing on the deck's two
    /// barriers 4.8 m apart, 4.2 m clear under the truss, a 3.2 x 1.8 m screen above it. The builder places
    /// it on the line as a prefab instance, so it can be nudged in the editor like any other prop.
    ///
    /// The screen is not a second copy of the broadcast picture. That would be a second full-resolution
    /// render of the whole roof every frame, which a phone cannot afford for a prop. It is its own small
    /// camera (192 x 108 on a phone, 384 x 216 on a PC) rendered by hand a few times a second with no
    /// shadows and no post, zoomed onto the athlete the gallery is featuring — which is what the screen at
    /// a real meet does anyway: the close-up, not the wide. Between refreshes the texture simply holds, the
    /// way a stadium screen's low frame rate reads from the stands.
    ///
    /// The clock is a one-label UI Toolkit panel drawn into a 512 x 128 texture, because the project has no
    /// TextMeshPro assets and a panel is the text renderer it already ships. It only redraws when the
    /// hundredths change, which a panel does on its own.
    /// </summary>
    public class FinishGantry : MonoBehaviour
    {
        [Header("Wiring")]
        public RaceEvent race;
        [Tooltip("Optional. The screen follows whoever the gallery is on; without it, the leader.")]
        public BroadcastDirector director;
        [Tooltip("The renderer of the model's Gantry_Screen part.")]
        public Renderer screen;
        [Tooltip("The renderer of the model's Gantry_Clock part.")]
        public Renderer clockFace;
        [Tooltip("The project's runtime theme, so the clock's label has a font. Assets/UI/PoDecath.tss.")]
        public ThemeStyleSheet theme;

        [Header("Screen feed")]
        [Tooltip("Refreshes a second on the PC tier.")]
        public float feedFpsPc = 15f;
        [Tooltip("Refreshes a second on the mobile tier: a prop, not a picture, so it is the first thing to give way.")]
        public float feedFpsMobile = 6f;
        [Tooltip("Vertical field of view of the screen's camera. Narrow: the screen is a close-up.")]
        [Range(10f, 60f)] public float feedFov = 22f;
        [Tooltip("Where the screen's camera stands, relative to the gantry: up the beam and back down the track.")]
        public Vector3 feedCameraOffset = new Vector3(0f, 6.5f, -3f);
        [Tooltip("Brightness of the screen. It is a light source in its own right, so it wants to read over daylight.")]
        [Range(0.5f, 4f)] public float screenGlow = 1.6f;

        RenderTexture _feed, _clockTex;
        Camera _cam;
        Material _screenMat, _clockMat;
        UIDocument _clockDoc;
        PanelSettings _clockPanel;
        Label _clockLabel;
        float _nextFeed;
        string _shownClock = "";

        void OnEnable()
        {
            BuildFeed();
            BuildClock();
        }

        void OnDisable()
        {
            if (_cam != null) Destroy(_cam.gameObject);
            if (_clockDoc != null) Destroy(_clockDoc.gameObject);
            if (_clockPanel != null) Destroy(_clockPanel);
            if (_feed != null) { _feed.Release(); Destroy(_feed); }
            if (_clockTex != null) { _clockTex.Release(); Destroy(_clockTex); }
            if (_screenMat != null) Destroy(_screenMat);
            if (_clockMat != null) Destroy(_clockMat);
        }

        // ---------------------------------------------------------------- the screen

        void BuildFeed()
        {
            if (screen == null) return;
            bool mobile = RenderTier.IsMobile;
            _feed = new RenderTexture(mobile ? 192 : 384, mobile ? 108 : 216, 16, RenderTextureFormat.ARGB32) { name = "GantryFeed" };
            _feed.Create();

            var go = new GameObject("Gantry Feed Camera");
            go.transform.SetParent(transform, false);
            _cam = go.AddComponent<Camera>();
            _cam.enabled = false;               // rendered by hand, at the feed's own rate
            _cam.targetTexture = _feed;
            _cam.fieldOfView = feedFov;
            _cam.nearClipPlane = 0.3f;
            _cam.farClipPlane = 400f;
            _cam.cullingMask = ~(1 << 5);       // not the UI layer
            var extra = go.AddComponent<UniversalAdditionalCameraData>();
            extra.renderShadows = false;
            extra.renderPostProcessing = false;
            extra.antialiasing = AntialiasingMode.None;
            extra.requiresDepthTexture = false;
            extra.requiresColorTexture = false;

            // An unlit surface: the screen emits its picture rather than being lit by the sun.
            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
            _screenMat = new Material(unlit != null ? unlit : screen.sharedMaterial.shader) { name = "GantryScreen (live)" };
            _screenMat.mainTexture = _feed;
            _screenMat.color = Color.white * screenGlow;
            screen.sharedMaterial = _screenMat;
        }

        void LateUpdate()
        {
            TickFeed();
            TickClock();
        }

        void TickFeed()
        {
            if (_cam == null || race == null) return;
            float fps = RenderTier.IsMobile ? feedFpsMobile : feedFpsPc;
            if (Time.unscaledTime < _nextFeed) return;
            _nextFeed = Time.unscaledTime + 1f / Mathf.Max(1f, fps);

            _cam.transform.position = transform.TransformPoint(feedCameraOffset);
            Vector3? target = Subject();
            // Nobody to follow yet (the grid is empty, or between attempts): look down the home straight.
            Vector3 look = target ?? transform.position - transform.forward * 20f + Vector3.up * 1f;
            _cam.transform.rotation = Quaternion.LookRotation(look - _cam.transform.position, Vector3.up);
            _cam.Render();
        }

        /// <summary>The featured athlete's chest, else the leader's; null before there is anyone.</summary>
        Vector3? Subject()
        {
            RaceEvent.Athlete a = director != null ? director.Featured : null;
            if (a == null)
            {
                List<RaceEvent.Athlete> order = race.LiveOrder();
                a = order.Count > 0 ? order[0] : null;
            }
            if (a == null) return null;
            Vector3 p = a.IsRL && a.rig != null ? a.rig.BasePosition : (a.go != null ? a.go.transform.position : transform.position);
            return p + Vector3.up * 0.3f;
        }

        // ---------------------------------------------------------------- the clock

        void BuildClock()
        {
            if (clockFace == null) return;
            _clockTex = new RenderTexture(512, 128, 0, RenderTextureFormat.ARGB32) { name = "GantryClock" };
            _clockTex.Create();

            _clockPanel = ScriptableObject.CreateInstance<PanelSettings>();
            _clockPanel.name = "GantryClockPanel";
            _clockPanel.targetTexture = _clockTex;
            _clockPanel.scaleMode = PanelScaleMode.ConstantPixelSize;
            _clockPanel.clearColor = true;
            _clockPanel.colorClearValue = new Color(0.02f, 0.02f, 0.03f, 1f);
            if (theme != null) _clockPanel.themeStyleSheet = theme;

            // Inactive while it is wired: a UIDocument binds to its panel in OnEnable.
            var go = new GameObject("Gantry Clock Panel");
            go.SetActive(false);
            go.transform.SetParent(transform, false);
            _clockDoc = go.AddComponent<UIDocument>();
            _clockDoc.panelSettings = _clockPanel;
            go.SetActive(true);

            _clockLabel = new Label("0.00");
            _clockLabel.style.flexGrow = 1f;
            _clockLabel.style.fontSize = 104;
            _clockLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _clockLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _clockLabel.style.color = new Color(1f, 0.78f, 0.25f);   // the amber of a trackside timer
            _clockDoc.rootVisualElement.style.flexGrow = 1f;
            _clockDoc.rootVisualElement.Add(_clockLabel);

            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
            _clockMat = new Material(unlit != null ? unlit : clockFace.sharedMaterial.shader) { name = "GantryClock (live)" };
            _clockMat.mainTexture = _clockTex;
            _clockMat.color = Color.white * 1.3f;
            clockFace.sharedMaterial = _clockMat;
        }

        void TickClock()
        {
            if (_clockLabel == null || race == null) return;
            string text;
            if (race.Current == RaceEvent.Phase.Countdown) text = Mathf.CeilToInt(Mathf.Max(0f, race.Countdown)).ToString();
            else if (race.Current == RaceEvent.Phase.Idle) text = "0.00";
            else
            {
                // Stops on the winner's time the moment somebody crosses, the way a finish clock does.
                float t = race.RaceTime;
                foreach (RaceEvent.Athlete a in race.Athletes)
                    if (a != null && a.finished && a.time > 0f && a.time < t) t = a.time;
                text = Format(t);
            }
            if (text == _shownClock) return;
            _shownClock = text;
            _clockLabel.text = text;
        }

        static string Format(float seconds)
        {
            if (seconds < 60f) return seconds.ToString("F2");
            int m = Mathf.FloorToInt(seconds / 60f);
            return $"{m}:{seconds - m * 60f:00.00}";
        }
    }
}
