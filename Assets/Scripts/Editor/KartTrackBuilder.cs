using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// ProBuilder go-kart race track ON the White House roof. A raised deck loop (two straights joined by
    /// semicircles), sized to cover a target fraction of the RESIDENCE roof only, so the wings stay clear.
    /// It stands on legs ray-cast down onto whatever roof surface is beneath each support point.
    /// The loop is solved so centre-line lap x deck width equals coverage x roof plan area; the dash lane
    /// then takes whatever the resulting straight can hold. Static ProBuilder geometry with colliders.
    /// </summary>
    public static class KartTrackBuilder
    {
        public struct TrackInfo
        {
            public float deckTopY;
            public Vector3 straightStart;
            public Vector3 straightEnd;
            public Vector3 center;
            public float halfLength;
            public float radius;
            public float width;
            public float dashLength;      // usable straight, metres
            public float lapLength;       // centre-line lap, metres
            public float laneSpacing;     // dash lane pitch that fits the deck
            public float coverage;        // achieved fraction of the roof plan
            public Transform root;
        }

        internal const int CurveSegments = 24;

        /// <param name="roofPlan">Flat roof rectangle to build on, in metres.</param>
        /// <param name="roofCentreXZ">World centre of that rectangle.</param>
        /// <param name="coverage">Target ribbon area as a fraction of the roof plan.</param>
        public static TrackInfo Build(Transform parent, Bounds building, Material asphalt, Material barrier, Material line, Material column,
            Vector2 roofPlan, Vector2 roofCentreXZ, float coverage = 0.40f, float deckWidth = 5.3f, float edgeMargin = 1.5f)
        {
            bool alongX = roofPlan.x >= roofPlan.y;
            var root = new GameObject("KartTrack");
            if (parent != null) root.transform.SetParent(parent, false);
            root.transform.position = new Vector3(roofCentreXZ.x, 0f, roofCentreXZ.y);
            root.transform.rotation = alongX ? Quaternion.identity : Quaternion.Euler(0f, 90f, 0f);

            float L = alongX ? roofPlan.x : roofPlan.y;               // roof length along the straights
            float W = alongX ? roofPlan.y : roofPlan.x;               // roof width across the loop
            float halfW = deckWidth * 0.5f;

            // Solve the stadium so (4*halfLen + 2*pi*R) * deckWidth == coverage * roof area.
            // R takes the width the roof can spare; halfLen then carries the remaining area.
            float roofArea = roofPlan.x * roofPlan.y;
            float targetArea = coverage * roofArea;
            float R = Mathf.Max(4f, (W - 2f * edgeMargin - deckWidth) * 0.5f);
            float maxHalfLen = (L - 2f * edgeMargin) * 0.5f - R;
            float halfLen = (targetArea / deckWidth - 2f * Mathf.PI * R) * 0.25f;
            halfLen = Mathf.Clamp(halfLen, R * 0.30f, Mathf.Max(R * 0.30f, maxHalfLen));

            float lap = 4f * halfLen + 2f * Mathf.PI * R;
            float achieved = (lap * deckWidth) / roofArea;
            float dashLength = Mathf.Max(6f, 2f * halfLen - 2f);      // the straight, less end margins
            float laneSpacing = (deckWidth - 1.0f) * 0.25f;           // five lanes across the deck
            Debug.Log($"[KartTrack] roof {roofPlan.x:F1} x {roofPlan.y:F1} m ({roofArea:F0} m2) | " +
                      $"R {R:F1} m, straight {2f * halfLen:F1} m, deck {deckWidth:F1} m | " +
                      $"lap {lap:F1} m, ribbon {lap * deckWidth:F0} m2 = {achieved * 100f:F1}% of roof | " +
                      $"dash {dashLength:F1} m at {laneSpacing:F2} m lanes");

            float thick = 0.5f;
            float top = building.max.y + 0.9f;                          // clear of the chimney tops
            float deckY = top - thick * 0.5f;
            float barrierH = 0.8f, barrierT = 0.25f;

            var deck = new GameObject("Deck").transform; deck.SetParent(root.transform, false);
            var rails = new GameObject("Barriers").transform; rails.SetParent(root.transform, false);
            var legs = new GameObject("Legs").transform; legs.SetParent(root.transform, false);
            var marks = new GameObject("Markings").transform; marks.SetParent(root.transform, false);

            // Straights (+Z side is the dash straight)
            foreach (float sz in new[] { 1f, -1f })
            {
                Box(deck, sz > 0 ? "Straight_Dash" : "Straight_Back", new Vector3(0f, deckY, sz * R), new Vector3(2f * halfLen, thick, deckWidth), Quaternion.identity, asphalt);
                Box(rails, "Rail_Outer", new Vector3(0f, top + barrierH * 0.5f, sz * (R + halfW - barrierT * 0.5f)), new Vector3(2f * halfLen, barrierH, barrierT), Quaternion.identity, barrier);
                Box(rails, "Rail_Inner", new Vector3(0f, top + barrierH * 0.5f, sz * (R - halfW + barrierT * 0.5f)), new Vector3(2f * halfLen, barrierH, barrierT), Quaternion.identity, barrier);
            }

            // Semicircular ends: true annular wedges (rotated boxes leave gaps on the outer edge)
            foreach (float sx in new[] { 1f, -1f })
            {
                Vector3 c = new Vector3(sx * halfLen, 0f, 0f);
                float dTheta = Mathf.PI / CurveSegments;
                for (int i = 0; i < CurveSegments; i++)
                {
                    float t0 = Mathf.PI * 0.5f - i * dTheta;
                    float t1 = t0 - dTheta;
                    string tag = sx > 0 ? "E" : "W";
                    Wedge(deck, $"Curve_{tag}_{i}", c, sx, R - halfW, R + halfW, t0, t1, deckY - thick * 0.5f, deckY + thick * 0.5f, asphalt);
                    Wedge(rails, $"Rail_Outer_{tag}_{i}", c, sx, R + halfW - barrierT, R + halfW, t0, t1, top, top + barrierH, barrier);
                    Wedge(rails, $"Rail_Inner_{tag}_{i}", c, sx, R - halfW, R - halfW + barrierT, t0, t1, top, top + barrierH, barrier);
                }
            }

            // Legs down onto whatever roof is below each support point
            Physics.SyncTransforms();
            float deckBottom = deckY - thick * 0.5f;
            for (float x = -halfLen; x <= halfLen + 0.01f; x += 5f)
                foreach (float sz in new[] { 1f, -1f })
                {
                    Leg(legs, root.transform, new Vector3(x, deckBottom, sz * (R - halfW + 0.6f)), column);
                    Leg(legs, root.transform, new Vector3(x, deckBottom, sz * (R + halfW - 0.6f)), column);
                }
            foreach (float sx in new[] { 1f, -1f })
                for (int i = 1; i < 8; i++)
                {
                    float theta = Mathf.PI * 0.5f - i * (Mathf.PI / 8f);
                    Vector3 radial = new Vector3(sx * Mathf.Cos(theta), 0f, Mathf.Sin(theta));
                    Leg(legs, root.transform, new Vector3(sx * halfLen, deckBottom, 0f) + radial * (R - halfW + 0.6f), column);
                    Leg(legs, root.transform, new Vector3(sx * halfLen, deckBottom, 0f) + radial * (R + halfW - 0.6f), column);
                }

            // Dash markings on the +Z straight (runs toward +X), symmetric about the deck centre
            float x0 = -dashLength * 0.5f, x1 = dashLength * 0.5f;
            Box(marks, "StartLine", new Vector3(x0, top + 0.01f, R), new Vector3(0.2f, 0.02f, deckWidth - 0.7f), Quaternion.identity, line);
            Box(marks, "FinishLine", new Vector3(x1, top + 0.01f, R), new Vector3(0.2f, 0.02f, deckWidth - 0.7f), Quaternion.identity, line);
            int step = dashLength >= 40f ? 10 : 5;
            for (int m = step; m < dashLength - 0.5f; m += step)
                Box(marks, $"Mark_{m}m", new Vector3(x0 + m, top + 0.01f, R + halfW - 0.7f), new Vector3(0.08f, 0.02f, 0.8f), Quaternion.identity, line);
            for (int lane = -2; lane <= 2; lane++)
                Box(marks, $"Lane_{lane}", new Vector3(0f, top + 0.008f, R + lane * laneSpacing), new Vector3(dashLength + 2f, 0.016f, 0.05f), Quaternion.identity, line);

            return new TrackInfo
            {
                deckTopY = top,
                straightStart = root.transform.TransformPoint(new Vector3(x0, top, R)),
                straightEnd = root.transform.TransformPoint(new Vector3(x1, top, R)),
                center = root.transform.position,
                halfLength = halfLen, radius = R, width = deckWidth, root = root.transform,
                dashLength = dashLength, lapLength = lap, laneSpacing = laneSpacing, coverage = achieved,
            };
        }

        internal static void Leg(Transform parent, Transform root, Vector3 localTop, Material mat)
        {
            Vector3 worldTop = root.TransformPoint(localTop);
            float height;
            if (Physics.Raycast(worldTop + Vector3.up * 0.05f, Vector3.down, out RaycastHit hit, 60f, ~0, QueryTriggerInteraction.Ignore))
                height = worldTop.y - hit.point.y;
            else
                height = worldTop.y;   // nothing below: leg to the ground
            if (height < 0.35f) return;   // deck rests directly on the roof here
            ProBuilderMesh pb = ShapeGenerator.GenerateCylinder(PivotLocation.Center, 10, 0.3f, height, 0);
            GameObject go = pb.gameObject;
            go.name = $"Leg_{height:F1}m";
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localTop - Vector3.up * height * 0.5f;
            Finish(pb, mat);
        }

        /// <summary>Annular sector prism between radii rIn..rOut and angles t0..t1 (radians) around centre c; sx mirrors the x axis.</summary>
        internal static GameObject Wedge(Transform parent, string name, Vector3 c, float sx, float rIn, float rOut, float t0, float t1, float yBottom, float yTop, Material mat)
        {
            Vector3 P(float r, float t, float y) => c + new Vector3(sx * r * Mathf.Cos(t), y, r * Mathf.Sin(t));
            var v = new[]
            {
                P(rIn, t0, yBottom), P(rOut, t0, yBottom), P(rOut, t1, yBottom), P(rIn, t1, yBottom),
                P(rIn, t0, yTop),    P(rOut, t0, yTop),    P(rOut, t1, yTop),    P(rIn, t1, yTop),
            };
            Vector3 centroid = Vector3.zero;
            foreach (var p in v) centroid += p;
            centroid /= v.Length;
            var faces = new System.Collections.Generic.List<Face>();
            void Quad(int a, int b, int d, int e)
            {
                Vector3 n = Vector3.Cross(v[b] - v[a], v[d] - v[a]);
                Vector3 fc = (v[a] + v[b] + v[d] + v[e]) * 0.25f;
                bool outward = Vector3.Dot(n, fc - centroid) > 0f;   // Unity: clockwise winding faces the viewer
                faces.Add(outward ? new Face(new[] { a, b, d, a, d, e }) : new Face(new[] { a, d, b, a, e, d }));
            }
            Quad(0, 1, 2, 3);   // bottom
            Quad(4, 5, 6, 7);   // top
            Quad(0, 1, 5, 4);   // start cap
            Quad(3, 2, 6, 7);   // end cap
            Quad(1, 2, 6, 5);   // outer wall
            Quad(0, 3, 7, 4);   // inner wall
            ProBuilderMesh pb = ProBuilderMesh.Create(v, faces);
            GameObject go = pb.gameObject;
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            Finish(pb, mat);
            return go;
        }

        internal static GameObject Box(Transform parent, string name, Vector3 localCenter, Vector3 size, Quaternion localRot, Material mat)
        {
            ProBuilderMesh pb = ShapeGenerator.GenerateCube(PivotLocation.Center, size);
            GameObject go = pb.gameObject;
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localCenter;
            go.transform.localRotation = localRot;
            Finish(pb, mat);
            return go;
        }

        internal static void Finish(ProBuilderMesh pb, Material mat)
        {
            pb.ToMesh();
            pb.Refresh();
            GameObject go = pb.gameObject;
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null && mat != null) mr.sharedMaterial = mat;
            var mc = go.GetComponent<MeshCollider>();
            if (mc == null) mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;
            go.isStatic = true;
            EditorUtility.SetDirty(go);
        }
    }
}
