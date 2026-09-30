using UnityEngine;
using UnityEngine.Rendering;
using PoDecath.Sim;

namespace PoDecath.Env
{
    /// <summary>
    /// The afternoon wears on while a long race is being run. Over a 400 m or a 1500 m the look walks
    /// from the scene's preset toward the next one later in the day (afternoon toward golden hour, golden
    /// hour toward the floodlit night), paced by how far the leader has got, and the floodlights come up
    /// mast by mast once the light on the deck has fallen far enough to need them. A restart puts the
    /// scene back on its preset in the same frame.
    ///
    /// Why progress and not the clock: a 1500 m that is going badly and a 1500 m that is going well should
    /// reach the same sky at the line, and a race that stalls should not keep getting darker. The leader's
    /// distance over the race distance is the one number that means "how far through this are we" to the
    /// camera, the crowd (<c>RaceAudio</c> builds on it too) and the viewer at once.
    ///
    /// Short races are left alone. A dash or a single lap is over in half a minute, and a sky that visibly
    /// moves in that time reads as a lighting bug, not as time passing. Only a <see cref="LapEvent"/> of
    /// <see cref="minLaps"/> or more drifts, and how far it gets scales with its length: a 400 m moves part
    /// of the way (<see cref="shortReach"/>), a 1500 m all of it.
    ///
    /// <b>What can drift, and what cannot.</b> The scenes are lit with mixed lighting and a shadowmask
    /// (<c>LightingBakery</c>): the sun's direct light is real time, and everything it bounced is baked.
    /// So this changes only what the renderer evaluates every frame:
    ///
    /// - the sun's colour and intensity, and a fraction of its rotation (below);
    /// - the fog colour, and how far the fog reaches;
    /// - the ambient probe, rescaled per channel toward the later preset's ambient;
    /// - the sky, through a private copy of its material (tinted for an HDRI panorama, re-parameterised for
    ///   the procedural one) so no material asset is ever written to;
    /// - the crowd's light and the wind, both shader globals <see cref="SceneLook"/> already publishes;
    /// - the floodlights, the lit windows and the heat shimmer.
    ///
    /// These do <b>not</b> drift, and cannot without a second bake:
    ///
    /// - <b>Lightmaps</b>: the bounce light, the sky light and the ambient occlusion on the building and the
    ///   deck stay as they were baked for the scene's preset.
    /// - <b>The shadowmask</b>: static shadows onto static surfaces are baked in the direction the sun had
    ///   at bake time. The phone's quality level uses plain Shadowmask, where that is every building shadow
    ///   on the roof, so on the Mobile tier the sun does not turn at all (<see cref="mobileSunTurn"/>); a sun
    ///   that turned there would light one side of a cornice while its shadow still fell the other way. The
    ///   PC level uses Distance Shadowmask, where shadows inside the shadow distance are real time, so the
    ///   sun turns part of the way (<see cref="pcSunTurn"/>) and only the far-off baked shadows lag.
    /// - <b>Light probes</b>: the athletes' ambient comes from the baked probe ring over the deck, not from
    ///   the ambient probe. It is left alone on purpose, so the athletes stay lit like the stone they run on.
    /// - <b>The reflection probe</b> renders once on load; its sky is the preset's.
    /// - <b>The post-processing profiles</b> are assets and are not touched; neither is exposure.
    /// - <b>The sky picture</b> itself: one HDRI panorama cannot morph into another, so it is tinted toward
    ///   the later preset's horizon colour instead.
    ///
    /// Cost: the leader is one pass over the field; everything else is a dozen property writes, made only
    /// when the blend has actually moved. Nothing allocates after the first frame of a drift (which caches
    /// the floodlights' lights and copies the sky material once per scene).
    ///
    /// Presentation only: it reads the race and never writes to it (AGENTS.md, "The broadcast layer reads
    /// the physics").
    /// </summary>
    [DefaultExecutionOrder(-40)]   // after SceneLook (-50), so a preset re-applied this frame is drifted again this frame
    public class DayDrift : MonoBehaviour
    {
        [Header("Wiring")]
        [Tooltip("The scene's look. The drift starts from its preset, reads the next preset's numbers from it, "
               + "and hands the scene back to it on a restart.")]
        public SceneLook look;
        [Tooltip("The event whose progress paces the drift. Only a LapEvent of minLaps or more drifts at all.")]
        public RaceEvent race;

        [Header("Which races")]
        [Tooltip("Fewest laps for the sky to move. 4 is the 400 m: anything shorter is over too quickly for a "
               + "moving sun to read as time passing rather than as a lighting fault.")]
        public int minLaps = 4;
        [Tooltip("Laps at which the drift reaches the next preset in full by the finish. 15 is the 1500 m.")]
        public int fullLaps = 15;
        [Tooltip("How far toward the next preset a race of exactly minLaps gets by the line, 0..1. Longer "
               + "races scale up from here to 1 at fullLaps.")]
        [Range(0f, 1f)] public float shortReach = 0.6f;
        [Tooltip("Blend against race progress (leader distance / race distance). The default eases in, so "
               + "the first laps hold the preset and the change gathers pace toward the bell.")]
        public AnimationCurve pace = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        [Tooltip("Fastest the blend may move, per second. Progress is smooth already; this only stops a "
               + "late-registering leader or a tier switch from snapping the sky.")]
        public float maxRate = 0.2f;

        [Header("Sun")]
        [Tooltip("Fraction of the way the sun's rotation follows the blend on the Mobile tier. 0: that "
               + "quality level uses plain Shadowmask, so every building shadow is baked and cannot turn with it.")]
        [Range(0f, 1f)] public float mobileSunTurn = 0f;
        [Tooltip("Fraction of the way the sun's rotation follows the blend on the PC tier. Distance "
               + "Shadowmask keeps shadows inside the shadow distance real time, but the baked ones beyond "
               + "it stay put, so the sun turns only part of the way.")]
        [Range(0f, 1f)] public float pcSunTurn = 0.35f;

        [Header("Fog")]
        [Tooltip("How much nearer the fog end comes by the later preset, as a fraction of SceneLook.fogEnd. "
               + "Low sun means a longer path through the haze.")]
        [Range(0f, 0.9f)] public float fogThicken = 0.25f;

        [Header("Floodlights")]
        [Tooltip("Light on the deck (sun intensity x sun luminance x sin elevation) below which the first "
               + "mast comes on. The afternoon preset is about 1.5, golden hour about 0.37, night 0.")]
        public float floodsOnBelow = 0.7f;
        [Tooltip("Light on the deck at which every mast is at full power.")]
        public float floodsFullAt = 0.3f;
        [Tooltip("How much later in the darkening each mast after the first strikes, 0..1 of the ramp. "
               + "Masts on one breaker come on together; a stadium's come on in a sequence you can see.")]
        [Range(0f, 0.25f)] public float mastStagger = 0.15f;
        [Tooltip("Blend past which the afternoon's heat shimmer is switched off.")]
        [Range(0f, 1f)] public float hazeOffAt = 0.5f;

        /// <summary>How far the scene has moved toward the next preset right now, 0..1. 0 when not drifting.</summary>
        public float Blend => _blend;

        /// <summary>The preset the drift is heading for, meaningful while <see cref="Blend"/> is above zero.</summary>
        public SceneLook.TimeOfDay Toward => _to;

        static readonly int WindId = Shader.PropertyToID("_PoDecathWind");
        static readonly int CrowdTintId = Shader.PropertyToID("_PoDecathCrowdTint");
        static readonly int SkyTintId = Shader.PropertyToID("_SkyTint");
        static readonly int GroundColorId = Shader.PropertyToID("_GroundColor");
        static readonly int AtmosphereId = Shader.PropertyToID("_AtmosphereThickness");
        static readonly int ExposureId = Shader.PropertyToID("_Exposure");
        static readonly int TintId = Shader.PropertyToID("_Tint");

        float _blend;
        float _written = -1f;
        int _dirtyFrames;
        bool _active;

        SceneLook.TimeOfDay _from, _to;
        SceneLook.Preset _a, _b;

        SphericalHarmonicsL2 _baseProbe;
        Vector3 _probeGain;
        Vector3 _skyGain;

        Material _skySource, _skyCopy;
        bool _skyProcedural, _skyPanoramic;
        Color _skyBaseTint;

        Light[] _floods;
        bool _windowsLit, _hazeOn;

        void OnEnable()
        {
            if (race != null) race.RaceStarted += OnRaceStarted;
            RenderTier.Changed += OnTierChanged;
        }

        void OnDisable()
        {
            if (race != null) race.RaceStarted -= OnRaceStarted;
            RenderTier.Changed -= OnTierChanged;
            Restore();
        }

        void OnDestroy()
        {
            if (_skyCopy != null) Destroy(_skyCopy);
        }

        void OnRaceStarted() => Restore();

        // SceneLook re-applies its preset on a tier change; the drift has to be laid back over it. Two
        // frames, because the change can land after this component's Update in the frame it happens.
        void OnTierChanged(Tier t) => _dirtyFrames = 2;

        void Update()
        {
            if (look == null || race == null) return;

            // The look was changed under a drift (a preset picked in the inspector mid-race): start again
            // from the new one rather than blending from numbers that are no longer on screen.
            if (_active && look.timeOfDay != _from) Restore();

            float target = Target();
            _blend = Mathf.MoveTowards(_blend, target, Mathf.Max(0.01f, maxRate) * Time.deltaTime);

            if (_blend <= 0f)
            {
                if (_active) Restore();
                return;
            }
            if (!_active && !Begin()) { _blend = 0f; return; }

            if (_dirtyFrames > 0) _dirtyFrames--;
            else if (Mathf.Abs(_blend - _written) < 0.001f) return;
            _written = _blend;
            Write(_blend);
        }

        /// <summary>
        /// Where the blend should be for the race as it stands: nothing for a short race or before the gun,
        /// otherwise the pace curve on leader progress, scaled by how long the race is. It holds through the
        /// finish and the results; only a restart takes it back.
        /// </summary>
        float Target()
        {
            if (!(race is LapEvent lap) || lap.laps < minLaps || race.raceDistance <= 0f) return 0f;
            if (race.Current != RaceEvent.Phase.Running && race.Current != RaceEvent.Phase.Finished) return 0f;
            if (!Later(look.timeOfDay, out _)) return 0f;

            float lead = 0f;
            foreach (RaceEvent.Athlete a in race.Athletes) if (a.distance > lead) lead = a.distance;
            float progress = Mathf.Clamp01(lead / race.raceDistance);

            float span = Mathf.Max(1, fullLaps - minLaps);
            float reach = Mathf.Lerp(shortReach, 1f, Mathf.Clamp01((lap.laps - minLaps) / span));
            float shaped = pace != null && pace.length > 0 ? pace.Evaluate(progress) : progress;
            return Mathf.Clamp01(shaped) * reach;
        }

        /// <summary>The next preset later in the day, if there is one. The night is the end of the line.</summary>
        static bool Later(SceneLook.TimeOfDay t, out SceneLook.TimeOfDay next)
        {
            switch (t)
            {
                case SceneLook.TimeOfDay.Afternoon: next = SceneLook.TimeOfDay.GoldenHour; return true;
                case SceneLook.TimeOfDay.GoldenHour: next = SceneLook.TimeOfDay.Floodlit; return true;
                default: next = t; return false;
            }
        }

        /// <summary>
        /// The first frame of a drift: note what is on screen now, so every later frame is a blend from
        /// here and a restart can put it back. The only allocations the component makes happen here.
        /// </summary>
        bool Begin()
        {
            _from = look.timeOfDay;
            if (!Later(_from, out _to)) return false;
            _a = SceneLook.PresetFor(_from);
            _b = SceneLook.PresetFor(_to);

            // Ambient: the probe SceneLook generated from the sky, rescaled per channel toward the later
            // preset's ambient. Both presets carry an ambient colour even where only the night uses it flat,
            // so the ratio of the two is the colour shift the later sky would have given the probe.
            _baseProbe = RenderSettings.ambientProbe;
            _probeGain = Ratio(_b.ambient * _b.ambientIntensity, _a.ambient * _a.ambientIntensity);

            // The sky: a private copy, so the drift never writes to a material asset (in the editor that
            // would save the tint into the project). Made once per scene and reused on every attempt.
            Material sky = RenderSettings.skybox;
            if (sky != null && sky != _skyCopy)
            {
                if (_skyCopy == null || _skySource != sky)
                {
                    if (_skyCopy != null) Destroy(_skyCopy);
                    _skyCopy = new Material(sky) { name = sky.name + " (drift)" };
                    _skySource = sky;
                }
                else _skyCopy.CopyPropertiesFromMaterial(sky);
                _skyProcedural = _skyCopy.HasProperty(SkyTintId);
                _skyPanoramic = !_skyProcedural && _skyCopy.HasProperty(TintId);
                _skyBaseTint = _skyPanoramic ? _skyCopy.GetColor(TintId) : Color.white;
            }
            // The fog colour is matched to each preset's horizon, so its shift is the panorama's tint shift.
            _skyGain = Ratio(_b.fogColor, _a.fogColor);

            if (_floods == null && look.floodlights != null)
                _floods = look.floodlights.GetComponentsInChildren<Light>(true);

            _windowsLit = _from == SceneLook.TimeOfDay.Floodlit;
            _hazeOn = _from == SceneLook.TimeOfDay.Afternoon;
            _written = -1f;
            _active = true;
            return true;
        }

        /// <summary>Everything the drift moves, at blend <paramref name="k"/>.</summary>
        void Write(float k)
        {
            // Sun. Colour and intensity follow in full; the rotation only as far as the baked shadows allow.
            Light sun = look.sun;
            if (sun != null)
            {
                float turn = RenderTier.IsMobile ? mobileSunTurn : pcSunTurn;
                sun.transform.rotation = Quaternion.Slerp(Quaternion.Euler(_a.sunEuler), Quaternion.Euler(_b.sunEuler), k * turn);
                sun.color = Color.Lerp(_a.sunColor, _b.sunColor, k);
                sun.intensity = Mathf.Lerp(_a.sunIntensity, _b.sunIntensity, k);
            }

            // Ambient probe, per channel.
            SphericalHarmonicsL2 sh = _baseProbe;
            for (int c = 0; c < 3; c++)
            {
                float g = Mathf.Lerp(1f, _probeGain[c], k);
                for (int i = 0; i < 9; i++) sh[c, i] = _baseProbe[c, i] * g;
            }
            RenderSettings.ambientProbe = sh;

            // Fog.
            if (RenderSettings.fog)
            {
                RenderSettings.fogColor = Color.Lerp(_a.fogColor, _b.fogColor, k);
                RenderSettings.fogEndDistance = look.fogEnd * Mathf.Lerp(1f, 1f - fogThicken, k);
            }

            // Sky, on the private copy.
            if (_skyCopy != null)
            {
                if (_skyProcedural)
                {
                    _skyCopy.SetColor(SkyTintId, Color.Lerp(_a.skyTint, _b.skyTint, k));
                    _skyCopy.SetColor(GroundColorId, Color.Lerp(_a.groundColor, _b.groundColor, k));
                    _skyCopy.SetFloat(AtmosphereId, Mathf.Lerp(_a.atmosphere, _b.atmosphere, k));
                    _skyCopy.SetFloat(ExposureId, Mathf.Lerp(_a.exposure, _b.exposure, k));
                }
                else if (_skyPanoramic)
                {
                    Color t = _skyBaseTint;
                    t.r *= Mathf.Lerp(1f, _skyGain.x, k);
                    t.g *= Mathf.Lerp(1f, _skyGain.y, k);
                    t.b *= Mathf.Lerp(1f, _skyGain.z, k);
                    _skyCopy.SetColor(TintId, t);
                    if (_skyCopy.HasProperty(ExposureId)) _skyCopy.SetFloat(ExposureId, Mathf.Lerp(_a.skyExposure, _b.skyExposure, k));
                }
                if (RenderSettings.skybox != _skyCopy) RenderSettings.skybox = _skyCopy;
            }

            // The crowd's light and the wind, the same globals SceneLook publishes.
            Color crowd = Color.Lerp(_a.crowdTint, _b.crowdTint, k);
            crowd.a = 1f;
            Shader.SetGlobalColor(CrowdTintId, crowd);
            Shader.SetGlobalFloat(WindId, Mathf.Lerp(_a.wind, _b.wind, k));

            Floods(k);

            bool haze = _from == SceneLook.TimeOfDay.Afternoon && k < hazeOffAt;
            if (haze != _hazeOn)
            {
                _hazeOn = haze;
                if (look.heatHaze != null) look.heatHaze.Set(haze);
            }
        }

        /// <summary>
        /// Light on the deck for the blended preset, as the floodlights see it: intensity x luminance x the
        /// sine of the elevation. Measured on the full blend, not on the partly-turned sun, because it is the
        /// time of day the lamps answer to, not whatever the shadowmask lets the sun show.
        /// </summary>
        float DeckLight(float k)
        {
            float elevation = Mathf.Lerp(_a.sunEuler.x, _b.sunEuler.x, k) * Mathf.Deg2Rad;
            Color c = Color.Lerp(_a.sunColor, _b.sunColor, k);
            float lum = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            return Mathf.Lerp(_a.sunIntensity, _b.sunIntensity, k) * lum * Mathf.Max(0f, Mathf.Sin(elevation));
        }

        /// <summary>
        /// The masts come on one after another as the light falls through <see cref="floodsOnBelow"/>, each
        /// ramping to full rather than switching, and the windows light with the first of them.
        /// </summary>
        void Floods(float k)
        {
            float dark = Mathf.InverseLerp(floodsOnBelow, floodsFullAt, DeckLight(k));
            bool any = false;
            if (_floods != null && _floods.Length > 0 && look.floodlights != null)
            {
                int n = _floods.Length;
                float stagger = n > 1 ? Mathf.Min(mastStagger, 0.8f / (n - 1)) : 0f;
                float width = Mathf.Max(0.05f, 1f - (n - 1) * stagger);
                float full = look.floodlights.intensity;
                for (int i = 0; i < n; i++)
                {
                    Light l = _floods[i];
                    if (l == null) continue;
                    float level = Mathf.Clamp01((dark - i * stagger) / width);
                    bool on = level > 0.001f || look.floodlights.On;
                    l.enabled = on;
                    l.intensity = look.floodlights.On ? full : full * level * level;   // lamps strike dim and swell
                    any |= level > 0.001f;
                }
            }
            else any = dark > 0f;

            bool lit = any || _from == SceneLook.TimeOfDay.Floodlit;
            if (lit != _windowsLit)
            {
                _windowsLit = lit;
                look.SetWindowsLit(lit);
            }
        }

        /// <summary>
        /// Back to the preset, at once. The floodlights' own intensity is restored first because SceneLook
        /// only switches them; then SceneLook re-applies its preset in full, which also puts its own sky
        /// material back, regenerates the ambient probe and resets the fog, the windows and the shimmer.
        /// </summary>
        void Restore()
        {
            _blend = 0f;
            _written = -1f;
            if (!_active) return;
            _active = false;

            if (_floods != null && look != null && look.floodlights != null)
            {
                float full = look.floodlights.intensity;
                foreach (Light l in _floods) if (l != null) l.intensity = full;
            }
            if (look != null) look.Apply(force: true);
            else if (_skySource != null && RenderSettings.skybox == _skyCopy) RenderSettings.skybox = _skySource;
        }

        /// <summary>Per-channel ratio of two colours, clamped so a near-black source cannot blow up.</summary>
        static Vector3 Ratio(Color to, Color from) => new Vector3(
            Mathf.Clamp(to.r / Mathf.Max(0.02f, from.r), 0f, 2f),
            Mathf.Clamp(to.g / Mathf.Max(0.02f, from.g), 0f, 2f),
            Mathf.Clamp(to.b / Mathf.Max(0.02f, from.b), 0f, 2f));
    }
}
