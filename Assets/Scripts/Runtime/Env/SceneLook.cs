using UnityEngine;
using UnityEngine.Rendering;

namespace PoDecath.Env
{
    /// <summary>
    /// Owns how the rooftop is lit and graded: the global post-processing Volume, the sun, the sky behind
    /// it, the ambient and fog that tie the two together, and which of those the current
    /// <see cref="RenderTier"/> can afford.
    ///
    /// The scene builders used to place one directional light and leave everything else on Unity's
    /// defaults, which is why the deck read as a grey slab under a lamp. Three things fix that and none of
    /// them is expensive: a sky that the ambient probe is actually generated from (so the shadow side of an
    /// athlete picks up blue rather than black), a warm sun angled low enough to throw a shadow with a
    /// direction, and distance fog matched to the sky so the city behind the White House recedes instead of
    /// sitting flat against it.
    ///
    /// Time of day is a preset rather than a slider because the three that matter are lit differently
    /// rather than merely rotated: an afternoon has a high neutral key, a golden hour has a long warm one
    /// with a cool sky opposite it, and a floodlit night has almost no key at all and lives on ambient.
    /// </summary>
    [ExecuteAlways]   // the editor must show the same rooftop the game does, without entering play mode
    [DefaultExecutionOrder(-50)]
    public class SceneLook : MonoBehaviour
    {
        public enum TimeOfDay { Afternoon, GoldenHour, Floodlit }

        [Header("Wiring")]
        [Tooltip("The scene's key light. Left alone if empty, so a scene can opt out.")]
        public Light sun;
        [Tooltip("Global volume this component switches between the tier profiles.")]
        public Volume volume;
        public VolumeProfile pcProfile;
        public VolumeProfile mobileProfile;
        [Tooltip("Procedural skybox material, baked by PoDecath/Bake Look.")]
        public Material skyMaterial;
        [Tooltip("Panoramic HDRI skies baked by PoDecath/Bake Look, one per preset. A missing one falls back to the procedural sky.")]
        public Material skyAfternoon;
        public Material skyGoldenHour;
        public Material skyNight;
        [Tooltip("Optional. Lit for the floodlit preset, off for the others.")]
        public Floodlights floodlights;
        [Tooltip("Optional. On for the afternoon, off for the others.")]
        public HeatHaze heatHaze;
        [Tooltip("The building whose windows light up at night.")]
        public GameObject building;
        [Tooltip("The warm emissive the windows swap to at night, baked by PoDecath/Bake Look.")]
        public Material windowLitMaterial;

        [Header("Look")]
        public TimeOfDay timeOfDay = TimeOfDay.Afternoon;
        [Tooltip("Re-applies every frame in the editor so the preset can be auditioned without playing.")]
        public bool liveUpdate = true;

        [Header("Fog")]
        [Tooltip("Distance haze. The White House sits in a city block that reaches 1.5 km; without it the "
               + "far buildings are as sharp as the deck rail and the roof stops reading as high up.")]
        public bool fog = true;
        public float fogStart = 120f;
        public float fogEnd = 1400f;

        TimeOfDay _applied = (TimeOfDay)(-1);
        Tier _appliedTier = (Tier)(-1);

        /// <summary>One preset, in the four numbers that distinguish it from the others.</summary>
        struct Preset
        {
            public Vector3 sunEuler;
            public Color sunColor;
            public float sunIntensity;
            public Color skyTint;
            public Color groundColor;
            public float atmosphere;
            public float exposure;
            public Color ambient;        // used when the sky is too dark to generate a useful probe
            public float ambientIntensity;
            public Color fogColor;
            public float wind;           // 0..1, published as _PoDecathWind for the bunting and the tape
            public Color crowdTint;      // _PoDecathCrowdTint: the stands are unlit, so this is their light
            public float skyExposure;    // for the HDRI sky, when there is one
        }

        static Preset For(TimeOfDay t) => t switch
        {
            TimeOfDay.GoldenHour => new Preset
            {
                sunEuler = new Vector3(14f, -58f, 0f),
                sunColor = new Color(1f, 0.79f, 0.55f),
                sunIntensity = 1.9f,
                skyTint = new Color(0.58f, 0.48f, 0.46f),
                groundColor = new Color(0.28f, 0.24f, 0.21f),
                atmosphere = 1.35f,
                exposure = 1.05f,
                ambient = new Color(0.42f, 0.40f, 0.44f),
                ambientIntensity = 1.05f,
                fogColor = new Color(0.72f, 0.6f, 0.5f),
                wind = 0.35f,
                crowdTint = new Color(0.95f, 0.82f, 0.72f),
                skyExposure = 1f,
            },
            TimeOfDay.Floodlit => new Preset
            {
                sunEuler = new Vector3(-16f, 30f, 0f),   // below the horizon: a moon key, not a sun
                sunColor = new Color(0.62f, 0.7f, 0.95f),
                sunIntensity = 0.35f,
                skyTint = new Color(0.10f, 0.13f, 0.2f),
                groundColor = new Color(0.05f, 0.05f, 0.07f),
                atmosphere = 0.5f,
                exposure = 0.5f,
                ambient = new Color(0.16f, 0.18f, 0.24f),
                ambientIntensity = 0.8f,
                fogColor = new Color(0.09f, 0.11f, 0.16f),
                wind = 0.5f,
                crowdTint = new Color(0.38f, 0.42f, 0.58f),
                skyExposure = 0.9f,
            },
            _ => new Preset
            {
                sunEuler = new Vector3(48f, -40f, 0f),
                sunColor = new Color(1f, 0.96f, 0.9f),
                sunIntensity = 2.1f,
                skyTint = new Color(0.44f, 0.56f, 0.78f),
                groundColor = new Color(0.32f, 0.31f, 0.29f),
                atmosphere = 0.85f,
                exposure = 0.95f,
                ambient = new Color(0.5f, 0.54f, 0.6f),
                ambientIntensity = 1.15f,
                fogColor = new Color(0.66f, 0.73f, 0.83f),
                wind = 0.6f,
                crowdTint = Color.white,
                skyExposure = 1f,
            },
        };

        void OnEnable()
        {
            // The tier owns the quality level and the LOD ceiling, and a scene entered directly (the
            // sweep, a dev scene, a build that skips the menu) has nobody else to push it. Play mode
            // only: in the editor the quality level is the user's to change.
            if (Application.isPlaying) RenderTier.Apply();
            RenderTier.Changed += OnTierChanged;
            Apply(force: true);
        }

        void OnDisable() => RenderTier.Changed -= OnTierChanged;

        void OnTierChanged(Tier t) => Apply(force: true);

        void Update()
        {
            if (!liveUpdate) return;
            Apply(force: false);
        }

        void OnValidate() => Apply(force: true);

        /// <summary>
        /// Pushes the preset and the tier into the scene. Cheap enough to call every frame — it early-outs
        /// unless something actually changed — which is what lets the preset be auditioned in the inspector.
        /// </summary>
        public void Apply(bool force)
        {
            bool tierChanged = _appliedTier != RenderTier.Current;
            if (!force && _applied == timeOfDay && !tierChanged) return;
            _applied = timeOfDay;
            _appliedTier = RenderTier.Current;

            Preset p = For(timeOfDay);
            ApplySun(p);
            ApplySky(p);
            ApplyFog(p);
            ApplyVolume();
            ApplyExtras(p);
        }

        void ApplySun(Preset p)
        {
            if (sun == null) return;
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(p.sunEuler);
            sun.color = p.sunColor;
            sun.intensity = p.sunIntensity;
            sun.shadows = LightShadows.Soft;
            // A phone renders one cascade over 55 m, so a wide bias is what keeps shadow acne off the deck;
            // the desktop has four cascades and can afford a tight one that keeps contact shadows.
            bool mobile = RenderTier.IsMobile;
            sun.shadowStrength = mobile ? 0.78f : 0.9f;
            sun.shadowBias = mobile ? 0.1f : 0.03f;
            sun.shadowNormalBias = mobile ? 0.6f : 0.25f;
        }

        void ApplySky(Preset p)
        {
            // A photographed sky when one was baked for this preset (Poly Haven HDRIs, see
            // Assets/Textures/Sky/CREDITS.txt); the procedural sky otherwise, driven by the same numbers.
            Material sky = timeOfDay switch
            {
                TimeOfDay.GoldenHour => skyGoldenHour,
                TimeOfDay.Floodlit => skyNight,
                _ => skyAfternoon,
            };
            if (sky != null)
            {
                sky.SetFloat("_Exposure", p.skyExposure);
                RenderSettings.skybox = sky;
            }
            else if (skyMaterial != null)
            {
                skyMaterial.SetColor("_SkyTint", p.skyTint);
                skyMaterial.SetColor("_GroundColor", p.groundColor);
                skyMaterial.SetFloat("_AtmosphereThickness", p.atmosphere);
                skyMaterial.SetFloat("_Exposure", p.exposure);
                RenderSettings.skybox = skyMaterial;
            }
            // Skybox ambient means the shadow side of an athlete picks up the sky rather than a constant.
            // At night the sky is too dark to be a useful probe, so that preset falls back to a flat colour.
            if (timeOfDay == TimeOfDay.Floodlit)
            {
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = p.ambient;
            }
            else
            {
                RenderSettings.ambientMode = AmbientMode.Skybox;
            }
            RenderSettings.ambientIntensity = p.ambientIntensity;
            RenderSettings.reflectionIntensity = 1f;
            if (sun != null) RenderSettings.sun = sun;
            DynamicGI.UpdateEnvironment();
        }

        void ApplyFog(Preset p)
        {
            RenderSettings.fog = fog;
            if (!fog) return;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = p.fogColor;
            RenderSettings.fogStartDistance = fogStart;
            RenderSettings.fogEndDistance = fogEnd;
        }

        void ApplyVolume()
        {
            if (volume == null) return;
            VolumeProfile wanted = RenderTier.IsMobile ? mobileProfile : pcProfile;
            if (wanted != null && volume.sharedProfile != wanted) volume.sharedProfile = wanted;
            volume.isGlobal = true;
            volume.priority = 0f;
        }
        // ---------------------------------------------------------------- everything the preset touches beyond light

        Renderer[] _windows;
        Material[][] _windowMaterials;
        bool _windowsLit;

        /// <summary>
        /// The wind, the crowd's light, the floodlights, the shimmer and the windows. Globals rather than
        /// per-object settings, so a preset is one place and the things that read it need no wiring.
        /// </summary>
        void ApplyExtras(Preset p)
        {
            Shader.SetGlobalFloat("_PoDecathWind", p.wind);
            Shader.SetGlobalColor("_PoDecathCrowdTint", new Color(p.crowdTint.r, p.crowdTint.g, p.crowdTint.b, 1f));
            bool night = timeOfDay == TimeOfDay.Floodlit;
            if (floodlights != null) floodlights.Set(night);
            if (heatHaze != null) heatHaze.Set(timeOfDay == TimeOfDay.Afternoon);
            ApplyWindows(night);
            ApplyBuildingShadows();
        }

        bool _shadowsApplied;
        bool _shadowsMobile;

        /// <summary>
        /// On the mobile tier the building stops casting shadows. Its single cascade covers 45 m of a
        /// 150 m building whose shadow falls on the lawn, not on the deck, and the shadow pass was
        /// costing a second draw of every part inside that range. The athletes keep theirs.
        /// </summary>
        void ApplyBuildingShadows()
        {
            if (building == null || !Application.isPlaying) return;
            bool mobile = RenderTier.IsMobile;
            if (_shadowsApplied && _shadowsMobile == mobile) return;
            _shadowsApplied = true;
            _shadowsMobile = mobile;
            var mode = mobile ? UnityEngine.Rendering.ShadowCastingMode.Off : UnityEngine.Rendering.ShadowCastingMode.On;
            foreach (MeshRenderer r in building.GetComponentsInChildren<MeshRenderer>(true)) r.shadowCastingMode = mode;
        }

        /// <summary>
        /// The building's windows (every renderer named W_*) swap to the lit material at night and back
        /// by day. A swap rather than an emissive property block, because the glTF shader's emission is
        /// a texture and a factor and the windows have neither; the originals are kept and restored.
        /// </summary>
        void ApplyWindows(bool lit)
        {
            if (building == null || windowLitMaterial == null) return;
            if (_windows == null)
            {
                var found = new System.Collections.Generic.List<Renderer>();
                foreach (MeshRenderer r in building.GetComponentsInChildren<MeshRenderer>(true))
                    if (r.gameObject.name.StartsWith("W_")) found.Add(r);
                _windows = found.ToArray();
                _windowMaterials = new Material[_windows.Length][];
                for (int i = 0; i < _windows.Length; i++) _windowMaterials[i] = _windows[i].sharedMaterials;
                _windowsLit = false;
            }
            if (lit == _windowsLit) return;
            _windowsLit = lit;
            for (int i = 0; i < _windows.Length; i++)
            {
                if (_windows[i] == null) continue;
                if (!lit) { _windows[i].sharedMaterials = _windowMaterials[i]; continue; }
                var mats = new Material[_windowMaterials[i].Length];
                for (int m = 0; m < mats.Length; m++) mats[m] = windowLitMaterial;
                _windows[i].sharedMaterials = mats;
            }
        }
    }
}
