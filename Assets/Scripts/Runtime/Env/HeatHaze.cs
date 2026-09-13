using System.Collections.Generic;
using UnityEngine;
using PoDecath.Sim;

namespace PoDecath.Env
{
    /// <summary>
    /// Heat shimmer over the straights on a hot afternoon: transparent curtains across the deck every few
    /// metres, each re-sampling the picture behind it through a rolling offset (<c>PoDecath/HeatHaze</c>).
    /// It reads when the shot looks along the deck (the rail camera, the head-on), which is when a
    /// summer track actually does shimmer.
    ///
    /// PC tier only. The effect needs the opaque texture, which the mobile pipeline asset does not
    /// request, and it is a second-order cue on a phone screen anyway. SceneLook turns it on for the
    /// afternoon preset and off for the others.
    /// </summary>
    public class HeatHaze : MonoBehaviour
    {
        public TrackPath path;
        public Material material;
        [Tooltip("Metres between curtains along each straight.")]
        public float spacing = 6f;
        public float height = 1.3f;
        [Tooltip("Extra width past the deck on each side.")]
        public float overhang = 0.8f;

        MeshRenderer _renderer;
        Mesh _mesh;
        bool _wanted = true;

        void Awake()
        {
            if (path == null || material == null || !RenderTier.HeatHaze) { enabled = false; return; }
            Build();
            Apply();
        }

        void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }

        /// <summary>Whether the shimmer is wanted by the current look; the tier still has the last word.</summary>
        public void Set(bool on)
        {
            _wanted = on;
            Apply();
        }

        void Apply()
        {
            if (_renderer != null) _renderer.enabled = _wanted && RenderTier.HeatHaze;
        }

        void Build()
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            float L = path.StraightLength, A = path.ArcLength;
            float half = path.deckWidth * 0.5f + overhang;
            float[] starts = { 0f, L + A };
            foreach (float s0 in starts)
            {
                for (float s = s0 + spacing * 0.5f; s < s0 + L; s += spacing)
                {
                    Vector3 left = path.Position(s, half) + Vector3.up * 0.05f;
                    Vector3 right = path.Position(s, -half) + Vector3.up * 0.05f;
                    int v = verts.Count;
                    verts.Add(left); verts.Add(right);
                    verts.Add(right + Vector3.up * height); verts.Add(left + Vector3.up * height);
                    uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(1f, 0f));
                    uvs.Add(new Vector2(1f, 1f)); uvs.Add(new Vector2(0f, 1f));
                    tris.Add(v); tris.Add(v + 2); tris.Add(v + 1);
                    tris.Add(v); tris.Add(v + 3); tris.Add(v + 2);
                }
            }
            if (verts.Count == 0) { enabled = false; return; }

            _mesh = new Mesh { name = "HeatHaze" };
            _mesh.SetVertices(verts);
            _mesh.SetUVs(0, uvs);
            _mesh.SetTriangles(tris, 0);
            _mesh.RecalculateBounds();

            var go = new GameObject("HeatHazeMesh");
            go.transform.SetParent(transform, false);
            go.transform.position = Vector3.zero;
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = go.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = material;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        }
    }
}
