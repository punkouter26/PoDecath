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

        [Header("Look")]
        public TimeOfDay timeOfDay = TimeOfDay.Afternoon;
        [Tooltip("Re-applies every frame in the editor so the preset can be auditioned without playing.")]
        public bool liveUpdate = true;

        [Header("Fog")]
        [Tooltip("Distance haze. The White House sits in a city block that reaches 1.5 km; without it the "
               + "far buildings are as sharp as the deck rail and the roof stops reading as high up.")]
        public bool fog = true;
        public float fogStart = 120f;
        public float fogEnd = 1100f;

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
            },
        };

        void OnEnable()
        {
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
            if (skyMaterial != null)
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
    }
}
