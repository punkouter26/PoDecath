using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Pure-pursuit "carrot" for run-to-target policies: keeps a target `lookahead` metres ahead of the
    /// athlete on the TrackPath centre line (plus the athlete's lane offset) and tracks lap progress.
    /// Runs before PolicyRunner so the command is fresh for the policy step.
    /// </summary>
    [DefaultExecutionOrder(-55)]
    public class TrackFollower : MonoBehaviour
    {
        public TrackPath path;
        public VelocityCommandSource command;
        public CreatureRig rig;
        [Tooltip("Carrot distance ahead along the track (m). Training used 6 m.")]
        public float lookahead = 6f;
        public float lateralOffset = 0f;

        public float S { get; private set; }
        public float Progress { get; private set; }
        public int Laps { get; private set; }
        public float LastLapTime { get; private set; }
        public float Lateral { get; private set; }
        float _lapStart;

        public void ResetAt(float s, float lateral)
        {
            S = Mathf.Repeat(s, path != null ? path.LapLength : 1f);
            lateralOffset = lateral;
            Progress = 0f;
            Laps = 0;
            LastLapTime = 0f;
            _lapStart = Time.fixedTime;
            UpdateCarrot();
        }

        void FixedUpdate()
        {
            if (path == null || rig == null) return;
            float lap = path.LapLength;
            float sNew = path.Project(rig.BasePosition, S);
            float ds = Mathf.Repeat(sNew - S + lap * 0.5f, lap) - lap * 0.5f;
            float before = Progress;
            Progress += ds;
            S = sNew;
            Lateral = path.Lateral(rig.BasePosition, S);
            int lapsNow = Mathf.FloorToInt(Progress / lap);
            if (lapsNow > Laps)
            {
                Laps = lapsNow;
                LastLapTime = Time.fixedTime - _lapStart;
                _lapStart = Time.fixedTime;
            }
            UpdateCarrot();
        }

        void UpdateCarrot()
        {
            if (path == null || command == null) return;
            command.SetTarget(path.Position(S + lookahead, lateralOffset));
        }
    }
}
