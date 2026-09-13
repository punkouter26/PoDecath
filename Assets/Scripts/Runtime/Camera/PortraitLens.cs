using Unity.Cinemachine;
using UnityEngine;

namespace PoDecath.Cam
{
    /// <summary>
    /// Holds a shot's framing as the angle it is actually composed for — the horizontal one — and solves
    /// the lens for whatever shape the screen turns out to be.
    ///
    /// Cinemachine's <c>Lens.FieldOfView</c>, like Unity's, is the <b>vertical</b> angle. On a landscape
    /// monitor that is a reasonable thing to author against, because the width comes out wider than the
    /// number. On a phone held upright it is the worst possible choice: the width comes out far narrower,
    /// and every shot in the gallery silently becomes a telephoto.
    ///
    /// Measured on the shipped gallery, portrait, with a 100 m field of eleven on the roof:
    ///
    /// <code>
    ///   shot         authored   9:16 horizontal   visible width at 25 m
    ///   Wide             52 v             30.7              13.7 m
    ///   Bend             42 v             24.4              10.8 m
    ///   StartLine        40 v             23.1              10.2 m
    ///   Rail             38 v             21.9               9.7 m
    ///   Finish           36 v             20.7               9.1 m
    ///   OffTheGun        34 v             19.5               8.6 m
    ///   HeadOn           32 v             18.3               8.1 m
    /// </code>
    ///
    /// The roof plan is 51 x 26 m and the straight alone is 22 m, so the <i>stadium wide</i> could not
    /// show the straight it was pointed at. At the finish of that race two athletes out of eleven were
    /// inside the frustum; the other nine were off the sides of the picture. On a 20:9 phone — which this
    /// project explicitly builds for, <c>PlayerSettings.Android.maxAspectRatio = 2.4</c> — every figure
    /// above shrinks by a further fifth.
    ///
    /// So author the width and derive the height:
    ///
    /// <code>
    ///   vertical = 2 * atan( tan(horizontal / 2) / aspect )
    /// </code>
    ///
    /// <see cref="maxVerticalFov"/> is the honest limit on that. A tall enough screen asks for a vertical
    /// angle that would fish-eye the whole frame, and past a point the right answer is "this screen cannot
    /// hold the shot" rather than a distorted picture of it. Where the clamp bites, the shot loses width
    /// again — that is a real trade, and it is a number worth nudging in the inspector against the phone
    /// rather than settling in code.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(CinemachineCamera))]
    [DefaultExecutionOrder(-50)]   // before the director raises a priority, so a cut is never one frame stale
    public class PortraitLens : MonoBehaviour
    {
        [Tooltip("How wide the shot is meant to be, in degrees across the screen. This is the number that "
               + "frames the shot; the vertical angle is solved from it and the live aspect ratio.")]
        [Range(5f, 140f)] public float horizontalFov = 45f;

        [Tooltip("Ceiling on the solved vertical angle. A very tall screen would otherwise ask for an "
               + "angle that fish-eyes the frame. Where this bites, the shot gives width back.")]
        [Range(20f, 120f)] public float maxVerticalFov = 80f;

        [Tooltip("Floor on the solved vertical angle, so a wide desktop window cannot flatten a shot to "
               + "nothing.")]
        [Range(5f, 60f)] public float minVerticalFov = 12f;

        CinemachineCamera _cam;
        float _lastAspect = -1f;
        float _lastHorizontal = -1f;

        /// <summary>The vertical angle this shot resolves to right now. Read by the scene sweep.</summary>
        public float SolvedVerticalFov { get; private set; }

        void OnEnable()
        {
            _cam = GetComponent<CinemachineCamera>();
            _lastAspect = -1f;
            Apply();
        }

        // LateUpdate, so a rotation or a resize is reflected on the frame it happens rather than the one
        // after it. The work is two trig calls behind an equality check, and only when something moved.
        void LateUpdate() => Apply();

        void Apply()
        {
            if (_cam == null) _cam = GetComponent<CinemachineCamera>();
            if (_cam == null) return;

            float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 0f;
            if (aspect <= 0f || float.IsNaN(aspect)) return;
            if (Mathf.Abs(aspect - _lastAspect) < 1e-4f && Mathf.Approximately(horizontalFov, _lastHorizontal)) return;
            _lastAspect = aspect;
            _lastHorizontal = horizontalFov;

            float h = Mathf.Clamp(horizontalFov, 1f, 175f) * Mathf.Deg2Rad;
            float v = 2f * Mathf.Atan(Mathf.Tan(h * 0.5f) / aspect) * Mathf.Rad2Deg;
            SolvedVerticalFov = Mathf.Clamp(v, minVerticalFov, Mathf.Max(minVerticalFov, maxVerticalFov));
            _cam.Lens.FieldOfView = SolvedVerticalFov;
        }

        /// <summary>
        /// The width this shot actually covers at a given range, on a given screen shape. The scene
        /// builders use it to sanity-check a shot against the thing it is pointed at, because "52 degrees"
        /// says nothing about whether a 22 m straight fits.
        /// </summary>
        public float WidthAt(float metres, float aspect)
        {
            float h = Mathf.Clamp(horizontalFov, 1f, 175f) * Mathf.Deg2Rad;
            float v = Mathf.Clamp(2f * Mathf.Atan(Mathf.Tan(h * 0.5f) / aspect) * Mathf.Rad2Deg,
                                  minVerticalFov, Mathf.Max(minVerticalFov, maxVerticalFov));
            float hEff = 2f * Mathf.Atan(Mathf.Tan(v * 0.5f * Mathf.Deg2Rad) * aspect);
            return 2f * metres * Mathf.Tan(hEff * 0.5f);
        }
    }
}
