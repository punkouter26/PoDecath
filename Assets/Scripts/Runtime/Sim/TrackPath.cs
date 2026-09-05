using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Stadium-loop centre line matching KartTrackBuilder and training/envs/run_track.py.
    /// Local frame: this transform sits at the loop centre with +X along the straights. The +Z straight is the
    /// dash straight and travel runs +X on it, then clockwise (seen from above) around the +X end.
    /// Arc length s is in metres, wrapping at LapLength.
    /// </summary>
    public class TrackPath : MonoBehaviour
    {
        public float halfLength = 11.2f;
        public float radius = 8.8f;
        public float deckWidth = 5.3f;
        public float deckTopY = 24f;

        public float StraightLength => 2f * halfLength;
        public float ArcLength => Mathf.PI * radius;
        public float LapLength => 2f * StraightLength + 2f * ArcLength;

        /// <summary>Local (x, z) centre-line point and unit tangent at arc length s.</summary>
        public void Local(float s, out Vector2 pos, out Vector2 tan)
        {
            float L = StraightLength, A = ArcLength, R = radius, hl = halfLength;
            s = Mathf.Repeat(s, LapLength);
            if (s < L) { pos = new Vector2(-hl + s, R); tan = new Vector2(1f, 0f); return; }
            if (s < L + A)
            {
                float th = Mathf.PI * 0.5f - (s - L) / R;
                pos = new Vector2(hl + R * Mathf.Cos(th), R * Mathf.Sin(th));
                tan = new Vector2(Mathf.Sin(th), -Mathf.Cos(th));
                return;
            }
            if (s < 2f * L + A) { pos = new Vector2(hl - (s - L - A), -R); tan = new Vector2(-1f, 0f); return; }
            float t2 = -Mathf.PI * 0.5f - (s - 2f * L - A) / R;
            pos = new Vector2(-hl + R * Mathf.Cos(t2), R * Mathf.Sin(t2));
            tan = new Vector2(Mathf.Sin(t2), -Mathf.Cos(t2));
        }

        /// <summary>World position on the deck surface at arc length s, offset laterally (positive = left of travel).</summary>
        public Vector3 Position(float s, float lateral = 0f)
        {
            Local(s, out Vector2 p, out Vector2 t);
            Vector2 n = new Vector2(-t.y, t.x);
            Vector2 q = p + n * lateral;
            Vector3 w = transform.TransformPoint(new Vector3(q.x, 0f, q.y));
            w.y = deckTopY;
            return w;
        }

        public Vector3 Tangent(float s)
        {
            Local(s, out _, out Vector2 t);
            return transform.TransformDirection(new Vector3(t.x, 0f, t.y)).normalized;
        }

        /// <summary>Signed lateral offset of a world point from the centre line at arc length s.</summary>
        public float Lateral(Vector3 world, float s)
        {
            Local(s, out Vector2 p, out Vector2 t);
            Vector3 l = transform.InverseTransformPoint(world);
            Vector2 d = new Vector2(l.x, l.z) - p;
            return -t.y * d.x + t.x * d.y;
        }

        /// <summary>Nearest arc length to a world point, searched in a window around a previous estimate.</summary>
        public float Project(Vector3 world, float sPrev, float back = 2f, float forward = 4f, int samples = 25)
        {
            Vector3 l = transform.InverseTransformPoint(world);
            Vector2 xz = new Vector2(l.x, l.z);
            float best = sPrev, bestD = float.MaxValue;
            for (int i = 0; i < samples; i++)
            {
                float s = sPrev - back + (back + forward) * i / (samples - 1);
                Local(s, out Vector2 p, out _);
                float d = (p - xz).sqrMagnitude;
                if (d < bestD) { bestD = d; best = s; }
            }
            return Mathf.Repeat(best, LapLength);
        }

        /// <summary>Global nearest arc length (coarse scan), for initial placement.</summary>
        public float ProjectGlobal(Vector3 world)
        {
            float best = 0f, bestD = float.MaxValue;
            Vector3 l = transform.InverseTransformPoint(world);
            Vector2 xz = new Vector2(l.x, l.z);
            for (float s = 0f; s < LapLength; s += 0.5f)
            {
                Local(s, out Vector2 p, out _);
                float d = (p - xz).sqrMagnitude;
                if (d < bestD) { bestD = d; best = s; }
            }
            return best;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            Vector3 prev = Position(0f);
            for (float s = 1f; s <= LapLength + 0.01f; s += 1f)
            {
                Vector3 p = Position(s);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
        }
    }
}
