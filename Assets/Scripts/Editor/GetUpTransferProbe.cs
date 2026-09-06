using System;
using System.Collections.Generic;
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
    /// Measures whether a get-up policy actually transfers from MuJoCo to PhysX.
    ///
    /// The project's headline open item is that <c>athlete_getup.onnx</c> stands up in essentially every
    /// MuJoCo episode and, in Unity, lifts uprightness from about 0.03 to about 0.35 and falls back. That
    /// claim was made from watching play mode. Watching is not a measurement: it cannot say whether a
    /// change made things better, and it cannot be re-run against a new checkpoint. This can.
    ///
    /// The probe drops one athlete flat on its back, hands it the recovery policy with a zero command --
    /// the same thing <see cref="RecoveryController"/> does mid-race -- and records uprightness and pelvis
    /// height every physics step for a fixed window. It writes a CSV of the whole trace plus a JSON
    /// summary, so two checkpoints can be compared on numbers rather than impressions.
    ///
    /// Why it is driven from <see cref="EditorApplication.update"/> rather than a loop
    /// ------------------------------------------------------------------------------
    /// Two constraints in this project rule out the obvious `for` loop. The Editor does not advance
    /// play-mode frames while unfocused with no Game view, so the probe has to call
    /// <see cref="EditorApplication.Step"/> itself; and the Pipeline server aborts any main-thread
    /// operation that runs past five seconds, so it cannot block while doing it. Ticking one step per
    /// editor update satisfies both: the run is deterministic, it survives an unfocused editor, and no
    /// single call holds the main thread.
    ///
    /// Entering play mode triggers a domain reload, which wipes statics and silently unsubscribes the
    /// update hook. Every scrap of state therefore lives in <see cref="SessionState"/> and the samples go
    /// to disk as they are taken, so a reload costs nothing and the hook re-attaches itself on load.
    ///
    /// Configure with <c>training/logs/getup_probe_config.json</c> (all keys optional):
    ///   { "scene": "Assets/Scenes/RooftopLap.unity", "model": "Assets/Policies/athlete_getup.onnx",
    ///     "seconds": 8.0, "label": "baseline" }
    /// </summary>
    [InitializeOnLoad]
    public static class GetUpTransferProbe
    {
        const string MenuPath = "PoDecath/Probe Get-Up Transfer";

        // SessionState survives the domain reload that entering play mode causes. EditorPrefs would too,
        // but it also survives quitting the editor, and a probe left "running" across a restart would
        // re-arm itself against whatever scene happened to be open next.
        const string KeyPhase = "PoDecath.GetUpProbe.Phase";
        const string KeyFrames = "PoDecath.GetUpProbe.Frames";
        const string KeySettle = "PoDecath.GetUpProbe.Settle";
        const string KeyStartUp = "PoDecath.GetUpProbe.StartUpright";
        const string KeyPeak = "PoDecath.GetUpProbe.Peak";
        const string KeyHold = "PoDecath.GetUpProbe.HoldSteps";
        const string KeyBestHold = "PoDecath.GetUpProbe.BestHold";
        const string KeyT0 = "PoDecath.GetUpProbe.T0";
        const string KeyLastT = "PoDecath.GetUpProbe.LastT";

        static readonly string LogDir = Path.Combine(ProjectRoot, "training", "logs");
        static string ConfigPath => Path.Combine(LogDir, "getup_probe_config.json");
        static string CsvPath => Path.Combine(LogDir, "getup_transfer_trace.csv");
        static string JsonPath => Path.Combine(LogDir, "getup_transfer.json");

        static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

        // Tuning that is not worth a config key.
        const int WarmupFrames = 90;      // let AthleteSpawner build the rig and PolicyRunner load its model
        const int SettleFrames = 100;     // let the body come to rest on the deck before the policy starts
        const float StandUpright = 0.82f; // matches RecoveryController.upUprightDot
        const float StandHeightFrac = 0.8f;

        class Config
        {
            public string scene = "Assets/Scenes/RooftopLap.unity";
            public string model = "Assets/Policies/athlete_getup.onnx";
            public double seconds = 8.0;
            public string label = "";
        }

        static Config _cfg;
        static PolicyRunner _runner;
        static CreatureRig _rig;
        static float _standHeight = 0.95f;   // pelvis height above the local floor when standing
        static float _floorY;                // world Y of the deck under the athlete
        static StreamWriter _csv;

        // Anything that would reset, respawn or re-police the body while the probe is running. The lap
        // scene is hands-off by design: it puts a fallen athlete back on its feet after three seconds,
        // which silently turned the first run of this probe into a measurement of a standing athlete.
        static readonly string[] InterferingComponents =
            { "LapEvent", "DashEvent", "LongJumpEvent", "EpisodeManager", "RecoveryController" };

        static GetUpTransferProbe()
        {
            // Re-attach after a domain reload if a run is still in flight.
            if (Phase != "idle")
                EditorApplication.update += Tick;
        }

        static string Phase
        {
            get => SessionState.GetString(KeyPhase, "idle");
            set => SessionState.SetString(KeyPhase, value);
        }

        [MenuItem(MenuPath)]
        public static void Start()
        {
            if (Phase != "idle")
            {
                Debug.LogWarning($"[GetUpProbe] already running (phase {Phase}). Use '{MenuPath} (Abort)' first.");
                return;
            }

            _cfg = LoadConfig();
            Directory.CreateDirectory(LogDir);

            SessionState.SetInt(KeyFrames, 0);
            SessionState.SetInt(KeySettle, 0);
            SessionState.SetFloat(KeyStartUp, 0f);
            SessionState.SetFloat(KeyPeak, -1f);
            SessionState.SetFloat(KeyHold, 0f);
            SessionState.SetFloat(KeyBestHold, 0f);
            SessionState.SetFloat(KeyT0, 0f);
            SessionState.SetFloat(KeyLastT, 0f);
            WriteStatus("running", null);

            if (!string.IsNullOrEmpty(_cfg.scene) &&
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().path != _cfg.scene)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    Debug.LogError("[GetUpProbe] aborted: the open scene has unsaved changes.");
                    WriteStatus("aborted", "unsaved scene");
                    return;
                }
                EditorSceneManager.OpenScene(_cfg.scene, OpenSceneMode.Single);
            }

            Phase = "entering";
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
            Debug.Log($"[GetUpProbe] started: scene {_cfg.scene}, model {_cfg.model}, {_cfg.seconds:0.#}s");
        }

        [MenuItem(MenuPath + " (Abort)")]
        public static void Abort()
        {
            Finish("aborted", "aborted by hand");
        }

        // ---- the tick -------------------------------------------------------------------------------
        static void Tick()
        {
            try
            {
                switch (Phase)
                {
                    case "entering":
                        // EnterPlaymode is asynchronous; wait for it, then pause so that every subsequent
                        // frame comes from an explicit Step() and the trace is reproducible.
                        if (EditorApplication.isPlaying && !EditorApplication.isCompiling)
                        {
                            EditorApplication.isPaused = true;
                            Phase = "warmup";
                        }
                        return;

                    case "warmup":
                        Step();
                        if (Bump(KeyFrames) >= WarmupFrames)
                        {
                            if (!Acquire()) return;
                            SilenceInterference();
                            MeasureStandingReference();
                            PlaceSupine();
                            SessionState.SetInt(KeySettle, 0);
                            Phase = "settling";
                        }
                        return;

                    case "settling":
                        // Let the body come to rest before the policy is allowed to touch it, so the
                        // starting uprightness is a real resting pose and not a placement artefact.
                        Step();
                        if (Bump(KeySettle) >= SettleFrames)
                        {
                            if (!Acquire()) return;
                            // Refuse to measure a body that is not actually down. A get-up score taken
                            // from a standing start is worse than no score: it reads as a pass.
                            float startUp = _rig.UprightDot;
                            if (startUp > 0.35f)
                            {
                                Finish("error", $"athlete is not supine after settling (upright {startUp:0.###}); " +
                                                "nothing was measured");
                                return;
                            }
                            SessionState.SetFloat(KeyStartUp, startUp);
                            SessionState.SetFloat(KeyT0, Time.fixedTime);
                            SessionState.SetFloat(KeyLastT, Time.fixedTime);
                            _runner.UseRecovery = true;
                            if (_runner.commandSource != null)
                                _runner.commandSource.enabled = false;   // a get-up is trained on a zero command
                            OpenCsv();
                            SessionState.SetInt(KeyFrames, 0);
                            Phase = "probing";
                        }
                        return;

                    case "probing":
                        {
                            if (!Acquire()) return;
                            Step();
                            Bump(KeyFrames);

                            // Simulated time comes from Time.fixedTime. Counting Step() calls would be
                            // wrong: Step() advances one editor frame, and a frame runs however many
                            // FixedUpdates are due -- not necessarily exactly one.
                            float now = Time.fixedTime;
                            float t = now - SessionState.GetFloat(KeyT0, now);
                            float dt = Mathf.Max(0f, now - SessionState.GetFloat(KeyLastT, now));
                            SessionState.SetFloat(KeyLastT, now);

                            float up = _rig.UprightDot;
                            float h = _rig.BasePosition.y - _floorY;   // above the deck, not above the world

                            if (up > SessionState.GetFloat(KeyPeak, -1f))
                                SessionState.SetFloat(KeyPeak, up);

                            bool standing = up >= StandUpright && h >= _standHeight * StandHeightFrac;
                            float hold = standing ? SessionState.GetFloat(KeyHold, 0f) + dt : 0f;
                            SessionState.SetFloat(KeyHold, hold);
                            if (hold > SessionState.GetFloat(KeyBestHold, 0f))
                                SessionState.SetFloat(KeyBestHold, hold);

                            _csv?.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                "{0:0.####},{1:0.#####},{2:0.#####},{3}", t, up, h, standing ? 1 : 0));

                            if (t >= _cfg.seconds)
                                Finish("completed", null);
                            return;
                        }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[GetUpProbe] {e.GetType().Name}: {e.Message}");
                Finish("error", e.Message);
            }
        }

        static void Step()
        {
            // Explicit stepping is what makes this work with the editor unfocused; see the class remarks.
            if (EditorApplication.isPlaying)
                EditorApplication.Step();
        }

        static int Bump(string key)
        {
            int v = SessionState.GetInt(key, 0) + 1;
            SessionState.SetInt(key, v);
            return v;
        }

        /// <summary>Re-resolves the scene references, which a domain reload drops.</summary>
        static bool Acquire()
        {
            _cfg ??= LoadConfig();
            if (_runner != null && _rig != null) return true;

            var runners = UnityEngine.Object.FindObjectsByType<PolicyRunner>(FindObjectsSortMode.None);
            _runner = runners.FirstOrDefault(r => r.rig != null && r.rig.IsBound);
            if (_runner == null)
            {
                if (SessionState.GetInt(KeyFrames, 0) > WarmupFrames * 4)
                    Finish("error", "no bound PolicyRunner appeared in the scene");
                return false;
            }

            _rig = _runner.rig;

            var asset = AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>(_cfg.model);
            if (asset == null)
            {
                Finish("error", $"model not found at {_cfg.model}");
                return false;
            }
            if (_runner.recoveryModel != asset)
            {
                _runner.recoveryModel = asset;
                _runner.Initialize(_runner.config, _runner.model);   // reloads both workers
            }
            if (!_runner.HasRecoveryModel)
            {
                Finish("error", $"recovery worker did not load from {_cfg.model}");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Records where the deck is and how tall this athlete stands on it.
        ///
        /// Both numbers have to be relative to the floor under the body. This scene sits on the White
        /// House roof about 25 m above the world origin, so a height test written against world Y is
        /// satisfied by any pose at all -- which is exactly how the first run of this probe reported a
        /// five-second stand from an athlete that had never left its feet.
        /// </summary>
        static void MeasureStandingReference()
        {
            Vector3 p = _rig.BasePosition;
            _floorY = p.y - 1.0f;
            if (Physics.Raycast(p + Vector3.up * 0.2f, Vector3.down, out RaycastHit hit, 50f,
                                ~0, QueryTriggerInteraction.Ignore))
                _floorY = hit.point.y;
            _standHeight = Mathf.Max(0.2f, p.y - _floorY);
        }

        /// <summary>
        /// Turns off everything in the scene that would move the body on its own, so the trace measures
        /// the policy and nothing else.
        /// </summary>
        static void SilenceInterference()
        {
            int n = 0;
            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (mb == null || !mb.enabled) continue;
                if (Array.IndexOf(InterferingComponents, mb.GetType().Name) < 0) continue;
                mb.enabled = false;
                n++;
            }
            if (n > 0) Debug.Log($"[GetUpProbe] disabled {n} component(s) that would have reset the athlete");
        }

        /// <summary>
        /// Puts the athlete flat on its back, which is the full-difficulty case the MuJoCo curriculum
        /// ends at. The rig's up axis is external +Z (Unity local +Y) and its forward is external +X
        /// (Unity local +X), so rotating 90 degrees about the body's own lateral axis lays the up axis
        /// into the horizontal plane -- uprightness near zero -- with the back toward the deck.
        /// </summary>
        static void PlaceSupine()
        {
            Vector3 p = _rig.BasePosition;
            Quaternion standing = _rig.BaseRotation;
            Quaternion supine = Quaternion.AngleAxis(90f, standing * Vector3.forward) * standing;
            _rig.ResetPose(new Vector3(p.x, _floorY + 0.35f, p.z), supine);
        }

        static void OpenCsv()
        {
            CloseCsv();
            Directory.CreateDirectory(LogDir);
            _csv = new StreamWriter(CsvPath, false) { AutoFlush = true };
            _csv.WriteLine("t,upright,height,standing");
        }

        static void CloseCsv()
        {
            _csv?.Flush();
            _csv?.Dispose();
            _csv = null;
        }

        static void Finish(string status, string error)
        {
            EditorApplication.update -= Tick;
            CloseCsv();
            if (status != "aborted")
                WriteStatus(status, error);
            else
                WriteStatus("aborted", error);
            Phase = "idle";
            _runner = null;
            _rig = null;
            if (EditorApplication.isPlaying)
                EditorApplication.ExitPlaymode();
            Debug.Log($"[GetUpProbe] {status}. Summary -> {JsonPath}");
        }

        static void WriteStatus(string status, string error)
        {
            float start = SessionState.GetFloat(KeyStartUp, 0f);
            float peak = SessionState.GetFloat(KeyPeak, -1f);
            float bestHold = SessionState.GetFloat(KeyBestHold, 0f);
            var sb = new StringBuilder();
            sb.Append('{');
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"status\":\"{0}\",", status);
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"label\":\"{0}\",", _cfg?.label ?? "");
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"model\":\"{0}\",", _cfg?.model ?? "");
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"start_upright\":{0:0.####},", start);
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"peak_upright\":{0:0.####},", peak);
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"longest_hold_s\":{0:0.###},", bestHold);
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"stood\":{0},", bestHold > 0f ? "true" : "false");
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"held_one_second\":{0},", bestHold >= 1.0f ? "true" : "false");
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
            catch (Exception e)
            {
                Debug.LogWarning($"[GetUpProbe] could not read {ConfigPath}: {e.Message}");
            }
            return new Config();
        }
    }
}
