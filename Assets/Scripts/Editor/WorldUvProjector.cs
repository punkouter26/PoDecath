using System.Collections.Generic;
using UnityEngine;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// Re-projects a generated mesh's UVs from world position, in metres, so a tiling surface reads at the
    /// same scale everywhere on it.
    ///
    /// The track is ProBuilder geometry: two straights, forty-eight annular wedges round the bends, a few
    /// hundred legs and rails. ProBuilder's own UVs are per-face and normalised, so a 22 m straight and a
    /// 1 m wedge each get one repeat of the texture and the asphalt visibly changes size at every joint —
    /// the exact failure a tri-planar shader exists to avoid. Doing the projection here instead of in a
    /// shader gets the same result for nothing at runtime, and keeps every material on stock URP Lit, so
    /// SSAO, decals, the SRP batcher and shadow casting all continue to work without a custom pass.
    ///
    /// Vertices are unwelded first, because a vertex shared by a top face and a side face needs a different
    /// UV in each and cannot carry both. Tangents are recalculated afterwards, without which the generated
    /// normal maps would light backwards.
    /// </summary>
    public static class WorldUvProjector
    {
        /// <summary>Above this the split is not worth it; the White House mesh is not ours to re-UV anyway.</summary>
        const int MaxVertices = 60000;

        /// <summary>Projects one object's mesh. <paramref name="metresPerTile"/> is one texture repeat.</summary>
        public static void Project(GameObject go, float metresPerTile)
        {
            if (go == null) return;
            var mf = go.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;
            Mesh projected = Project(mf.sharedMesh, go.transform, metresPerTile);
            if (projected == null) return;
            mf.sharedMesh = projected;
            var mc = go.GetComponent<MeshCollider>();
            if (mc != null) mc.sharedMesh = projected;
        }

        /// <summary>Projects every mesh under a root, one tile size for all of them.</summary>
        public static void ProjectHierarchy(Transform root, float metresPerTile)
        {
            if (root == null) return;
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
                Project(mf.gameObject, metresPerTile);
        }

        /// <summary>
        /// Builds the re-projected mesh. Each triangle is projected down its own dominant world axis — a
        /// deck panel takes its UVs from world XZ, a barrier face from XY or ZY — which is what makes the
        /// texture continuous across the joint between two pieces that meet at a right angle.
        /// </summary>
        public static Mesh Project(Mesh source, Transform space, float metresPerTile)
        {
            if (source == null || !source.isReadable || source.vertexCount > MaxVertices) return null;
            float tile = Mathf.Max(0.05f, metresPerTile);

            Vector3[] srcV = source.vertices;
            Vector3[] srcN = source.normals;
            Color[] srcC = source.colors;
            bool haveNormals = srcN != null && srcN.Length == srcV.Length;
            bool haveColors = srcC != null && srcC.Length == srcV.Length;

            Matrix4x4 toWorld = space != null ? space.localToWorldMatrix : Matrix4x4.identity;

            var verts = new List<Vector3>(source.vertexCount * 2);
            var norms = new List<Vector3>(source.vertexCount * 2);
            var uvs = new List<Vector2>(source.vertexCount * 2);
            var cols = haveColors ? new List<Color>(source.vertexCount * 2) : null;
            var subMeshes = new List<int[]>();

            for (int sub = 0; sub < source.subMeshCount; sub++)
            {
                int[] tris = source.GetTriangles(sub);
                var outTris = new int[tris.Length];
                for (int t = 0; t < tris.Length; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    Vector3 wa = toWorld.MultiplyPoint3x4(srcV[a]);
                    Vector3 wb = toWorld.MultiplyPoint3x4(srcV[b]);
                    Vector3 wc = toWorld.MultiplyPoint3x4(srcV[c]);
                    int axis = DominantAxis(Vector3.Cross(wb - wa, wc - wa));

                    outTris[t] = Emit(verts, norms, uvs, cols, srcV, srcN, srcC, haveNormals, haveColors, a, wa, axis, tile);
                    outTris[t + 1] = Emit(verts, norms, uvs, cols, srcV, srcN, srcC, haveNormals, haveColors, b, wb, axis, tile);
                    outTris[t + 2] = Emit(verts, norms, uvs, cols, srcV, srcN, srcC, haveNormals, haveColors, c, wc, axis, tile);
                }
                subMeshes.Add(outTris);
            }

            var mesh = new Mesh { name = source.name + "_WorldUV" };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            if (haveNormals) mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            if (haveColors) mesh.SetColors(cols);
            mesh.subMeshCount = subMeshes.Count;
            for (int i = 0; i < subMeshes.Count; i++) mesh.SetTriangles(subMeshes[i], i);
            if (!haveNormals) mesh.RecalculateNormals();
            mesh.RecalculateTangents();   // the normal maps are unusable without these
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Appends one vertex with its projected UV and returns its index.</summary>
        static int Emit(List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<Color> cols,
                        Vector3[] srcV, Vector3[] srcN, Color[] srcC, bool haveNormals, bool haveColors,
                        int index, Vector3 world, int axis, float tile)
        {
            verts.Add(srcV[index]);
            if (haveNormals) norms.Add(srcN[index]);
            if (haveColors) cols.Add(srcC[index]);
            uvs.Add(Uv(world, axis, tile));
            return verts.Count - 1;
        }

        /// <summary>0 = project down world X, 1 = down Y, 2 = down Z.</summary>
        static int DominantAxis(Vector3 n)
        {
            float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
            if (ay >= ax && ay >= az) return 1;
            return ax >= az ? 0 : 2;
        }

        static Vector2 Uv(Vector3 world, int axis, float tile) => axis switch
        {
            0 => new Vector2(world.z, world.y) / tile,
            1 => new Vector2(world.x, world.z) / tile,
            _ => new Vector2(world.x, world.y) / tile,
        };
    }
}
