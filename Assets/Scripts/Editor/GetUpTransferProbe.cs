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
        const string KeySteps0 = "PoDecath.GetUpProbe.Steps0";
        const string KeySteps = "PoDecath.GetUpProbe.Steps";
        const string KeyActSum = "PoDecath.GetUpProbe.ActSum";
        const string KeyActN = "PoDecath.GetUpProbe.ActN";
        const string KeyModelName = "PoDecath.GetUpProbe.ModelName";

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
            /// <summary>
            /// Drive the recovery through <see cref="RecoveryController"/> instead of switching the
            /// policy on directly. This is the integration test: it answers "does a fallen athlete
            /// rejoin the race", where the default mode only answers "can the policy stand it up".
            /// Those are different questions, and the project spent a while with the first one
            /// answered yes and the second one answered no.
            /// </summary>
            public bool use_recovery_controller = false;
        }

        static Config _cfg;
        static PolicyRunner _runner;
        static CreatureRig _rig;
        static float _standHeight = 0.95f;   // pelvis height above the local floor when standing
        static float _floorY;                // world Y of the deck under the athlete
        static RecoveryController _recovery;
        static StreamWriter _csv;

        // Anything that would reset, respawn or re-police the body while the probe is running. The lap
        // scene is hands-off by design: it puts a fallen athlete back on its feet after three seconds,
        // which silently turned the first run of this probe into a measurement of a standing athlete.
        static readonly string[] InterferingComponents =
            { "LapEvent", "DashEvent", "LongJumpEvent", "EpisodeManager", "RecoveryController" };

        // In controller mode the recovery controller is the thing under test, so it stays on.
        static readonly string[] InterferingWithController =
            { "LapEvent", "DashEvent", "LongJumpEvent", "EpisodeManager" };

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
            // Reset the diagnostics too, or a failed run reports the previous run's active model.
            SessionState.SetInt(KeySteps, 0);
            SessionState.SetInt(KeySteps0, 0);
            SessionState.SetFloat(KeyActSum, 0f);
            SessionState.SetInt(KeyActN, 0);
            SessionState.SetString(KeyModelName, "(none)");
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
                            // Park the runner while the body settles, which is what a real fall does:
                            // the event switches a fallen runner off and RecoveryController switches it
                            // back on. Leaving it on meant the *running* policy spent the settle window
                            // driving the athlete back onto its feet, so the probe never saw a fallen
                            // body to measure.
                            _runner.enabled = false;
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
                            // Baseline the policy's own step counter so the summary can say whether the
                            // network actually ran. Without this the probe cannot tell a policy that
                            // tried and failed from one that never drove the joints at all -- and those
                            // two produce the same uprightness trace.
                            SessionState.SetInt(KeySteps0, _runner.PolicySteps);
                            SessionState.SetString(KeyModelName, _runner.ActiveModelName);
                            SessionState.SetFloat(KeyActSum, 0f);
                            SessionState.SetInt(KeyActN, 0);
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

                            // Every tick, not once, and both flags.
                            //
                            // UseRecovery: PolicyRunner.Initialize() clears it, and Acquire()
                            // re-initialises whenever a domain reload drops the static references --
                            // which silently handed the first runs of this probe back to the *running*
                            // policy.
                            //
                            // enabled: the events own the runner's on/off switch. DashEvent parks every
                            // runner with `enabled = false` while it sets the field up and only switches
                            // the RL athletes on when the countdown ends. SilenceInterference disables
                            // the event before that ever happens, so unless the probe turns the runner
                            // on itself the body just lies there and the trace is passive settling.
                            if (_cfg.use_recovery_controller)
                            {
                                // Ask once, then keep hands off: the controller owns both flags now.
                                if (_recovery != null && _recovery.Current == RecoveryController.State.Running)
                                    _recovery.TryRecover();
                            }
                            else
                            {
                                _runner.UseRecovery = true;
                                _runner.enabled = true;
                            }

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

                            SessionState.SetInt(KeySteps, _runner.PolicySteps - SessionState.GetInt(KeySteps0, 0));
                            SessionState.SetString(KeyModelName, _runner.ActiveModelName);
                            float[] act = _runner.LastAction;
                            if (act != null && act.Length > 0)
                            {
                                float mag = 0f;
                                for (int a = 0; a < act.Length; a++) mag += Mathf.Abs(act[a]);
                                SessionState.SetFloat(KeyActSum, SessionState.GetFloat(KeyActSum, 0f) + mag / act.Length);
                                SessionState.SetInt(KeyActN, SessionState.GetInt(KeyActN, 0) + 1);
                            }

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
                            {
                                int steps = SessionState.GetInt(KeySteps, 0);
                                string active = SessionState.GetString(KeyModelName, "?");
                                string want = Path.GetFileNameWithoutExtension(_cfg.model);
                                if (steps <= 0)
                                    Finish("error", $"the policy never ran ({steps} steps): this trace is "
                                                  + "passive ragdoll settling, not a get-up attempt");
                                else if (active != want && !_cfg.use_recovery_controller)
                                    Finish("error", $"wrong policy drove the body: active '{active}', "
                                                  + $"expected '{want}'");
                                // In controller mode ending on the *running* policy is the win: it means
                                // RecoveryController got the athlete up and handed it back to the race.
                                else
                                    Finish("completed", null);
                            }
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
            var bound = runners.Where(r => r.rig != null && r.rig.IsBound).ToList();
            // In controller mode the athlete under test is specifically one that has a
            // RecoveryController on it. A lap scene holds more than one rig and the first bound runner
            // is not necessarily the one the spawner gave a controller to.
            _runner = (_cfg.use_recovery_controller
                          ? bound.FirstOrDefault(r => r.GetComponent<RecoveryController>() != null)
                          : null)
                      ?? bound.FirstOrDefault();
            if (_runner == null)
            {
                if (SessionState.GetInt(KeyFrames, 0) > WarmupFrames * 4)
                    Finish("error", "no bound PolicyRunner appeared in the scene");
                return false;
            }

            _rig = _runner.rig;
            _recovery = _runner.GetComponent<RecoveryController>();

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

            // Cast past the athlete, not into it. A ray dropped from just above the pelvis hits the
            // rig's own hip and thigh colliders within centimetres, which put the "floor" about 0.6 m
            // too high: every height in the trace came out negative and the standing test -- height at
            // least 80% of standing -- could never pass. The result read as "gets upright but never
            // stands" when what it actually measured was the floor being in the wrong place.
            int creature = LayerMask.NameToLayer("Creature");
            int mask = creature >= 0 ? ~(1 << creature) : ~0;

            _floorY = p.y - 1.0f;
            if (Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 60f,
                                mask, QueryTriggerInteraction.Ignore))
                _floorY = hit.point.y;
            _standHeight = p.y - _floorY;

            // A standing humanoid's pelvis is around 0.9 m up. Anything far from that means the ray
            // found the wrong surface, and every height in this run would be meaningless.
            if (_standHeight < 0.4f || _standHeight > 1.6f)
            {
                Finish("error", $"implausible standing height {_standHeight:0.###} m " +
                                $"(pelvis {p.y:0.##}, floor {_floorY:0.##}) -- heights would be meaningless");
                return;
            }
            Debug.Log($"[GetUpProbe] standing pelvis {_standHeight:0.###} m above deck y={_floorY:0.##}");
        }

        /// <summary>
        /// Turns off everything in the scene that would move the body on its own, so the trace measures
        /// the policy and nothing else.
        /// </summary>
        static void SilenceInterference()
        {
            int n = 0;
            string[] list = (_cfg != null && _cfg.use_recovery_controller)
                ? InterferingWithController : InterferingComponents;
            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (mb == null || !mb.enabled) continue;
                if (Array.IndexOf(list, mb.GetType().Name) < 0) continue;
                // Disabling a MonoBehaviour stops its Update, not its coroutines. The race events run
                // their whole setup -- countdown, athletes placed on the start line -- from a coroutine,
                // which cheerfully carried on and stood the athlete back up moments after the probe had
                // laid it down.
                mb.StopAllCoroutines();
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
            // Construct the pose, do not hope physics settles into one.
            //
            // Rotating the standing pose 90 degrees about its own lateral axis and dropping it from
            // 0.35 m gave a different resting pose every run -- uprightness 0.07 once and 0.575 the
            // next, the second of which is not a fallen athlete at all. The starting pose is the
            // independent variable of this whole measurement, so it has to be exact.
            //
            // The rig's head axis is local +Y and its facing axis is local +X. Flat on the back means
            // the head axis lies in the horizontal plane and the facing axis points at the sky. In
            // Unity's basis Cross(right, up) == forward, so mapping local +Y onto the ground-projected
            // heading and local +Z onto Cross(worldUp, heading) puts local +X on world up exactly.
            Vector3 p = _rig.BasePosition;
            Vector3 heading = Vector3.ProjectOnPlane(_rig.BaseRotation * Vector3.right, Vector3.up);
            if (heading.sqrMagnitude < 1e-4f) heading = Vector3.forward;
            heading.Normalize();
            Quaternion supine = Quaternion.LookRotation(Vector3.Cross(Vector3.up, heading), heading);

            // Just clear of the deck, so it settles rather than bounces.
            _rig.ResetPose(new Vector3(p.x, _floorY + 0.22f, p.z), supine);
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
            _recovery = null;
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
            int actN = SessionState.GetInt(KeyActN, 0);
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"active_model\":\"{0}\",",
                SessionState.GetString(KeyModelName, "?"));
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"policy_steps\":{0},", SessionState.GetInt(KeySteps, 0));
            if (_cfg != null && _cfg.use_recovery_controller)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "\"recovery_state\":\"{0}\",",
                    _recovery != null ? _recovery.Current.ToString() : "(no controller)");
                sb.AppendFormat(CultureInfo.InvariantCulture, "\"recoveries\":{0},",
                    _recovery != null ? _recovery.Recoveries : -1);
                sb.AppendFormat(CultureInfo.InvariantCulture, "\"controllers_in_scene\":{0},",
                    UnityEngine.Object.FindObjectsByType<RecoveryController>(FindObjectsSortMode.None).Length);
            }
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"mean_abs_action\":{0:0.####},",
                actN > 0 ? SessionState.GetFloat(KeyActSum, 0f) / actN : 0f);
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
