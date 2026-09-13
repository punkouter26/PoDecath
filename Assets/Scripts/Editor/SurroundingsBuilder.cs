using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using PoDecath.Fx;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// The world past the edge of the grounds, which used to be a flat green plane to the horizon.
    ///
    /// Three things, each one static mesh: a ring of low-rise blocks at the distance the city starts,
    /// with the wedge to the south left open the way the Mall is; the obelisk, 169 m tall, where the
    /// monument stands; and a treeline of cut-out sprites between the grounds and the blocks. None of it
    /// is meant to be looked at, only to be there behind the roof when a wide shot pans, and the distance
    /// fog does most of the work. Seeded, so a rebuild puts every block back where it was.
    /// </summary>
    public static class SurroundingsBuilder
    {
        const int Seed = 1600;

        public static GameObject Build(Bounds building, float groundY, VfxBank vfx)
        {
            var root = new GameObject("Surroundings");
            Vector3 centre = new Vector3(building.center.x, groundY, building.center.z);

            Material a = Dress("Skyline_A", new Color(0.58f, 0.57f, 0.56f));
            Material b = Dress("Skyline_B", new Color(0.66f, 0.64f, 0.62f));
            Material c = Dress("Skyline_C", new Color(0.5f, 0.51f, 0.54f));
            Skyline(root.transform, centre, a, b, c);
            Obelisk(root.transform, centre, Dress("Skyline_Marble", new Color(0.86f, 0.85f, 0.8f)));
            if (vfx != null && vfx.treeline != null) Treeline(root.transform, centre, vfx.treeline);

            foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = true;
            return root;
        }

        static Material Dress(string name, Color colour)
        {
            Material m = PoDecathSceneBuilder.Mat(name, colour);
            m.color = colour;
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.15f);
            EditorUtility.SetDirty(m);
            return m;
        }

        // ---------------------------------------------------------------- the city

        /// <summary>
        /// Blocks between 430 m and 980 m out, none taller than the city's own height limit allows, with
        /// the south left open. Three material slots so the ring is not one flat grey.
        /// </summary>
        static void Skyline(Transform parent, Vector3 centre, Material a, Material b, Material c)
        {
            var rng = new System.Random(Seed);
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var subs = new[] { new List<int>(), new List<int>(), new List<int>() };

            const int count = 240;
            for (int i = 0; i < count; i++)
            {
                float angle = (float)(rng.NextDouble() * 360.0);
                if (Mathf.Abs(Mathf.DeltaAngle(angle, 90f)) < 32f) continue;   // the Mall, to the south (+Z)
                float radius = Mathf.Lerp(430f, 980f, (float)rng.NextDouble());
                Vector3 dir = new Vector3(Mathf.Cos(angle * Mathf.Deg2Rad), 0f, Mathf.Sin(angle * Mathf.Deg2Rad));
                Vector3 at = centre + dir * radius;
                float w = Mathf.Lerp(14f, 38f, (float)rng.NextDouble());
                float d = Mathf.Lerp(14f, 38f, (float)rng.NextDouble());
                float h = Mathf.Lerp(12f, 42f, (float)rng.NextDouble() * (float)rng.NextDouble() + 0.15f);
                float yaw = angle + 90f + (float)(rng.NextDouble() - 0.5) * 24f;   // streets follow the ring
                AddBox(verts, normals, subs[rng.Next(3)], at + Vector3.up * (h * 0.5f), new Vector3(w, h, d), yaw);
            }

            var mesh = new Mesh { name = "Skyline" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.subMeshCount = 3;
            for (int s = 0; s < 3; s++) mesh.SetTriangles(subs[s], s);
            mesh.RecalculateBounds();
            Place(parent, "Skyline", mesh, new[] { a, b, c }, castShadows: false);
        }

        /// <summary>The monument: a tapered square shaft and a pyramidion, 169 m, south-southeast.</summary>
        static void Obelisk(Transform parent, Vector3 centre, Material marble)
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var tris = new List<int>();
            Vector3 at = centre + new Vector3(220f, 0f, 1250f);
            const float baseHalf = 8.4f, topHalf = 5.25f, shaft = 152f, tip = 169f;
            Vector3[] ring0 = Square(at, baseHalf), ring1 = Square(at + Vector3.up * shaft, topHalf);
            for (int i = 0; i < 4; i++)
            {
                int j = (i + 1) % 4;
                AddQuad(verts, normals, tris, ring0[i], ring0[j], ring1[j], ring1[i]);
                AddTri(verts, normals, tris, ring1[i], ring1[j], at + Vector3.up * tip);
            }
            var mesh = new Mesh { name = "Obelisk" };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            Place(parent, "Obelisk", mesh, new[] { marble }, castShadows: false);
        }

        /// <summary>
        /// Cross-quad trees between 150 m and 340 m out. Two quads at right angles read as a canopy from
        /// any side at that range, and cost four triangles each.
        /// </summary>
        static void Treeline(Transform parent, Vector3 centre, Material material)
        {
            var rng = new System.Random(Seed + 1);
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            const int count = 170;
            for (int i = 0; i < count; i++)
            {
                float angle = (float)(rng.NextDouble() * 360.0);
                float radius = Mathf.Lerp(150f, 340f, (float)rng.NextDouble());
                Vector3 dir = new Vector3(Mathf.Cos(angle * Mathf.Deg2Rad), 0f, Mathf.Sin(angle * Mathf.Deg2Rad));
                Vector3 at = centre + dir * radius;
                float h = Mathf.Lerp(9f, 16f, (float)rng.NextDouble());
                float w = h * 0.9f;
                float yaw = (float)(rng.NextDouble() * 180.0);
                for (int q = 0; q < 2; q++)
                {
                    Quaternion rot = Quaternion.Euler(0f, yaw + q * 90f, 0f);
                    Vector3 side = rot * Vector3.right * (w * 0.5f);
                    int v = verts.Count;
                    verts.Add(at - side); verts.Add(at + side);
                    verts.Add(at + side + Vector3.up * h); verts.Add(at - side + Vector3.up * h);
                    for (int k = 0; k < 4; k++) normals.Add(Vector3.up);
                    uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(1f, 0f));
                    uvs.Add(new Vector2(1f, 1f)); uvs.Add(new Vector2(0f, 1f));
                    tris.Add(v); tris.Add(v + 2); tris.Add(v + 1);
                    tris.Add(v); tris.Add(v + 3); tris.Add(v + 2);
                }
            }
            var mesh = new Mesh { name = "Treeline" };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            Place(parent, "Treeline", mesh, new[] { material }, castShadows: false);
        }

        // ---------------------------------------------------------------- geometry helpers

        static void Place(Transform parent, string name, Mesh mesh, Material[] materials, bool castShadows)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = materials;
            mr.shadowCastingMode = castShadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        static Vector3[] Square(Vector3 centre, float half) => new[]
        {
            centre + new Vector3(-half, 0f, -half), centre + new Vector3(half, 0f, -half),
            centre + new Vector3(half, 0f, half), centre + new Vector3(-half, 0f, half),
        };

        static void AddBox(List<Vector3> verts, List<Vector3> normals, List<int> tris, Vector3 centre, Vector3 size, float yawDegrees)
        {
            Quaternion rot = Quaternion.Euler(0f, yawDegrees, 0f);
            Vector3 h = size * 0.5f;
            Vector3 P(float x, float y, float z) => centre + rot * new Vector3(x * h.x, y * h.y, z * h.z);
            // +X, -X, +Z, -Z, top. No bottom: it is on the ground.
            AddQuad(verts, normals, tris, P(1, -1, -1), P(1, -1, 1), P(1, 1, 1), P(1, 1, -1));
            AddQuad(verts, normals, tris, P(-1, -1, 1), P(-1, -1, -1), P(-1, 1, -1), P(-1, 1, 1));
            AddQuad(verts, normals, tris, P(1, -1, 1), P(-1, -1, 1), P(-1, 1, 1), P(1, 1, 1));
            AddQuad(verts, normals, tris, P(-1, -1, -1), P(1, -1, -1), P(1, 1, -1), P(-1, 1, -1));
            AddQuad(verts, normals, tris, P(-1, 1, -1), P(1, 1, -1), P(1, 1, 1), P(-1, 1, 1));
        }

        /// <summary>A quad a, b, c, d counter-clockwise seen from outside; the winding is flipped for Unity.</summary>
        static void AddQuad(List<Vector3> verts, List<Vector3> normals, List<int> tris, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Vector3 n = Vector3.Cross(b - a, d - a).normalized;
            int v = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
            for (int k = 0; k < 4; k++) normals.Add(n);
            tris.Add(v); tris.Add(v + 2); tris.Add(v + 1);
            tris.Add(v); tris.Add(v + 3); tris.Add(v + 2);
        }

        static void AddTri(List<Vector3> verts, List<Vector3> normals, List<int> tris, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 n = Vector3.Cross(b - a, c - a).normalized;
            int v = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c);
            for (int k = 0; k < 3; k++) normals.Add(n);
            tris.Add(v); tris.Add(v + 2); tris.Add(v + 1);
        }
    }
}
