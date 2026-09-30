// Adaptive Performance is an engine module from Unity 6 (UnityEngine.AdaptivePerformanceModule, pulled in by
// com.unity.adaptiveperformance and its Google Android provider in Packages/manifest.json). It only ever
// reports anything on a device with a provider — Android (Google), iOS, or the editor's simulator — so
// every other player compiles the governor down to a component that does nothing.
#if UNITY_6000_0_OR_NEWER && (UNITY_ANDROID || UNITY_IOS || UNITY_EDITOR)
#define PODECATH_THERMAL
#endif

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.Universal;
#if PODECATH_THERMAL
using UnityEngine.AdaptivePerformance;
#endif

namespace PoDecath.Env
{
    /// <summary>
    /// Keeps a hot phone at a steady frame rate by rendering fewer pixels, instead of letting the phone
    /// halve its own clocks and drop the frame rate for it.
    ///
    /// A phone that has been rendering a sixteen-athlete race for ten minutes gets warm, and a warm phone
    /// throttles: the SoC lowers its clocks, and a game that fitted in 16.6 ms at the start does not
    /// fit any more. The phone does that whether the game likes it or not. What the game gets to choose is
    /// what gives first, and the cheapest thing to give on this project is resolution — the mobile URP
    /// asset already renders at 0.8 of the screen, and 0.7 of a 1080x2400 portrait screen is still more
    /// pixels than the athletes have detail. So when Adaptive Performance says throttling is imminent,
    /// the render scale steps down; when it says throttling has started, it steps down faster; and when
    /// the device has been cool for a sustained period, it steps back up, one step at a time.
    ///
    /// The hysteresis is the point. Down is quick (a few seconds between steps) and up is slow (tens of
    /// seconds of no warning, and a temperature reading below <see cref="recoverBelowTemperature"/>),
    /// because a governor that steps up the moment the warning clears puts the load straight back on and
    /// oscillates — and a resolution that changes every few seconds is far more visible than one that
    /// is simply a little lower.
    ///
    /// What it changes: <see cref="UniversalRenderPipelineAsset.renderScale"/> on the asset in force,
    /// which is the one <see cref="RenderTier.Apply"/> selected through the quality level. Nothing else.
    /// **It never touches physics, the fixed step, the policies or their decimation** — the athletes are
    /// the one thing on screen that must behave identically on a cold phone and a hot one; see AGENTS.md,
    /// "The broadcast layer reads the physics; it must never write it". The original scale is put back
    /// when this is disabled, which matters in the editor: a render scale changed on the asset in play mode
    /// would otherwise still be on it after play mode ends.
    ///
    /// Adaptive Performance must be initialised for any of this to happen (Project Settings > Adaptive
    /// Performance, provider ticked for Android, "Initialize Adaptive Performance on Startup"). Without a
    /// provider <c>Holder.Instance</c> is null and this sits and reports "no provider"; on PC it compiles
    /// to nothing at all.
    ///
    /// Every change is logged, kept in <see cref="ChangesSince"/> for the per-race JSON
    /// (<c>RaceLog</c> writes it as <c>"thermal"</c>), and summarised in <see cref="Summary"/> for the
    /// FRAME page of the diagnostics sheet.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public class ThermalGovernor : MonoBehaviour
    {
        [Header("Render scale")]
        [Tooltip("Render scale is never taken below this. 0.6 of a 1080-wide portrait screen is 648 "
               + "pixels across, which is about where the athletes' limbs start to shimmer.")]
        [Range(0.3f, 1f)] public float floorScale = 0.6f;
        [Tooltip("How much each step takes off the render scale. From the mobile asset's 0.8, four steps "
               + "reach the 0.6 floor.")]
        [Range(0.01f, 0.25f)] public float step = 0.05f;

        [Header("Stepping down")]
        [Tooltip("Seconds between steps down while the device reports throttling (clocks already lowered).")]
        public float throttlingStepSeconds = 3f;
        [Tooltip("Seconds between steps down while the device reports throttling imminent. Slower: the "
               + "phone is warning, not yet acting, and one step often buys enough.")]
        public float imminentStepSeconds = 8f;

        [Header("Stepping back up")]
        [Tooltip("Seconds of no thermal warning before one step back up. Each further step needs the same "
               + "again, so a phone that has cooled climbs back over minutes rather than seconds.")]
        public float coolSeconds = 30f;
        [Tooltip("Adaptive Performance's normalised temperature (0 normal, 1 at the throttling limit) must "
               + "also be below this before stepping up. Ignored where the provider does not report it.")]
        [Range(0f, 1f)] public float recoverBelowTemperature = 0.6f;

        [Tooltip("Seconds between reads of the thermal state. The warning level changes on the order of "
               + "seconds; reading it every frame buys nothing.")]
        public float pollSeconds = 0.5f;

        /// <summary>The device's thermal warning, in the project's own words so nothing outside needs the package.</summary>
        public enum Heat
        {
            /// <summary>No provider: PC, the editor without the simulator, or Adaptive Performance not initialised.</summary>
            NoProvider,
            /// <summary>Normal operating temperature.</summary>
            Normal,
            /// <summary>The device expects to throttle soon.</summary>
            ThrottlingImminent,
            /// <summary>The device is throttling now.</summary>
            Throttling,
        }

        /// <summary>One render-scale change, for the race log.</summary>
        public struct Change
        {
            /// <summary>Monotonic number of this change since the app started.</summary>
            public int index;
            /// <summary><see cref="Time.unscaledTime"/> when it happened.</summary>
            public float time;
            public Heat heat;
            /// <summary>Normalised temperature 0..1, or -1 where the provider does not report one.</summary>
            public float temperature;
            public float fromScale, toScale;
        }

        // ---------------------------------------------------------------- the public summary

        /// <summary>The last thermal warning read.</summary>
        public static Heat Level { get; private set; } = Heat.NoProvider;

        /// <summary>The last normalised temperature read, 0..1, or -1 when unknown.</summary>
        public static float Temperature { get; private set; } = -1f;

        /// <summary>The render scale in force, or -1 before a governor has run or when there is no URP asset.</summary>
        public static float RenderScale { get; private set; } = -1f;

        /// <summary>The render scale the tier's asset had before any step, which is what cooling returns to.</summary>
        public static float BaselineScale { get; private set; } = -1f;

        /// <summary>Steps currently taken off the baseline.</summary>
        public static int Steps { get; private set; }

        /// <summary>Every change ever made, counted; <see cref="ChangesSince"/> takes this as its mark.</summary>
        public static int ChangeCount { get; private set; }

        /// <summary>One line for the diagnostics sheet: the warning, the scale, the steps.</summary>
        public static string Summary
        {
            get
            {
                if (Level == Heat.NoProvider)
                    return RenderScale > 0f ? $"no provider   {RenderScale:F2}x" : "no provider";
                string temp = Temperature >= 0f ? $"  {Temperature * 100f:F0}%" : "";
                return $"{Level}{temp}   {RenderScale:F2}x   {Steps} step{(Steps == 1 ? "" : "s")}";
            }
        }

        /// <summary>The overlay's grade for <see cref="Summary"/>: good, warn or bad, null when there is nothing to grade.</summary>
        public static string SummaryGrade => Level switch
        {
            Heat.Throttling => "bad",
            Heat.ThrottlingImminent => "warn",
            Heat.Normal => Steps > 0 ? "warn" : "good",
            _ => null,
        };

        const int Kept = 64;
        static readonly List<Change> _changes = new List<Change>();

        /// <summary>The changes numbered <paramref name="mark"/> and after, oldest first. Only the last 64 are kept.</summary>
        public static List<Change> ChangesSince(int mark)
        {
            var list = new List<Change>();
            foreach (Change c in _changes) if (c.index >= mark) list.Add(c);
            return list;
        }

        /// <summary>
        /// The <c>"thermal"</c> object for the race log: the state at the moment of writing, and every change
        /// since <paramref name="mark"/>, timed in seconds from <paramref name="origin"/> (the gun).
        /// </summary>
        public static string Json(int mark, float origin)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("{");
            sb.AppendFormat(ci, "\"level\":\"{0}\",", Level);
            sb.AppendFormat(ci, "\"temperature\":{0:F2},", Temperature);
            sb.AppendFormat(ci, "\"render_scale\":{0:F2},", RenderScale);
            sb.AppendFormat(ci, "\"baseline_scale\":{0:F2},", BaselineScale);
            sb.AppendFormat(ci, "\"steps\":{0},", Steps);
            sb.Append("\"changes\":[");
            bool first = true;
            foreach (Change c in _changes)
            {
                if (c.index < mark) continue;
                if (!first) sb.Append(",");
                first = false;
                sb.AppendFormat(ci, "{{\"t\":{0:F1},\"level\":\"{1}\",\"temperature\":{2:F2},\"from\":{3:F2},\"to\":{4:F2}}}",
                                c.time - origin, c.heat, c.temperature, c.fromScale, c.toScale);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- state

        UniversalRenderPipelineAsset _asset;   // the asset this governor has changed, so it can put it back
        float _baseline = -1f;
        float _nextPoll;
        float _lastStep = -999f;
        float _coolFor;

        void OnEnable()
        {
            RenderTier.Changed += OnTierChanged;
            Capture();
        }

        void OnDisable()
        {
            RenderTier.Changed -= OnTierChanged;
            Restore();
        }

        void OnApplicationQuit() => Restore();

        /// <summary>
        /// A tier switch swaps the URP asset under this. The old asset gets its own scale back, the new one's
        /// scale becomes the baseline, and the steps already taken carry over: the device is no cooler for
        /// having changed quality level.
        /// </summary>
        void OnTierChanged(Tier tier)
        {
            Restore();
            Capture();
            Apply(Level, Temperature, record: false);
        }

        void Capture()
        {
            _asset = UniversalRenderPipeline.asset;
            _baseline = _asset != null ? _asset.renderScale : -1f;
            BaselineScale = _baseline;
            RenderScale = _baseline;
        }

        void Restore()
        {
            if (_asset != null && _baseline > 0f)
            {
                _asset.renderScale = _baseline;
                RenderScale = _baseline;
            }
            _asset = null;
        }

        void Update()
        {
            if (Time.unscaledTime < _nextPoll) return;
            float dt = Time.unscaledTime - (_nextPoll - pollSeconds);
            _nextPoll = Time.unscaledTime + Mathf.Max(0.1f, pollSeconds);

            if (!Read(out Heat heat, out float temperature))
            {
                Level = Heat.NoProvider;
                Temperature = -1f;
                return;
            }
            Level = heat;
            Temperature = temperature;
            if (_asset == null || _baseline <= 0f) return;

            float since = Time.unscaledTime - _lastStep;
            switch (heat)
            {
                case Heat.Throttling:
                    _coolFor = 0f;
                    if (since >= throttlingStepSeconds) StepBy(+1, heat, temperature);
                    break;
                case Heat.ThrottlingImminent:
                    _coolFor = 0f;
                    if (since >= imminentStepSeconds) StepBy(+1, heat, temperature);
                    break;
                default:
                    _coolFor += Mathf.Clamp(dt, 0f, 2f);
                    bool cool = temperature < 0f || temperature < recoverBelowTemperature;
                    if (Steps > 0 && cool && _coolFor >= coolSeconds)
                    {
                        StepBy(-1, heat, temperature);
                        _coolFor = 0f;   // every step back up earns its own cool period
                    }
                    break;
            }
        }

        /// <summary>
        /// Reads the provider. False when there is none, which is every PC and every phone on which
        /// Adaptive Performance has not been initialised.
        /// </summary>
        static bool Read(out Heat heat, out float temperature)
        {
            heat = Heat.NoProvider;
            temperature = -1f;
#if PODECATH_THERMAL
            IAdaptivePerformance ap = Holder.Instance;
            if (ap == null || !ap.Active || ap.ThermalStatus == null) return false;
            ThermalMetrics m = ap.ThermalStatus.ThermalMetrics;
            heat = m.WarningLevel switch
            {
                WarningLevel.Throttling => Heat.Throttling,
                WarningLevel.ThrottlingImminent => Heat.ThrottlingImminent,
                _ => Heat.Normal,
            };
            temperature = m.TemperatureLevel;
            if (float.IsNaN(temperature)) temperature = -1f;
            return true;
#else
            return false;
#endif
        }

        /// <summary>+1 is one step down in resolution, -1 one step back up.</summary>
        void StepBy(int delta, Heat heat, float temperature)
        {
            int max = MaxSteps();
            int next = Mathf.Clamp(Steps + delta, 0, max);
            if (next == Steps) return;
            Steps = next;
            _lastStep = Time.unscaledTime;
            Apply(heat, temperature, record: true);
        }

        int MaxSteps() =>
            _baseline > floorScale && step > 0f ? Mathf.CeilToInt((_baseline - floorScale) / step - 1e-4f) : 0;

        void Apply(Heat heat, float temperature, bool record)
        {
            if (_asset == null || _baseline <= 0f) return;
            Steps = Mathf.Clamp(Steps, 0, MaxSteps());
            float from = _asset.renderScale;
            float to = Steps == 0 ? _baseline : Mathf.Max(floorScale, _baseline - Steps * step);
            _asset.renderScale = to;
            RenderScale = to;
            if (!record || Mathf.Approximately(from, to)) return;

            _changes.Add(new Change
            {
                index = ChangeCount++,
                time = Time.unscaledTime,
                heat = heat,
                temperature = temperature,
                fromScale = from,
                toScale = to,
            });
            if (_changes.Count > Kept) _changes.RemoveAt(0);
            Debug.Log($"[Thermal] {heat}{(temperature >= 0f ? $" ({temperature * 100f:F0}%)" : "")}: "
                    + $"render scale {from:F2} -> {to:F2} ({Steps} step{(Steps == 1 ? "" : "s")} below {_baseline:F2}).");
        }
    }
}
