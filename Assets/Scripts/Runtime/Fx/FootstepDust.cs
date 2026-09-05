using UnityEngine;
using PoDecath.Audio;
using PoDecath.Env;

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

        float _last = -1f;
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
