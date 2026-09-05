using System;
using System.IO;
using UnityEngine;

namespace PoDecath.Sim
{
    public enum TerminationReason { None, Fell, LowHeight, OutOfBounds, Timeout, TargetReached, Manual }

    [Serializable]
    public struct EpisodeResult
    {
        public int index;
        public float duration;
        public float distance;
        public float meanSpeed;
        public float meanStability;
        public TerminationReason reason;
    }

    /// <summary>
    /// Zero-interaction evaluation loop. Watches termination conditions every physics step,
    /// records episode statistics, and resets the creature automatically.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public class EpisodeManager : MonoBehaviour
    {
        public PolicyRunner runner;
        public CreatureRig rig;
        public VelocityCommandSource commandSource;
        public ArenaGenerator arena;

        [Header("Reset")]
        public bool randomizeSpawnYaw = false;
        public float targetReachRadius = 0.5f;
        public bool autoRestart = true;
        [Tooltip("Physics steps to wait after a reset before termination checks start.")]
        public int settleSteps = 20;

        [Header("Logging")]
        public bool writeCsv = true;
        public string csvFileName = "podecath_eval.csv";

        public event Action<EpisodeResult> EpisodeEnded;
        public event Action EpisodeStarted;

        public int EpisodeIndex { get; private set; }
        public float EpisodeTime { get; private set; }
        public float Distance { get; private set; }
        public float Speed { get; private set; }
        public float Stability { get; private set; }
        public EpisodeResult LastResult { get; private set; }
        public bool Running { get; private set; }
        public Vector3 TargetPosition { get; private set; }

        Vector3 _lastPos;
        float _stabilityAccum;
        int _stabilitySamples;
        int _stepsSinceReset;
        StreamWriter _csv;
        System.Random _rng = new System.Random(7);

        void OnDestroy()
        {
            _csv?.Flush();
            _csv?.Dispose();
        }

        public void Begin()
        {
            if (writeCsv && _csv == null)
            {
                try
                {
                    string path = Path.Combine(Application.persistentDataPath, csvFileName);
                    bool exists = File.Exists(path);
                    _csv = new StreamWriter(path, true);
                    if (!exists) _csv.WriteLine("episode,model,arena,reason,duration_s,distance_m,mean_speed_mps,mean_stability");
                }
                catch (Exception e) { Debug.LogWarning($"[EpisodeManager] CSV disabled: {e.Message}"); _csv = null; }
            }
            StartEpisode();
        }

        void StartEpisode()
        {
            PolicyConfig cfg = runner != null ? runner.config : null;
            float h = cfg != null ? cfg.spawnHeight : 0.4f;
            Vector3 spawn = (arena != null ? arena.SpawnPosition : Vector3.zero) + Vector3.up * h;
            Quaternion rot = Quaternion.identity;
            if (randomizeSpawnYaw) rot = Quaternion.Euler(0f, (float)(_rng.NextDouble() * 360.0), 0f);

            TargetPosition = arena != null ? arena.SampleTarget(_rng) : spawn + Vector3.right * 6f;
            if (commandSource != null)
            {
                commandSource.SetTarget(TargetPosition);
                commandSource.ResetCommand();
            }

            if (runner != null) runner.ResetEpisode(spawn, rot);
            else if (rig != null) rig.ResetPose(spawn, rot);

            EpisodeTime = 0f;
            Distance = 0f;
            Speed = 0f;
            Stability = 1f;
            _stabilityAccum = 0f;
            _stabilitySamples = 0;
            _stepsSinceReset = 0;
            _lastPos = spawn;
            Running = true;
            EpisodeStarted?.Invoke();
        }

        void FixedUpdate()
        {
            if (!Running || rig == null) return;

            float dt = Time.fixedDeltaTime;
            EpisodeTime += dt;
            _stepsSinceReset++;

            Vector3 pos = rig.BasePosition;
            Vector3 delta = pos - _lastPos; delta.y = 0f;
            Distance += delta.magnitude;
            _lastPos = pos;

            Vector3 v = rig.BaseLinearVelocityWorld; v.y = 0f;
            Speed = Mathf.Lerp(Speed, v.magnitude, 0.1f);

            PolicyConfig cfg = runner != null ? runner.config : null;
            float minUp = cfg != null ? cfg.minUprightDot : 0.3f;
            float upright = rig.UprightDot;
            float instStability = Mathf.Clamp01((upright - minUp) / Mathf.Max(1e-3f, 1f - minUp));
            Stability = Mathf.Lerp(Stability, instStability, 0.05f);
            _stabilityAccum += instStability;
            _stabilitySamples++;

            if (_stepsSinceReset < settleSteps) return;

            TerminationReason reason = TerminationReason.None;
            float floorY = arena != null ? arena.FloorHeightAt(pos) : 0f;
            float minH = cfg != null ? cfg.minBaseHeight : 0.12f;
            float maxT = cfg != null ? cfg.episodeSeconds : 20f;

            if (upright < minUp) reason = TerminationReason.Fell;
            else if (pos.y - floorY < minH) reason = TerminationReason.LowHeight;
            else if (arena != null && !arena.Bounds.Contains(pos)) reason = TerminationReason.OutOfBounds;
            else if (Vector3.Distance(new Vector3(pos.x, 0f, pos.z), new Vector3(TargetPosition.x, 0f, TargetPosition.z)) < targetReachRadius)
                reason = TerminationReason.TargetReached;
            else if (EpisodeTime >= maxT) reason = TerminationReason.Timeout;

            if (reason != TerminationReason.None) EndEpisode(reason);
        }

        public void RestartNow() => EndEpisode(TerminationReason.Manual);

        void EndEpisode(TerminationReason reason)
        {
            Running = false;
            var result = new EpisodeResult
            {
                index = EpisodeIndex,
                duration = EpisodeTime,
                distance = Distance,
                meanSpeed = EpisodeTime > 0f ? Distance / EpisodeTime : 0f,
                meanStability = _stabilitySamples > 0 ? _stabilityAccum / _stabilitySamples : 0f,
                reason = reason,
            };
            LastResult = result;

            if (_csv != null)
            {
                string modelName = runner != null ? runner.ModelName : "none";
                string arenaName = arena != null ? arena.type.ToString() : "none";
                _csv.WriteLine($"{result.index},{modelName},{arenaName},{reason},{result.duration:F2},{result.distance:F3},{result.meanSpeed:F3},{result.meanStability:F3}");
                _csv.Flush();
            }

            EpisodeEnded?.Invoke(result);
            EpisodeIndex++;
            if (autoRestart) StartEpisode();
        }
    }
}
