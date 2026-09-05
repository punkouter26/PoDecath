using System;
using UnityEngine;
using PoDecath.Sim;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace PoDecath.UI
{
    /// <summary>
    /// Optional touch-flick perturbation. A quick swipe inside the middle band of the portrait
    /// screen (between the status card and the control bar) applies a horizontal impulse to the
    /// creature's base, in the direction of the swipe relative to the camera.
    /// </summary>
    public class TouchPerturbation : MonoBehaviour
    {
        public CreatureRig rig;
        public Camera cam;
        [Tooltip("Impulse (N s) per screen-pixel of swipe length, scaled by screen height / 1920.")]
        public float impulsePerPixel = 0.006f;
        public float maxImpulse = 5f;
        public float minFlickPixels = 40f;
        public float maxFlickSeconds = 0.5f;
        [Tooltip("Active vertical band as a fraction of screen height (bottom, top).")]
        public Vector2 activeBand = new Vector2(0.20f, 0.78f);

        public event Action<Vector3> Flicked;
        public Vector3 LastImpulse { get; private set; }

        Vector2 _start;
        float _startTime;
        bool _tracking;

        void Update()
        {
            if (!ReadPointer(out Vector2 pos, out bool pressedThisFrame, out bool releasedThisFrame)) return;

            if (pressedThisFrame)
            {
                float f = pos.y / Mathf.Max(1, Screen.height);
                if (f >= activeBand.x && f <= activeBand.y)
                {
                    _tracking = true;
                    _start = pos;
                    _startTime = Time.unscaledTime;
                }
            }
            else if (releasedThisFrame && _tracking)
            {
                _tracking = false;
                Vector2 delta = pos - _start;
                float dt = Time.unscaledTime - _startTime;
                if (delta.magnitude >= minFlickPixels && dt <= maxFlickSeconds) Apply(delta);
            }
        }

        void Apply(Vector2 screenDelta)
        {
            if (rig == null) return;
            Camera c = cam != null ? cam : Camera.main;
            Vector3 right = c != null ? c.transform.right : Vector3.forward;
            Vector3 fwd = c != null ? c.transform.forward : Vector3.right;
            right.y = 0f; fwd.y = 0f;
            if (right.sqrMagnitude < 1e-4f) right = Vector3.forward;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.right;
            right.Normalize(); fwd.Normalize();

            float scale = impulsePerPixel * (Screen.height / 1920f);
            Vector3 impulse = (right * screenDelta.x + fwd * screenDelta.y) * scale;
            impulse = Vector3.ClampMagnitude(impulse, maxImpulse);
            rig.ApplyImpulseToBase(impulse);
            LastImpulse = impulse;
            Flicked?.Invoke(impulse);
        }

        static bool ReadPointer(out Vector2 pos, out bool pressed, out bool released)
        {
#if ENABLE_INPUT_SYSTEM
            var p = Pointer.current;
            if (p == null) { pos = default; pressed = released = false; return false; }
            pos = p.position.ReadValue();
            pressed = p.press.wasPressedThisFrame;
            released = p.press.wasReleasedThisFrame;
            return true;
#else
            if (Input.touchCount > 0)
            {
                Touch t = Input.GetTouch(0);
                pos = t.position;
                pressed = t.phase == TouchPhase.Began;
                released = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
                return true;
            }
            pos = Input.mousePosition;
            pressed = Input.GetMouseButtonDown(0);
            released = Input.GetMouseButtonUp(0);
            return true;
#endif
        }
    }
}
