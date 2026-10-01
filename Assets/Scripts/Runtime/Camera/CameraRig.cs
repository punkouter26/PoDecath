using UnityEngine;
using Unity.Cinemachine;

namespace PoDecath.Cam
{
    /// <summary>
    /// Two Cinemachine cameras framed for portrait: a chase view behind the creature and a side view.
    /// Toggle() swaps priorities; the CinemachineBrain on the main camera blends between them.
    ///
    /// The cameras do not follow the body itself. They follow a subject that travels at the body's averaged
    /// velocity and faces the way it is going, because the body's own root is a pelvis: it rises and falls
    /// with every stride and twists left and right with every step, and a chase camera locked to its yaw
    /// swung from side to side behind it (owner, 2026-10-01: "the camera is shaking too much").
    /// </summary>
    public class CameraRig : MonoBehaviour
    {
        public CinemachineCamera chaseCamera;
        public CinemachineCamera sideCamera;
        public int activeIndex = 0;
        [Tooltip("Seconds the followed body's velocity is averaged over, and the time the subject takes to "
               + "close on where the body really is. Long enough to take the stride out, short enough that "
               + "a stumble still moves the picture.")]
        public float followSmoothing = 0.35f;
        [Tooltip("Seconds over which the subject's heading turns to the direction of travel.")]
        public float headingSmoothing = 0.8f;

        public string ActiveName => activeIndex == 0 ? "Chase" : "Side";

        Transform _target, _subject;
        Vector3 _pos, _prev, _velocity;
        float _yaw;
        bool _snap;

        void Start() => Apply();

        public void SetTarget(Transform target)
        {
            _target = target;
            if (_subject == null)
            {
                _subject = new GameObject("CameraRig_Subject").transform;
                _subject.SetParent(transform, false);
            }
            _snap = true;
            Follow();
            if (chaseCamera != null) { chaseCamera.Follow = _subject; chaseCamera.LookAt = _subject; }
            if (sideCamera != null) { sideCamera.Follow = _subject; sideCamera.LookAt = _subject; }
        }

        void LateUpdate() => Follow();

        void Follow()
        {
            if (_target == null || _subject == null) return;
            Vector3 p = _target.position;
            float dt = Time.deltaTime;
            // A reset puts the body back on the grid in one step; that is a cut, not something to glide through.
            if (_snap || (p - _prev).sqrMagnitude > 4f)
            {
                _pos = _prev = p;
                _velocity = Vector3.zero;
                _yaw = YawOf(_target.rotation * Vector3.right);   // the rig's forward axis is +X
                _snap = false;
            }
            else if (dt > 1e-5f)
            {
                float k = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, followSmoothing));
                _velocity += ((p - _prev) / dt - _velocity) * k;
                _prev = p;
                _pos += _velocity * dt + (p - _pos) * k;

                // Facing follows the direction of travel once there is one; standing still it keeps what it had.
                Vector3 flat = new Vector3(_velocity.x, 0f, _velocity.z);
                if (flat.sqrMagnitude > 0.25f)
                    _yaw = Mathf.LerpAngle(_yaw, YawOf(flat), 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, headingSmoothing)));
            }
            _subject.SetPositionAndRotation(_pos, Quaternion.Euler(0f, _yaw, 0f));
        }

        /// <summary>Rotation about Y, in degrees, that turns +X onto a horizontal direction.</summary>
        static float YawOf(Vector3 direction) => Mathf.Atan2(-direction.z, direction.x) * Mathf.Rad2Deg;

        public void Toggle()
        {
            activeIndex = (activeIndex + 1) % 2;
            Apply();
        }

        void Apply()
        {
            if (chaseCamera != null) chaseCamera.Priority = activeIndex == 0 ? 20 : 10;
            if (sideCamera != null) sideCamera.Priority = activeIndex == 1 ? 20 : 10;
        }
    }
}
