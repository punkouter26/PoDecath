using UnityEngine;
using PoDecath.Env;
using PoDecath.Sim;

namespace PoDecath.Fx
{
    /// <summary>
    /// A ribbon behind one athlete, in that athlete's own colour.
    ///
    /// The house rule is that athletes keep the textures they were imported with, so the field cannot be
    /// told apart by skin. A trail solves the same problem from the other side: it says who is who without
    /// touching the model, and it does something the colour never could — it shows the line a runner took
    /// through a bend, which is most of what there is to watch on a 100 m loop with an 8.8 m radius.
    ///
    /// It is a speed cue as much as an identity one. The ribbon only draws above <see cref="minSpeed"/>
    /// and its width tracks how fast the athlete is going, so a field strung out down the straight reads
    /// as a field strung out, and a runner who has gone down stops drawing one immediately.
    /// </summary>
    [DefaultExecutionOrder(70)]
    public class AthleteTrail : MonoBehaviour
    {
        [Header("Wiring")]
        [Tooltip("Set for a physics athlete; the ribbon hangs off the articulation root.")]
        public CreatureRig rig;
        [Tooltip("Set for the kinematic bot instead.")]
        public HeuristicRunner heuristic;
        public Material material;

        [Header("Look")]
        public Color color = Color.white;
        [Tooltip("Below this the ribbon is not drawn; a runner on the grid should not trail.")]
        public float minSpeed = 1.6f;
        [Tooltip("Speed at which the ribbon is at full width.")]
        public float referenceSpeed = 8f;
        public float maxWidth = 0.13f;
        [Tooltip("Height above the athlete's own origin, which for a physics athlete is the pelvis — about "
               + "0.95 m off the deck. That is the height that reads as a line through the bend; higher and "
               + "the ribbon sits across the athlete's head, lower and it disappears into the deck on the "
               + "outside of a curve.")]
        public float height = 0f;

        TrailRenderer _trail;
        float _width;

        void Start()
        {
            var go = new GameObject("Trail");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.up * height;

            _trail = go.AddComponent<TrailRenderer>();
            _trail.time = RenderTier.TrailSeconds;
            _trail.minVertexDistance = 0.12f;
            _trail.autodestruct = false;
            _trail.emitting = false;
            _trail.numCapVertices = 2;
            _trail.alignment = LineAlignment.View;
            _trail.textureMode = LineTextureMode.Stretch;
            _trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _trail.receiveShadows = false;
            if (material != null) _trail.sharedMaterial = material;

            // Tapered to nothing at the tail, and fading with it, so the ribbon reads as motion rather
            // than as a solid tube stuck to the athlete's back.
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
                new[] { new GradientAlphaKey(0.7f, 0f), new GradientAlphaKey(0f, 1f) });
            _trail.colorGradient = gradient;
            _trail.widthCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0.05f);
            _trail.widthMultiplier = 0f;
        }

        void LateUpdate()
        {
            if (_trail == null) return;

            float speed = Speed();
            bool down = rig != null && rig.UprightDot < 0.4f;
            bool show = !down && speed >= minSpeed;

            // Eased rather than switched: a ribbon that appears at full width on the frame a runner crosses
            // the threshold pops, and popping is the one thing a trail must never do.
            float target = show ? maxWidth * Mathf.Clamp01(speed / Mathf.Max(1f, referenceSpeed)) : 0f;
            _width = Mathf.MoveTowards(_width, target, maxWidth * 4f * Time.deltaTime);
            _trail.widthMultiplier = _width;
            _trail.emitting = _width > 0.001f;
        }

        float Speed()
        {
            if (rig != null) return rig.BaseLinearVelocityWorld.magnitude;
            if (heuristic != null) return heuristic.Speed;
            return 0f;
        }

        /// <summary>Drops the ribbon immediately, for a restart that teleports the athlete back to the grid.</summary>
        public void Clear()
        {
            if (_trail == null) return;
            _trail.Clear();
            _width = 0f;
            _trail.widthMultiplier = 0f;
        }
    }
}
