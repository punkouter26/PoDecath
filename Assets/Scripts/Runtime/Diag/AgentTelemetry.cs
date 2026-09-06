using System.Collections.Generic;
using UnityEngine;
using PoDecath.Sim;

namespace PoDecath.Diag
{
    /// <summary>
    /// What the agents are doing, and what to change because of it.
    ///
    /// The F3 panel already answered "is the frame fast enough". It could not answer the question this
    /// project actually spends its time on, which is "the athlete is running badly — is that the policy,
    /// the config, or the track?". Every number needed to tell those apart existed somewhere: uprightness
    /// on <see cref="RaceEvent.Athlete"/>, the model name on <see cref="PolicyRunner"/>, the clipping and
    /// clamping counters added to <c>PolicyRunner.Step</c>. None of them were anywhere a person could see
    /// them while the race was running, which is the only time they mean anything.
    ///
    /// So this samples all of them four times a second, keeps ninety seconds of history per agent, and —
    /// the part that matters — turns them into sentences. A number like "observation clipping 0.06" is
    /// only useful to somebody who already knows what it implies; "6% of the observation is arriving at
    /// the clip — a *Scale field does not match this checkpoint, and nothing else on this card can be
    /// trusted until that is fixed" is useful to whoever is holding the phone.
    ///
    /// The ordering of <see cref="Diagnose"/> is deliberate and is the whole design: a config fault
    /// invalidates every behavioural reading below it, so it is reported first and alone. Reporting "falls
    /// a lot" against a policy that is being fed a mis-scaled observation sends somebody off to retrain a
    /// checkpoint that was never the problem.
    /// </summary>
    [DefaultExecutionOrder(150)]
    public class AgentTelemetry : MonoBehaviour
    {
        [Tooltip("Optional. With a race, agents come from its field; without one, from every PolicyRunner "
               + "in the scene, so the panel works in a development scene too.")]
        public RaceEvent race;

        [Tooltip("Sampling period. The history is this times historySamples of wall clock.")]
        public float sampleSeconds = 0.25f;

        [Tooltip("Samples kept per agent. 360 at 4 Hz is ninety seconds, which covers a 400 m.")]
        public int historySamples = 360;

        [Tooltip("Write a session summary whenever a race restarts, and when the app is backgrounded or "
               + "quits. Off makes EXPORT on the diagnostics sheet the only way to get one.")]
        public bool exportOnRestart = true;

        /// <summary>How a line should be read: fine, worth knowing, or the thing to go and fix.</summary>
        public enum Grade { Good, Warn, Bad, Neutral }

        /// <summary>
        /// One athlete's rolling picture. Public fields rather than properties because the overlay walks
        /// this four times a second and the point of the class is to be read.
        /// </summary>
        public class Agent
        {
            public string name = "-";
            public bool isRL;

            public PolicyRunner runner;
            public RaceEvent.Athlete athlete;

            /// <summary>The ONNX actually driving this frame — the run policy, or the get-up one.</summary>
            public string activeModel = "-";
            public string runModel = "-";
            public string recoveryModel;
            public int obsSize, actSize;
            public float configHz;

            // Behaviour
            public float speed, speedPeak, speedMean;
            public float upright, uprightMin = 1f, uprightMean = 1f;
            public float distance;
            public int falls, recoveries, attempts;
            public bool down, recovering;
            public float downSeconds;

            // Execution — see the PolicyRunner properties of the same names.
            public float inferMs, clampFrac, obsClipFrac, jitter, actionMag, controlHz;

            // History, oldest-first when read through Sample().
            public float[] speedHistory, uprightHistory;
            public int historyCount, historyHead;

            public string verdict = "";
            public Grade grade = Grade.Neutral;

            float _speedSum; int _speedSamples;
            bool _wasDown;
            internal bool seen;

            internal void EnsureHistory(int n)
            {
                if (speedHistory != null && speedHistory.Length == n) return;
                speedHistory = new float[n];
                uprightHistory = new float[n];
                historyCount = historyHead = 0;
            }

            internal void Push(float s, float u)
            {
                speedHistory[historyHead] = s;
                uprightHistory[historyHead] = u;
                historyHead = (historyHead + 1) % speedHistory.Length;
                if (historyCount < speedHistory.Length) historyCount++;
            }

            /// <summary>Reads the ring buffer oldest-first, so a graph of it runs left to right in time.</summary>
            public float Sample(float[] buffer, int i) =>
                buffer[(historyHead - historyCount + i + buffer.Length) % buffer.Length];

            internal void Accumulate(float dt)
            {
                _speedSum += speed; _speedSamples++;
                speedMean = _speedSamples > 0 ? _speedSum / _speedSamples : 0f;
                speedPeak = Mathf.Max(speedPeak, speed);
                uprightMin = Mathf.Min(uprightMin, upright);

                // A fall is counted on the edge, not on the flag: the flag stays true for the rest of the
                // attempt, so polling it would count one fall four times a second for as long as the body
                // stayed down.
                if (down && !_wasDown) falls++;
                _wasDown = down;
                if (down) downSeconds += dt;
            }

            internal void OnAttemptChanged()
            {
                attempts++;
                _wasDown = false;
                down = false;
            }
        }

        readonly List<Agent> _agents = new List<Agent>();
        readonly Dictionary<PolicyRunner, Agent> _byRunner = new Dictionary<PolicyRunner, Agent>();

        /// <summary>The field, in the order the race lists it. Rebuilt only when the field changes.</summary>
        public IReadOnlyList<Agent> Agents => _agents;

        /// <summary>Seconds of history the graphs are showing.</summary>
        public float HistorySeconds => historySamples * sampleSeconds;

        /// <summary>One sentence about the field as a whole, for the top of the panel.</summary>
        public string Headline { get; private set; } = "No agents in this scene.";
        public Grade HeadlineGrade { get; private set; } = Grade.Neutral;

        float _next;
        int _lastAttempt = -1;

        void Update()
        {
            if (Time.unscaledTime < _next) return;
            float dt = _next > 0f ? Time.unscaledTime - _next + sampleSeconds : sampleSeconds;
            _next = Time.unscaledTime + sampleSeconds;

            Collect();
            foreach (Agent a in _agents)
            {
                ReadAgent(a);
                a.Accumulate(dt);
                a.EnsureHistory(Mathf.Max(16, historySamples));
                a.Push(a.speed, a.upright);
                Diagnose(a);
            }
            Summarise();
        }

        // ---------------------------------------------------------------- gathering

        /// <summary>
        /// Finds the field. A race owns one, so it is asked; a development scene has loose
        /// <see cref="PolicyRunner"/>s and is swept instead. Entries are keyed by runner so an athlete
        /// keeps its history across a restart, which is the whole reason the panel is worth opening —
        /// "it fell on the same bend three attempts running" is the finding, and it is invisible if the
        /// history resets with the race.
        /// </summary>
        void Collect()
        {
            foreach (Agent a in _agents) a.seen = false;

            if (race != null)
            {
                if (race.Attempt != _lastAttempt)
                {
                    // Written before the counters roll over, so the file describes the attempt that just
                    // ended rather than the empty one about to start. This is the export that happens
                    // without anybody asking for it, and it is the one that makes a training run
                    // comparable to the last: a restart is exactly when a result exists and is about to
                    // stop being on screen.
                    if (_lastAttempt >= 0)
                    {
                        if (exportOnRestart) WriteSessionSummary($"attempt{_lastAttempt}");
                        foreach (Agent a in _agents) a.OnAttemptChanged();
                    }
                    _lastAttempt = race.Attempt;
                }
                foreach (RaceEvent.Athlete ath in race.Athletes)
                {
                    Agent a = Resolve(ath.runner, ath.name);
                    a.athlete = ath;
                    a.isRL = ath.IsRL;
                    a.seen = true;
                }
            }
            else
            {
                PolicyRunner[] runners = FindObjectsByType<PolicyRunner>(FindObjectsSortMode.None);
                foreach (PolicyRunner r in runners)
                {
                    Agent a = Resolve(r, r != null ? r.name : "agent");
                    a.isRL = true;
                    a.seen = true;
                }
            }

            for (int i = _agents.Count - 1; i >= 0; i--)
            {
                if (_agents[i].seen) continue;
                if (_agents[i].runner != null) _byRunner.Remove(_agents[i].runner);
                _agents.RemoveAt(i);
            }
        }

        Agent Resolve(PolicyRunner runner, string name)
        {
            if (runner != null && _byRunner.TryGetValue(runner, out Agent found))
            {
                found.name = name;
                return found;
            }
            // A heuristic bot has no runner at all, so it cannot be keyed by one; match it by name.
            if (runner == null)
                foreach (Agent a in _agents)
                    if (a.runner == null && a.name == name) return a;

            var made = new Agent { name = name, runner = runner };
            made.EnsureHistory(Mathf.Max(16, historySamples));
            if (runner != null) _byRunner[runner] = made;
            _agents.Add(made);
            return made;
        }

        /// <summary>Copies this tick's values off the athlete and its runner.</summary>
        void ReadAgent(Agent a)
        {
            RaceEvent.Athlete ath = a.athlete;
            if (ath != null)
            {
                a.speed = ath.speed;
                a.distance = ath.distance;
                a.down = ath.fell;
                a.recovering = ath.recovering;
                a.recoveries = ath.recoveries;
                a.upright = ath.rig != null ? Mathf.Clamp01(ath.rig.UprightDot) : 1f;
                a.uprightMean = ath.MeanUpright;
            }

            PolicyRunner r = a.runner;
            if (r == null)
            {
                a.activeModel = "heuristic (no ONNX)";
                a.runModel = "heuristic";
                return;
            }

            a.activeModel = r.ActiveModelName;
            a.runModel = r.ModelName;
            a.recoveryModel = r.recoveryModel != null ? r.recoveryModel.name : null;
            a.obsSize = r.ObservationSize;
            a.actSize = r.ActionSize;
            a.configHz = r.config != null ? r.config.ControlHz : 0f;

            a.inferMs = r.InferenceMs;
            a.clampFrac = r.TargetClamping;
            a.obsClipFrac = r.ObservationClipping;
            a.jitter = r.ActionRate;
            a.actionMag = r.ActionMagnitude;
            a.controlHz = r.MeasuredControlHz;
        }

        // ---------------------------------------------------------------- the sentences

        // Thresholds, in one place, with the reason each was picked. They are deliberately loose: this is
        // a panel that has to be right about the direction of a problem, not precise about its size.
        const float ObsClipBad = 0.02f;    // 1 slot in 48 clipped is already a config fault, not noise
        const float ClampBad = 0.10f;      // a tenth of the joints asking for a pose the rig cannot hold
        const float JitterWarn = 0.35f;    // action units per 20 ms step; a clean gait sits well under this
        const float HzSlack = 0.9f;        // control rate this far under the config's is the device losing

        /// <summary>
        /// Turns one agent's numbers into the shortest sentence that says what to do about them.
        ///
        /// Strictly ordered worst-cause-first, and it stops at the first hit. Two faults at once is
        /// normal — a mis-scaled observation makes a policy fall over, which makes it look unstable —
        /// and listing both invites fixing the symptom. The cause is always the earlier line.
        /// </summary>
        void Diagnose(Agent a)
        {
            if (!a.isRL)
            {
                a.grade = Grade.Neutral;
                a.verdict = "Heuristic bot. Scripted gait, nothing to train — it is the yardstick the RL athletes are read against.";
                return;
            }

            if (a.runner == null || a.runModel.StartsWith("(none"))
            {
                a.grade = Grade.Bad;
                a.verdict = "No ONNX loaded — this body is holding its default pose. Check Assets/Policies and run PoDecath/Refresh Policy Library.";
                return;
            }

            if (a.obsClipFrac > ObsClipBad)
            {
                a.grade = Grade.Bad;
                a.verdict = $"{Pct(a.obsClipFrac)} of the observation is arriving at the clip. A *Scale field on the "
                          + "PolicyConfig does not match what this checkpoint was normalised with, so the policy is "
                          + "seeing inputs it never trained on. Fix that first — nothing below is meaningful until it is.";
                return;
            }

            if (a.clampFrac > ClampBad)
            {
                a.grade = Grade.Bad;
                a.verdict = $"{Pct(a.clampFrac)} of joint targets are being clamped to the rig's limits. The pose the "
                          + "policy asks for is not the pose the body gets. Lower actionScale, or regenerate the rig — "
                          + "its limits have drifted from training/models/athlete.xml.";
                return;
            }

            if (a.configHz > 0f && a.controlHz > 0f && a.controlHz < a.configHz * HzSlack)
            {
                a.grade = Grade.Bad;
                a.verdict = $"Running at {a.controlHz:F0} Hz of control, not {a.configHz:F0}. The device cannot hold "
                          + "200 Hz physics with this field, so the policy is being stepped at a rate it was not trained "
                          + "at. Race fewer athletes, or raise controlDecimation and retrain to match.";
                return;
            }

            if (a.falls > 0)
            {
                a.grade = Grade.Warn;
                string where = a.athlete != null && a.athlete.fell ? $", last at {a.athlete.fellAt:F0} m" : "";
                a.verdict = $"Fell {a.falls}x{where} and got up {a.recoveries}x. Uprightness bottomed at {a.uprightMin:F2}. "
                          + (a.recoveries > 0
                             ? "Recovery is working; the running policy is the one to harden."
                             : "No recoveries — either athlete_getup.onnx is not on this athlete, or it used its three.");
                return;
            }

            if (a.jitter > JitterWarn)
            {
                a.grade = Grade.Warn;
                a.verdict = $"Twitchy: the action moves {a.jitter:F2} per step. That is the policy chattering against "
                          + "the PD gains, and it costs speed and stability. Raise the action-rate penalty in training "
                          + "rather than changing anything here.";
                return;
            }

            if (a.speedMean > 0.1f && a.speedPeak > 0.1f && a.speedMean > a.speedPeak * 0.85f)
            {
                a.grade = Grade.Good;
                a.verdict = $"Clean and flat out: holding {a.speedMean:F2} m/s against a {a.speedPeak:F2} m/s peak, no "
                          + "falls. This is the reward cap, not the body's limit — raise --target-speed-final with "
                          + "--speed-adaptive to ask for more. Above ~5 m/s the 8.8 m bend, not the policy, is what breaks.";
                return;
            }

            a.grade = Grade.Good;
            a.verdict = $"Running clean at {a.speedMean:F2} m/s, uprightness {a.uprightMean:F2}, no falls. Nothing to fix.";
        }

        /// <summary>The one line at the top: how many are up, how many are down, and the worst fault found.</summary>
        void Summarise()
        {
            if (_agents.Count == 0)
            {
                Headline = "No agents in this scene.";
                HeadlineGrade = Grade.Neutral;
                return;
            }

            int rl = 0, down = 0, falls = 0;
            Agent worst = null;
            foreach (Agent a in _agents)
            {
                if (a.isRL) rl++;
                if (a.down) down++;
                falls += a.falls;
                if (a.grade == Grade.Bad && worst == null) worst = a;
            }

            if (worst != null)
            {
                Headline = $"{worst.name}: {First(worst.verdict)}";
                HeadlineGrade = Grade.Bad;
                return;
            }

            HeadlineGrade = falls > 0 ? Grade.Warn : Grade.Good;
            Headline = falls > 0
                ? $"{rl} RL of {_agents.Count} athletes, {down} down now, {falls} falls this session. Config is clean — the falls are the policy."
                : $"{rl} RL of {_agents.Count} athletes, no falls, no config faults. Read the speeds below for what to ask for next.";
        }

        static string First(string sentence)
        {
            int stop = sentence.IndexOf(". ", System.StringComparison.Ordinal);
            return stop > 0 ? sentence.Substring(0, stop + 1) : sentence;
        }

        static string Pct(float f) => $"{f * 100f:F1}%";

        // ---------------------------------------------------------------- the export
        //
        // The panel answers "what is wrong now". It cannot answer "is this checkpoint better than the last
        // one", because that question is asked hours apart, usually on a different device, and always
        // after the screen it would have been answered on is gone.
        //
        // So the same numbers the cards are drawn from are also written to a file: one per restart, one on
        // the way out, one whenever EXPORT is pressed - plus latest.json, which is always the most recent
        // and is what the deploy script pulls off the phone. The trace is downsampled to sixty points per
        // athlete, which is enough to see where in the lap an athlete slowed down and small enough that a
        // session of files is still a few tens of kilobytes.

        /// <summary>Where the files go on the device. Pulled from here by training/deploy_android.ps1.</summary>
        public static string ExportDirectory => System.IO.Path.Combine(Application.persistentDataPath, "telemetry");

        const int TracePoints = 60;

        [System.Serializable]
        class AgentRecord
        {
            public string name;
            public bool isRL;
            public string activeModel, runModel, recoveryModel;
            public int observationSize, actionSize;

            public float configHz, measuredHz;
            public float speedMean, speedPeak, distance;
            public float uprightMean, uprightMin;
            public int falls, recoveries;
            public float downSeconds;

            public float inferenceMs, observationClipping, targetClamping, actionRate, actionMagnitude;

            public string grade, verdict;

            /// <summary>Downsampled speed and uprightness, oldest first, over <c>traceSeconds</c>.</summary>
            public float[] speedTrace, uprightTrace;
        }

        [System.Serializable]
        class SessionRecord
        {
            public string writtenAt, reason, appVersion, scene, device, os, graphics;
            public float sessionSeconds, traceSeconds;
            public int attempt;
            public string headline, headlineGrade;
            public AgentRecord[] agents;
        }

        /// <summary>
        /// Writes one session summary and returns the path, or an empty string if it could not be written.
        ///
        /// Never throws: this is called from a UI button, from a scene restart and from the quit path, and
        /// a diagnostic that can take the game down with it on a full disk is worse than no diagnostic.
        /// </summary>
        public string WriteSessionSummary(string reason)
        {
            try
            {
                var record = new SessionRecord
                {
                    writtenAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    reason = reason,
                    appVersion = Application.version,
                    scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                    device = $"{SystemInfo.deviceModel} ({SystemInfo.processorType}, {SystemInfo.systemMemorySize} MB)",
                    os = SystemInfo.operatingSystem,
                    graphics = $"{SystemInfo.graphicsDeviceType} {SystemInfo.graphicsDeviceName}",
                    sessionSeconds = Time.unscaledTime,
                    traceSeconds = HistorySeconds,
                    attempt = _lastAttempt,
                    headline = Headline,
                    headlineGrade = HeadlineGrade.ToString(),
                    agents = new AgentRecord[_agents.Count],
                };

                for (int i = 0; i < _agents.Count; i++) record.agents[i] = Record(_agents[i]);

                System.IO.Directory.CreateDirectory(ExportDirectory);
                string json = JsonUtility.ToJson(record, true);

                string stamp = System.DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string path = System.IO.Path.Combine(ExportDirectory, $"session-{stamp}-{Safe(reason)}.json");
                System.IO.File.WriteAllText(path, json);

                // A stable name as well as a timestamped one: the deploy script has to be able to pull the
                // newest file without listing the directory and parsing dates off a phone.
                System.IO.File.WriteAllText(System.IO.Path.Combine(ExportDirectory, "latest.json"), json);

                Debug.Log($"[AgentTelemetry] Session summary written to {path}");
                return path;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[AgentTelemetry] Could not write the session summary: {e.Message}");
                return "";
            }
        }

        AgentRecord Record(Agent a)
        {
            return new AgentRecord
            {
                name = a.name,
                isRL = a.isRL,
                activeModel = a.activeModel,
                runModel = a.runModel,
                recoveryModel = a.recoveryModel,
                observationSize = a.obsSize,
                actionSize = a.actSize,

                configHz = a.configHz,
                measuredHz = a.controlHz,
                speedMean = a.speedMean,
                speedPeak = a.speedPeak,
                distance = a.distance,
                uprightMean = a.uprightMean,
                uprightMin = a.uprightMin,
                falls = a.falls,
                recoveries = a.recoveries,
                downSeconds = a.downSeconds,

                inferenceMs = a.inferMs,
                observationClipping = a.obsClipFrac,
                targetClamping = a.clampFrac,
                actionRate = a.jitter,
                actionMagnitude = a.actionMag,

                grade = a.grade.ToString(),
                verdict = a.verdict,

                speedTrace = Trace(a, a.speedHistory),
                uprightTrace = Trace(a, a.uprightHistory),
            };
        }

        /// <summary>
        /// The history, oldest-first, thinned to at most <see cref="TracePoints"/> by averaging each bucket
        /// rather than picking one sample out of it. Picking would drop exactly the spikes - the one sample
        /// where uprightness collapsed - that make the trace worth keeping.
        /// </summary>
        static float[] Trace(Agent a, float[] history)
        {
            if (history == null || a.historyCount == 0) return new float[0];

            int n = Mathf.Min(TracePoints, a.historyCount);
            var outp = new float[n];
            for (int i = 0; i < n; i++)
            {
                int from = i * a.historyCount / n;
                int to = Mathf.Max(from + 1, (i + 1) * a.historyCount / n);
                float sum = 0f;
                for (int j = from; j < to; j++) sum += a.Sample(history, j);
                outp[i] = sum / (to - from);
            }
            return outp;
        }

        static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "run";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
            return sb.ToString();
        }

        /// <summary>
        /// Backgrounding is how an Android session actually ends - onPause is guaranteed, onDestroy is not
        /// - so the summary is written there as well as on quit. Writing both means a duplicate file on a
        /// clean exit, which is a far smaller problem than a run that was never recorded.
        /// </summary>
        void OnApplicationPause(bool paused)
        {
            if (paused && exportOnRestart && _agents.Count > 0) WriteSessionSummary("paused");
        }

        void OnApplicationQuit()
        {
            if (exportOnRestart && _agents.Count > 0) WriteSessionSummary("quit");
        }

    }
}
