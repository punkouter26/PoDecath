using UnityEngine;
using Unity.Cinemachine;
using PoDecath.Env;

namespace PoDecath.Cam
{
    /// <summary>
    /// The camera flinching at things that actually hit hard.
    ///
    /// A broadcast camera does not shake because something happened; it shakes because something happened
    /// *near it*, and it shakes in proportion. Cinemachine's impulse system already models exactly that —
    /// an impulse is emitted at a world point and every listening camera feels it scaled by its own
    /// distance from it — so the only thing this class adds is a single pooled source that anything in the
    /// game can fire without holding a reference, and a scale factor that turns a contact impulse in
    /// newton-seconds into a shake amplitude.
    ///
    /// Every amplitude here is driven by a measured impulse rather than by a constant. A shin brushing a
    /// hurdle and an athlete demolishing one produce collision impulses an order of magnitude apart, and
    /// before this they produced the same effect; the point of the feature is that they no longer do.
    ///
    /// The source is moved to the impact point before each fire. <c>GenerateImpulseAtPositionWithVelocity</c>
    /// would do the same thing without the transform move, and is used here for exactly that reason — the
    /// move is only kept for anything inspecting the object in the hierarchy.
    /// </summary>
    [DefaultExecutionOrder(105)]
    public class CameraShake : MonoBehaviour
    {
        [Tooltip("Metres beyond which an impulse is no longer felt. The broadcast cameras sit 8-40 m out, "
               + "so a fall on the far straight should barely register on a shot of the near one.")]
        public float range = 34f;

        [Tooltip("How long one shake lasts. Short: this is a knock, not an earthquake.")]
        public float duration = 0.28f;

        [Tooltip("Metres per second of camera velocity per unit of strength. Strength is normalised 0..1 by "
               + "the caller, so this is the amplitude of the biggest shake the game can produce.")]
        public float metresPerSecond = 0.55f;

        [Tooltip("Shakes weaker than this are dropped rather than played, so a field of sixteen scuffing "
               + "hurdles does not leave the camera permanently trembling.")]
        public float minStrength = 0.08f;

        [Tooltip("Shortest gap between two shakes. Without it a single fall — which is a dozen contacts "
               + "over a few frames — fires a dozen overlapping impulses and reads as a camera fault.")]
        public float minInterval = 0.12f;

        static CameraShake _instance;
        CinemachineImpulseSource _source;
        float _last = -999f;

        /// <summary>The shaker in the current scene, if a race built one.</summary>
        public static CameraShake Instance => _instance;

        void Awake()
        {
            _instance = this;
            _source = gameObject.AddComponent<CinemachineImpulseSource>();

            CinemachineImpulseDefinition def = _source.ImpulseDefinition;
            // Dissipating, not Legacy. Legacy is the package default and it needs a SignalSourceAsset to
            // produce anything at all; with none assigned it emits silence, which looks exactly like a
            // feature that is wired up and simply does not work.
            def.ImpulseType = CinemachineImpulseDefinition.ImpulseTypes.Dissipating;
            def.ImpulseShape = CinemachineImpulseDefinition.ImpulseShapes.Bump;
            def.ImpulseDuration = duration;
            def.DissipationDistance = range;
            def.DissipationRate = 0.25f;
            def.ImpulseChannel = 1;   // matches the listeners' default mask
        }

        void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        /// <summary>
        /// Shakes every listening camera as if something of the given strength happened at a world point.
        /// <paramref name="strength"/> is 0..1; callers normalise their own impulse against a reference
        /// they can justify, so that one scale here covers a footfall and a wrecked hurdle.
        ///
        /// Returns quietly when no shaker exists, so a development scene that never built one is not a
        /// special case at every call site.
        /// </summary>
        public static void Shake(Vector3 at, float strength, Vector3 direction = default)
        {
            CameraShake shake = _instance;
            if (shake == null) return;
            shake.Fire(at, strength, direction);
        }

        void Fire(Vector3 at, float strength, Vector3 direction)
        {
            if (_source == null) return;
            strength = Mathf.Clamp01(strength);
            if (strength < minStrength) return;
            if (Time.unscaledTime - _last < minInterval) return;
            _last = Time.unscaledTime;

            // Straight down unless the caller had a direction in mind: a body or a hurdle hitting the deck
            // is the common case, and it drives the camera the way the deck was driven.
            Vector3 dir = direction.sqrMagnitude > 1e-4f ? direction.normalized : Vector3.down;
            transform.position = at;
            _source.GenerateImpulseAtPositionWithVelocity(at, dir * strength * metresPerSecond);
        }

        /// <summary>
        /// Gives one camera the ability to feel impulses. Called by the scene builder for each shot in the
        /// gallery; a camera without a listener is simply never shaken.
        /// </summary>
        public static CinemachineImpulseListener AddListener(CinemachineCamera cam)
        {
            if (cam == null) return null;
            var listener = cam.gameObject.AddComponent<CinemachineImpulseListener>();
            listener.ApplyAfter = CinemachineCore.Stage.Noise;
            listener.ChannelMask = 1;
            // The mobile tier is running at 0.8 render scale on a scene already well over its triangle
            // budget; a softer shake there costs nothing and keeps a small screen readable.
            listener.Gain = RenderTier.IsMobile ? 0.6f : 1f;
            listener.Use2DDistance = false;
            listener.UseCameraSpace = true;
            return listener;
        }
    }
}
