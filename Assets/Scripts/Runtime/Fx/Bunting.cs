using System.Collections.Generic;
using UnityEngine;
using PoDecath.Sim;

namespace PoDecath.Fx
{
    /// <summary>
    /// A string of pennants on a rope above the outer rail, all the way round the loop.
    ///
    /// It exists to make the wind visible. The rooftop already has wind in the mix (it rises with the
    /// camera's height) and nothing in the picture moved with it. Each pennant is one triangle whose tip
    /// flaps along the rope's normal in the vertex shader (<c>PoDecath/Pennant</c>), driven by the global
    /// wind strength SceneLook publishes per preset. One mesh; a couple of hundred triangles.
    /// </summary>
    public class Bunting : MonoBehaviour
    {
        public TrackPath path;
        public Material material;
        [Tooltip("Metres between pennants along the rope.")]
        public float pitch = 0.55f;
        [Tooltip("Rope height above the deck. The rail is 0.8 m.")]
        public float ropeHeight = 1.3f;
        [Tooltip("Metres outside the deck edge.")]
        public float outward = 0.18f;
        public float pennantWidth = 0.3f;
        public float pennantDrop = 0.42f;
        public int seed = 3;

        static readonly Color[] Palette =
        {
            new Color(0.85f, 0.15f, 0.12f), new Color(0.95f, 0.95f, 0.92f), new Color(0.15f, 0.3f, 0.7f),
        };

        Mesh _mesh;

        void Awake()
        {
            if (path == null || material == null) { enabled = false; return; }
            Build();
        }

        void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }

        void Build()
        {
            var rng = new System.Random(seed);
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var seeds = new List<Vector2>();
            var colors = new List<Color>();
            var tris = new List<int>();

            float lap = path.LapLength;
            float lateral = path.deckWidth * 0.5f + outward;
            Vector3 up = Vector3.up;
            int index = 0;

            // The rope: a thin strip a hair above the pennants' top edge.
            Color rope = new Color(0.12f, 0.1f, 0.09f);
            Vector3 prevL = Vector3.zero, prevR = Vector3.zero;
            bool first = true;
            for (float s = 0f; s <= lap + 0.01f; s += 1f)
            {
                Vector3 p = path.Position(s, lateral) + up * ropeHeight;
                Vector3 n = (path.Position(s, lateral + 0.5f) - path.Position(s, lateral)).normalized;
                Vector3 l = p - n * 0.012f, r = p + n * 0.012f;
                if (!first)
                {
                    int v = verts.Count;
                    verts.Add(prevL); verts.Add(prevR); verts.Add(r); verts.Add(l);
                    for (int k = 0; k < 4; k++) { normals.Add(up); uvs.Add(Vector2.zero); seeds.Add(Vector2.zero); colors.Add(rope); }
                    tris.Add(v); tris.Add(v + 2); tris.Add(v + 1);
                    tris.Add(v); tris.Add(v + 3); tris.Add(v + 2);
                }
                prevL = l; prevR = r; first = false;
            }

            for (float s = pitch * 0.5f; s < lap; s += pitch)
            {
                Vector3 top = path.Position(s, lateral) + up * (ropeHeight - 0.01f);
                Vector3 t = path.Tangent(s);
                Vector3 n = (path.Position(s, lateral + 0.5f) - path.Position(s, lateral)).normalized;
                Color c = Palette[index++ % Palette.Length];
                var seedv = new Vector2((float)rng.NextDouble(), 0f);
                int v = verts.Count;
                verts.Add(top - t * (pennantWidth * 0.5f));
                verts.Add(top + t * (pennantWidth * 0.5f));
                verts.Add(top - up * pennantDrop);
                for (int k = 0; k < 3; k++) { normals.Add(n); seeds.Add(seedv); colors.Add(c); }
                uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(1f, 0f)); uvs.Add(new Vector2(0.5f, 1f));
                tris.Add(v); tris.Add(v + 1); tris.Add(v + 2);
            }

            _mesh = new Mesh { name = "Bunting" };
            if (verts.Count > 65000) _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            _mesh.SetVertices(verts);
            _mesh.SetNormals(normals);
            _mesh.SetUVs(0, uvs);
            _mesh.SetUVs(1, seeds);
            _mesh.SetColors(colors);
            _mesh.SetTriangles(tris, 0);
            _mesh.RecalculateBounds();
            Bounds b = _mesh.bounds;
            b.Expand(0.6f);
            _mesh.bounds = b;

            var go = new GameObject("BuntingMesh");
            go.transform.SetParent(transform, false);
            go.transform.position = Vector3.zero;
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }
    }
}
