using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PoDecath.Sim;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// Scores the hand-coded gait on its own, away from the race.
    ///
    /// Measuring it inside a live event does not work. The race cycles -- countdown, run, finish,
    /// auto-restart -- and through every countdown it pins each athlete back on the start line with a
    /// ResetPose per frame, so any distance measured across that window is roughly zero no matter how
    /// well the controller walks. The first attempt to test this gait reported three athletes standing
    /// perfectly still, which said nothing about the gait and everything about when the sample was
    /// taken.
    ///
    /// So this silences the events, puts one athlete on the deck facing where it should go, tells
    /// <see cref="HeuristicGait"/> to walk, and watches. It answers the only questions that matter for
    /// tuning: does it stay upright, how far does it get, and how fast.
    ///
    /// Same machinery as the other probes -- one <see cref="EditorApplication.Step"/> per editor update,
    /// state in <see cref="SessionState"/> -- for the same reasons: the Editor will not advance play
    /// mode unfocused, the Pipeline server aborts long main-thread calls, and entering play mode
    /// triggers a domain reload.
    ///
    /// Configure with <c>training/logs/gait_probe_config.json</c>:
    ///   { "scene": "Assets/Scenes/Rooftop.unity", "seconds": 20.0, "speed": 2.0, "label": "v1" }
    /// </summary>
    [InitializeOnLoad]
    public static class GaitProbe
    {
        const string MenuPath = "PoDecath/Probe Heuristic Gait";

        const string KeyPhase = "PoDecath.GaitProbe.Phase";
        const string KeyFrames = "PoDecath.GaitProbe.Frames";
        const string KeyT0 = "PoDecath.GaitProbe.T0";
        const string KeyFell = "PoDecath.GaitProbe.FellAt";
        const string KeyMinUp = "PoDecath.GaitProbe.MinUpright";
        const string KeyMaxSpd = "PoDecath.GaitProbe.MaxSpeed";

        const int WarmupFrames = 90;
        const int SettleFrames = 60;
        const float FallenUpright = 0.4f;

        static readonly string[] Silence =
            { "LapEvent", "RaceEvent", "LongJumpEvent", "HurdleSet" };

        static string LogDir => Path.Combine(Directory.GetParent(Application.dataPath).FullName,
                                             "training", "logs");
        static string ConfigPath => Path.Combine(LogDir, "gait_probe_config.json");
        static string JsonPath => Path.Combine(LogDir, "gait_probe.json");
        static string CsvPath => Path.Combine(LogDir, "gait_probe_trace.csv");

        [Serializable]
        class Config
        {
            public string scene = "Assets/Scenes/Rooftop.unity";
            public float seconds = 20f;
            public float speed = 2.0f;
            /// <summary>
            /// "walk", "stand", "none" (controller off, drives hold their defaults) or "policy"
            /// (measure a trained RL athlete under identical conditions instead).
            ///
            /// "policy" is the control that matters. If the learned policy stays up from the same start
            /// where the coded one does not, the coded controller is at fault; if it falls too, the
            /// probe is setting the body down badly and no amount of gait tuning will show it.
            /// </summary>
            public string driver = "walk";
            public string label = "";
            /// <summary>
            /// Gain overrides, applied to HeuristicGait before the run. NaN leaves the component's own
            /// value alone. Tuning a balance controller means trying signs and magnitudes, and doing that
            /// through a recompile per attempt is the difference between a search and a slog.
            /// </summary>
            public float pitchGain = float.NaN;
            public float pitchRateGain = float.NaN;
            public float ankleRateGain = float.NaN;
            public float stanceWidth = float.NaN;
            public float rollGain = float.NaN;
            public float stepHz = float.NaN;
            public float hipSwing = float.NaN;
            public float pitchSetpoint = float.NaN;
        }

        static Config _cfg;
        static HeuristicRunner _bot;
        static PolicyRunner _policy;
        static AthleteRig _rig;
        static Vector3 _start;
        static float _floorY;
        static StreamWriter _csv;

        static GaitProbe()
        {
            if (Phase != "idle") EditorApplication.update += Tick;
        }

        static string Phase
        {
            get => SessionState.GetString(KeyPhase, "idle");
            set => SessionState.SetString(KeyPhase, value);
        }

        [MenuItem(MenuPath)]
        public static void Start()
        {
            if (Phase != "idle") { Debug.LogWarning($"[GaitProbe] already running ({Phase})."); return; }
            _cfg = LoadConfig();
            Directory.CreateDirectory(LogDir);
            SessionState.SetInt(KeyFrames, 0);
            SessionState.SetFloat(KeyFell, -1f);
            SessionState.SetFloat(KeyMinUp, 1f);
            SessionState.SetFloat(KeyMaxSpd, 0f);
            Write("running", null);

            if (!string.IsNullOrEmpty(_cfg.scene) &&
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().path != _cfg.scene)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) { Write("aborted", "unsaved scene"); return; }
                EditorSceneManager.OpenScene(_cfg.scene, OpenSceneMode.Single);
            }
            Phase = "entering";
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
            Debug.Log($"[GaitProbe] {_cfg.scene}, {_cfg.seconds:0.#}s at {_cfg.speed:0.##} m/s");
        }

        [MenuItem(MenuPath + " (Abort)")]
        public static void Abort() => Finish("aborted", "aborted by hand");

        static void Tick()
        {
            try
            {
                switch (Phase)
                {
                    case "entering":
                        if (EditorApplication.isPlaying && !EditorApplication.isCompiling)
                        {
                            EditorApplication.isPaused = true;
                            SessionState.SetInt(KeyFrames, 0);
                            Phase = "warmup";
                        }
                        return;

                    case "warmup":
                        Step();
                        if (Bump(KeyFrames) >= WarmupFrames)
                        {
                            if (!Acquire()) return;
                            SilenceEvents();
                            MeasureFloor();
                            // Leave it where the spawner put it -- that height is already correct for
                            // this rig -- and only turn it to face down the course.
                            _rig.ResetPose(_rig.BasePosition, Quaternion.identity);
                            SessionState.SetInt(KeyFrames, 0);
                            Phase = "settling";
                        }
                        return;

                    case "settling":
                        Step();
                        if (Bump(KeyFrames) >= SettleFrames)
                        {
                            if (!Acquire()) return;
                            _start = _rig.BasePosition;
                            if (_cfg.driver == "policy")
                            {
                                _policy.enabled = true;
                                if (_policy.commandSource != null)
                                {
                                    _policy.commandSource.enabled = true;
                                    _policy.commandSource.mode = CommandMode.Constant;
                                    _policy.commandSource.constantCommand = new Vector3(1f, 0f, _cfg.speed);
                                }
                            }
                            else
                            {
                                ApplyOverrides();
                                _bot.topSpeed = _cfg.speed;
                                _bot.ResetTo(_start, Vector3.right);
                                if (_cfg.driver == "none" && _bot.gait != null) _bot.gait.enabled = false;
                                else if (_cfg.driver == "walk") _bot.Go();   // otherwise Stand holds it
                            }
                            OpenCsv();
                            SessionState.SetFloat(KeyT0, Time.fixedTime);
                            Phase = "walking";
                        }
                        return;

                    case "walking":
                        {
                            if (!Acquire()) return;
                            Step();
                            float t = Time.fixedTime - SessionState.GetFloat(KeyT0, Time.fixedTime);
                            float up = _rig.UprightDot;
                            Vector3 d = _rig.BasePosition - _start; d.y = 0f;
                            float dist = Vector3.Dot(d, Vector3.right);
                            float spd = Vector3.Dot(_rig.BaseLinearVelocityWorld, Vector3.right);
                            float h = _rig.BasePosition.y - _floorY;

                            if (up < SessionState.GetFloat(KeyMinUp, 1f)) SessionState.SetFloat(KeyMinUp, up);
                            if (spd > SessionState.GetFloat(KeyMaxSpd, 0f)) SessionState.SetFloat(KeyMaxSpd, spd);
                            if (up < FallenUpright && SessionState.GetFloat(KeyFell, -1f) < 0f)
                                SessionState.SetFloat(KeyFell, t);

                            _csv?.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                "{0:0.###},{1:0.####},{2:0.###},{3:0.###},{4:0.###}", t, up, h, dist, spd));

                            if (t >= _cfg.seconds) Finish("completed", null);
                            return;
                        }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[GaitProbe] {e.GetType().Name}: {e.Message}");
                Finish("error", e.Message);
            }
        }

        static void ApplyOverrides()
        {
            HeuristicGait g = _bot.gait;
            if (g == null) return;
            if (!float.IsNaN(_cfg.pitchGain)) g.pitchGain = _cfg.pitchGain;
            if (!float.IsNaN(_cfg.pitchRateGain)) g.pitchRateGain = _cfg.pitchRateGain;
            if (!float.IsNaN(_cfg.ankleRateGain)) g.ankleRateGain = _cfg.ankleRateGain;
            if (!float.IsNaN(_cfg.stanceWidth)) g.stanceWidth = _cfg.stanceWidth;
            if (!float.IsNaN(_cfg.rollGain)) g.rollGain = _cfg.rollGain;
            if (!float.IsNaN(_cfg.stepHz)) g.stepHz = _cfg.stepHz;
            if (!float.IsNaN(_cfg.hipSwing)) g.hipSwing = _cfg.hipSwing;
            if (!float.IsNaN(_cfg.pitchSetpoint)) g.pitchSetpoint = _cfg.pitchSetpoint;
        }

        static void Step() { if (EditorApplication.isPlaying) EditorApplication.Step(); }

        static int Bump(string k) { int v = SessionState.GetInt(k, 0) + 1; SessionState.SetInt(k, v); return v; }

        static bool Acquire()
        {
            _cfg ??= LoadConfig();
            if (_bot != null && _rig != null) return true;
            if (_cfg.driver == "policy")
            {
                _policy = UnityEngine.Object.FindObjectsByType<PolicyRunner>(FindObjectsSortMode.None)
                                            .FirstOrDefault(r => r.rig != null && r.rig.IsBound && r.HasModel);
                if (_policy == null)
                {
                    if (SessionState.GetInt(KeyFrames, 0) > WarmupFrames * 4)
                        Finish("error", "no PolicyRunner with a loaded model in the scene");
                    return false;
                }
                _rig = _policy.rig;
                return true;
            }
            _bot = UnityEngine.Object.FindObjectsByType<HeuristicRunner>(FindObjectsSortMode.None)
                                     .FirstOrDefault(b => b.rig != null && b.rig.IsBound);
            if (_bot == null)
            {
                if (SessionState.GetInt(KeyFrames, 0) > WarmupFrames * 4)
                    Finish("error", "no HeuristicRunner with a bound rig in the scene -- is the bot still kinematic?");
                return false;
            }
            _rig = _bot.rig;
            return true;
        }

        static void SilenceEvents()
        {
            int n = 0;
            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (mb == null || !mb.enabled) continue;
                if (Array.IndexOf(Silence, mb.GetType().Name) < 0) continue;
                mb.StopAllCoroutines();   // disabling stops Update, not coroutines
                mb.enabled = false;
                n++;
            }
            // The policy athletes are not under test -- unless one of them is.
            foreach (var r in UnityEngine.Object.FindObjectsByType<PolicyRunner>(FindObjectsSortMode.None))
                r.enabled = _cfg.driver == "policy" && r == _policy;
            Debug.Log($"[GaitProbe] silenced {n} event component(s) and every PolicyRunner");
        }

        static void MeasureFloor()
        {
            Vector3 p = _rig.BasePosition;
            int creature = LayerMask.NameToLayer("Creature");
            int mask = creature >= 0 ? ~(1 << creature) : ~0;
            _floorY = p.y - 1.0f;
            if (Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 60f, mask,
                                QueryTriggerInteraction.Ignore))
                _floorY = hit.point.y;
        }

        static void OpenCsv()
        {
            CloseCsv();
            Directory.CreateDirectory(LogDir);
            _csv = new StreamWriter(CsvPath, false) { AutoFlush = true };
            _csv.WriteLine("t,upright,height,distance,speed");
        }

        static void CloseCsv() { _csv?.Flush(); _csv?.Dispose(); _csv = null; }

        static void Finish(string status, string error)
        {
            EditorApplication.update -= Tick;
            CloseCsv();
            Write(status, error);
            Phase = "idle";
            _bot = null; _rig = null; _policy = null;
            if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
            Debug.Log($"[GaitProbe] {status} -> {JsonPath}");
        }

        static void Write(string status, string error)
        {
            float fell = SessionState.GetFloat(KeyFell, -1f);
            float dist = 0f, secs = _cfg?.seconds ?? 0f;
            if (_rig != null) { Vector3 d = _rig.BasePosition - _start; d.y = 0f; dist = Vector3.Dot(d, Vector3.right); }
            var sb = new StringBuilder();
            sb.Append('{');
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"status\":\"{0}\",", status);
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"label\":\"{0}\",", _cfg?.label ?? "");
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"mode\":\"{0}\",", _cfg?.driver ?? "walk");
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"asked_speed\":{0:0.##},", _cfg?.speed ?? 0f);
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"seconds\":{0:0.#},", secs);
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"distance_m\":{0:0.##},", dist);
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"mean_speed_mps\":{0:0.##},",
                secs > 0f ? dist / secs : 0f);
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"max_speed_mps\":{0:0.##},", SessionState.GetFloat(KeyMaxSpd, 0f));
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"min_upright\":{0:0.###},", SessionState.GetFloat(KeyMinUp, 1f));
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"fell_at_s\":{0:0.##},", fell);
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"stayed_up\":{0},", fell < 0f ? "true" : "false");
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"error\":{0}",
                error == null ? "null" : "\"" + error.Replace("\"", "'") + "\"");
            sb.Append('}');
            Directory.CreateDirectory(LogDir);
            File.WriteAllText(JsonPath, sb.ToString());
        }

        static Config LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                    return JsonUtility.FromJson<Config>(File.ReadAllText(ConfigPath)) ?? new Config();
            }
            catch (Exception e) { Debug.LogWarning($"[GaitProbe] {ConfigPath}: {e.Message}"); }
            return new Config();
        }
    }
}
