using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Perturbation harness for comparing policies: once per attempt, when the reference athlete has run
    /// far enough to be at steady-state gait, shoves every RL athlete sideways with the same impulse.
    /// The impulse is expressed as a whole-body delta-v so it is rig-mass independent, and it steps up
    /// every <see cref="trialsPerLevel"/> attempts, so the log shows the delta-v each policy survives.
    /// Created at runtime by the comparison tooling; not part of a built scene.
    /// </summary>
    [DefaultExecutionOrder(-20)]
    public class PushTester : MonoBehaviour
    {
        public float pushAtDistance = 35f;
        public float[] deltaVLevels = { 0.75f, 1.0f, 1.25f, 1.5f, 1.75f, 2.0f, 2.5f, 3.0f };
        public int trialsPerLevel = 4;

        DashEvent _dash;
        int _lastAttempt = -1;
        int _pushedAttempts;
        bool _pushedThisAttempt;

        void Awake() => _dash = FindFirstObjectByType<DashEvent>();

        void FixedUpdate()
        {
            if (_dash == null) return;
            if (_dash.Attempt != _lastAttempt) { _lastAttempt = _dash.Attempt; _pushedThisAttempt = false; }
            if (_pushedThisAttempt || _dash.Current != DashEvent.Phase.Running) return;

            // Wait until everyone is up to speed and past the first bend.
            foreach (var a in _dash.Athletes)
                if (a.IsRL && !a.fell && !a.finished && a.distance < pushAtDistance) return;

            int level = Mathf.Min(_pushedAttempts / Mathf.Max(1, trialsPerLevel), deltaVLevels.Length - 1);
            float dv = deltaVLevels[level];
            // Alternate the side so a policy that only leans one way cannot fluke the whole sweep.
            float side = (_pushedAttempts % 2 == 0) ? 1f : -1f;

            foreach (var a in _dash.Athletes)
            {
                if (!a.IsRL || a.fell || a.finished) continue;
                float mass = 0f;
                foreach (var ab in a.rig.GetComponentsInChildren<ArticulationBody>()) mass += ab.mass;
                Vector3 lateral = Vector3.Cross(Vector3.up, a.rig.BaseForward).normalized * side;
                a.rig.ApplyImpulseToBase(lateral * dv * mass);
            }
            _pushedThisAttempt = true;
            _pushedAttempts++;
            Debug.Log($"[Push] attempt {_dash.Attempt + 1}: dv {dv:F2} m/s side {(side > 0 ? "+" : "-")}");
        }
    }
}
