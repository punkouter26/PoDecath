using UnityEngine;
using PoDecath.Audio;
using PoDecath.Env;
using PoDecath.Sim;

namespace PoDecath.Fx
{
    /// <summary>
    /// A puff of dust under every footfall.
    ///
    /// It listens to <see cref="FootstepAudio.Stepped"/> rather than watching the contact sensors itself:
    /// working out what counts as a step — the rising edge of a real contact, above a force threshold,
    /// outside a refractory window — is fiddly enough that having two components disagree about it would
    /// show, with dust and sound landing on different frames.
    ///
    /// Not every step earns a puff. A field of sixteen at four steps a second each is sixty-four bursts a
    /// second, which is a haze rather than a detail, so light steps are skipped and the whole thing turns
    /// itself off when the camera is too far away to resolve it.
    /// </summary>
    [DefaultExecutionOrder(80)]
    public class FootstepDust : MonoBehaviour
    {
        [Tooltip("Where the steps come from. Found on this object if not set.")]
        public FootstepAudio steps;

        [Header("Budget")]
        [Tooltip("Steps below this weight leave nothing behind; a runner at pace plants hard enough to.")]
        public float minWeight = 0.35f;
        [Tooltip("Beyond this the puff is a few pixels and not worth the draw call.")]
        public float maxCameraDistance = 40f;
        [Tooltip("Shortest gap between two puffs from this athlete, whatever its feet are doing.")]
        public float minInterval = 0.1f;

        [Header("Slip")]
        [Tooltip("A foot sliding this fast along the deck throws dust along the slide as well as under the "
               + "step. It is the visual half of what SkidMarks writes on the asphalt.")]
        public float minSlipSpeed = 2f;
        [Tooltip("Slip speed that throws a full puff.")]
        public float referenceSlipSpeed = 6f;
        [Tooltip("Shortest gap between two slip puffs from this athlete.")]
        public float slipInterval = 0.14f;

        float _last = -1f;
        float _lastSlip = -1f;
        Camera _view;

        void Awake()
        {
            if (steps == null) steps = GetComponent<FootstepAudio>();
            if (steps != null) steps.Stepped += OnStep;
            // Mobile keeps the effect but on a much shorter leash: the burst itself is already a third the
            // size there, and this stops sixteen athletes each asking for one every quarter second.
            if (RenderTier.IsMobile) { minWeight = Mathf.Max(minWeight, 0.55f); maxCameraDistance = 22f; }
        }

        void OnDestroy()
        {
            if (steps != null) steps.Stepped -= OnStep;
        }

        /// <summary>
        /// Dust off a foot that is sliding rather than gripping.
        ///
        /// Not driven off <see cref="FootstepAudio.Stepped"/> like the rest of this component, because a
        /// skid is not a step: it happens between plants, it lasts as long as the foot keeps sliding, and
        /// the event that reports steps deliberately fires once on the rising edge of a contact. The
        /// contact sensors already measure the slide for the physics, so this reads them directly.
        /// </summary>
        void FixedUpdate()
        {
            if (steps == null || steps.rig == null || steps.rig.feet == null) return;
            if (Time.fixedTime - _lastSlip < slipInterval) return;

            foreach (FootContactSensor f in steps.rig.feet)
            {
                if (f == null || !f.InContact || f.SlipSpeed < minSlipSpeed) continue;
                if (!InRange(f.Point)) return;

                _lastSlip = Time.fixedTime;
                float weight = Mathf.Clamp01(f.SlipSpeed / Mathf.Max(0.01f, referenceSlipSpeed));
                // Along the slide and low: dust thrown by a scrubbing foot goes the way the foot is going,
                // not the way the runner is facing. On a bend those are not the same thing, which is
                // exactly when it is worth seeing.
                VfxLibrary.Play(VfxLibrary.Effect.FootDust, f.Point, f.SlipDirection + Vector3.up * 0.35f, weight);
                return;
            }
        }

        bool InRange(Vector3 at)
        {
            if (_view == null) _view = Camera.main;
            if (_view == null) return true;
            return (at - _view.transform.position).sqrMagnitude <= maxCameraDistance * maxCameraDistance;
        }

        void OnStep(Vector3 at, float weight)
        {
            if (weight < minWeight) return;
            if (Time.time - _last < minInterval) return;

            if (_view == null) _view = Camera.main;
            if (_view != null && (at - _view.transform.position).sqrMagnitude > maxCameraDistance * maxCameraDistance) return;

            _last = Time.time;
            // Backwards and slightly up: a foot pushing off throws what it lifts behind the runner.
            Vector3 back = -transform.forward;
            VfxLibrary.Play(VfxLibrary.Effect.FootDust, at, back * 0.6f + Vector3.up, weight);
        }
    }
}
