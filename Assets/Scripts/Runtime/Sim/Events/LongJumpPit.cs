using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Runway and sand pit laid out inside the rooftop loop, matching <c>LongJumpBuilder</c>.
    ///
    /// Local frame: +X along the straights and +Z across them, like <see cref="TrackPath"/>, but the
    /// origin sits on the runway's own centre line, which the builder offsets from the loop's so the
    /// White House flagpole (at the loop centre) is clear of the run-up.
    /// Every distance below is a local x, increasing in the direction of the run-up. The measured
    /// distance of a jump is taken from <see cref="takeoffX"/> (the far edge of the take-off board,
    /// i.e. the foul line), which is how the real event measures regardless of where the foot landed.
    /// </summary>
    public class LongJumpPit : MonoBehaviour
    {
        [Header("Layout (local x, metres)")]
        [Tooltip("Where the runway surface starts.")]
        public float runwayStartX = -15.75f;
        [Tooltip("Foul line: the far edge of the take-off board. All jumps are measured from here.")]
        public float takeoffX = 2.2f;
        [Tooltip("Depth of the take-off board along the runway; its far edge is the foul line.")]
        public float boardDepth = 0.2f;
        public float pitNearX = 3.2f;
        public float pitFarX = 11.2f;

        [Header("Widths (metres)")]
        public float runwayWidth = 1.22f;
        public float pitWidth = 2.75f;

        [Header("Heights (world Y)")]
        [Tooltip("Top of the infield deck — the surface the run-up happens on.")]
        public float surfaceY = 24f;
        [Tooltip("Top of the sand, which is recessed below the deck.")]
        public float sandY = 23.7f;

        public float RunwayLength => takeoffX - runwayStartX;
        public float PitLength => pitFarX - pitNearX;
        public float HalfRunway => runwayWidth * 0.5f;
        public float HalfPit => pitWidth * 0.5f;

        /// <summary>Unit vector the run-up travels along, in world space.</summary>
        public Vector3 Direction => transform.TransformDirection(Vector3.right).normalized;

        /// <summary>Local x of a world point — how far along the runway it is.</summary>
        public float Along(Vector3 world) => transform.InverseTransformPoint(world).x;

        /// <summary>Local z of a world point — how far it has drifted off the centre line.</summary>
        public float Lateral(Vector3 world) => transform.InverseTransformPoint(world).z;

        /// <summary>Metres in front of the foul line, which is what a jump is scored on.</summary>
        public float Measure(Vector3 world) => Along(world) - takeoffX;

        /// <summary>World point on the deck surface at local x, offset across the runway.</summary>
        public Vector3 Point(float x, float lateral = 0f)
        {
            Vector3 w = transform.TransformPoint(new Vector3(x, 0f, lateral));
            w.y = surfaceY;
            return w;
        }

        /// <summary>World point on the sand at local x, offset across the pit.</summary>
        public Vector3 SandPoint(float x, float lateral = 0f)
        {
            Vector3 w = Point(x, lateral);
            w.y = sandY;
            return w;
        }

        /// <summary>True while a world point is over the sand, so a landing can be scored.</summary>
        public bool OverPit(Vector3 world)
        {
            Vector3 l = transform.InverseTransformPoint(world);
            return l.x >= pitNearX && l.x <= pitFarX && Mathf.Abs(l.z) <= HalfPit;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.95f, 0.4f, 0.2f);
            DrawRect(runwayStartX, takeoffX, HalfRunway, surfaceY);
            Gizmos.color = Color.yellow;
            DrawRect(pitNearX, pitFarX, HalfPit, sandY);
            Gizmos.color = Color.white;
            Gizmos.DrawLine(Point(takeoffX, -HalfRunway), Point(takeoffX, HalfRunway));
        }

        void DrawRect(float x0, float x1, float halfZ, float y)
        {
            Vector3 a = Point(x0, -halfZ), b = Point(x1, -halfZ), c = Point(x1, halfZ), d = Point(x0, halfZ);
            a.y = b.y = c.y = d.y = y;
            Gizmos.DrawLine(a, b); Gizmos.DrawLine(b, c); Gizmos.DrawLine(c, d); Gizmos.DrawLine(d, a);
        }
    }
}
