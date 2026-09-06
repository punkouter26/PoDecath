using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using PoDecath.Cam;
using PoDecath.Sim;

namespace PoDecath.Env
{
    /// <summary>
    /// Racks focus onto whoever the gallery has cut to.
    ///
    /// A fixed focus distance is worse than none: the director's shots range from a two-metre close-up on
    /// the grid to a forty-metre stadium wide, so anything authored into the profile is wrong for most of
    /// the race. This measures the distance from the live camera to the athlete
    /// <see cref="BroadcastDirector.Featured"/> names and drives the volume's
    /// <see cref="DepthOfField.focusDistance"/> to it, damped, so the rack is something you see happen
    /// rather than a snap.
    ///
    /// The aperture opens and closes with the shot as well. A wide shot of the whole roof wants everything
    /// sharp; a lower-third close-up wants the city behind the athlete to go soft. That is the difference
    /// between a game camera and a long lens on a rail, and it costs one float per frame.
    ///
    /// Mobile has no depth of field pass at all (see <see cref="RenderTier.DepthOfField"/>), so this
    /// disables itself there rather than writing to an override nothing reads.
    /// </summary>
    [DefaultExecutionOrder(140)]
    public class CinematicFocus : MonoBehaviour
    {
        [Header("Wiring")]
        public Volume volume;
        public BroadcastDirector director;
        public RaceEvent race;
        [Tooltip("Left empty, the tagged main camera is used. Cinemachine drives one camera, so this is it.")]
        public Camera view;

        [Header("Rack")]
        [Tooltip("Seconds for the focus to travel most of the way to a new subject. A real focus puller "
               + "takes about this long on a hard rack.")]
        public float rackSeconds = 0.32f;
        [Tooltip("Never focus nearer than this; the athlete's own chest is the nearest thing that matters.")]
        public float minDistance = 1.6f;
        public float maxDistance = 180f;

        [Header("Aperture")]
        [Tooltip("Open (small f-number) on a framed individual: shallow, so the background falls away.")]
        public float tightAperture = 2.6f;
        [Tooltip("Stopped down on a wide: the whole roof stays readable.")]
        public float wideAperture = 11f;
        public float apertureSeconds = 0.5f;

        DepthOfField _dof;
        float _distance = 12f;
        float _aperture = 5.6f;
        bool _live;

        void OnEnable()
        {
            _live = RenderTier.DepthOfField && Resolve();
            if (!_live) enabled = false;
        }

        bool Resolve()
        {
            if (view == null) view = Camera.main;
            if (volume == null || volume.profile == null) return false;
            // profile, not sharedProfile: this writes per-frame and must not dirty the asset on disk.
            return volume.profile.TryGet(out _dof) && _dof != null;
        }

        void LateUpdate()
        {
            if (!_live || _dof == null || view == null) return;

            bool tight = director != null && director.OnIndividual;
            float target = Subject(out bool haveSubject);
            if (!haveSubject) target = _distance;   // nothing to focus on: hold, do not snap to the far plane

            float k = rackSeconds > 0f ? 1f - Mathf.Exp(-Time.deltaTime / rackSeconds) : 1f;
            _distance = Mathf.Lerp(_distance, Mathf.Clamp(target, minDistance, maxDistance), k);

            float aTarget = tight ? tightAperture : wideAperture;
            float ak = apertureSeconds > 0f ? 1f - Mathf.Exp(-Time.deltaTime / apertureSeconds) : 1f;
            _aperture = Mathf.Lerp(_aperture, aTarget, ak);

            _dof.focusDistance.Override(_distance);
            _dof.aperture.Override(_aperture);
        }

        /// <summary>
        /// How far the subject is from the lens, measured along the camera's forward axis rather than as a
        /// straight-line distance — depth of field is a depth-buffer effect, so an athlete off to the side
        /// of a wide shot must not pull focus towards the camera.
        /// </summary>
        float Subject(out bool found)
        {
            found = false;
            Vector3 p;
            if (director != null && director.Featured != null) p = Position(director.Featured);
            else if (race != null && race.Reference != null) p = Position(race.Reference);
            else return _distance;

            found = true;
            Transform t = view.transform;
            return Vector3.Dot(p - t.position, t.forward);
        }

        static Vector3 Position(RaceEvent.Athlete a)
        {
            Vector3 p = a.IsRL ? a.rig.BasePosition : (a.go != null ? a.go.transform.position : Vector3.zero);
            return p + Vector3.up * 0.9f;   // the head and chest, which is what a close-up is focused on
        }
    }
}
