using UnityEngine;
using PoDecath.Env;
using PoDecath.Sim;

namespace PoDecath.Fx
{
    /// <summary>
    /// The strain in one athlete's joints, drawn on the athlete.
    ///
    /// A physics humanoid running well and a physics humanoid barely holding itself together look almost
    /// identical from a broadcast camera, right up to the moment one of them is on the deck. All the
    /// evidence exists -- <see cref="EffortMeter"/> has each joint's torque as a fraction of the limit it
    /// was configured with -- and none of it was on screen. This puts a small glow on every joint that
    /// grows and reddens with how hard that joint is pulling, so the fight into a bend is visible while it
    /// is happening rather than inferable afterwards from a fall.
    ///
    /// One mesh per athlete, not one object per joint. Twenty-one joints across a field of sixteen is 336
    /// markers, and as separate renderers that is 336 draw calls for a decoration. Built as a single
    /// camera-facing quad strip rebuilt in LateUpdate, it is one draw call per athlete and the vertex
    /// arrays are allocated once.
    ///
    /// Colour rides on vertex colour, which URP's particle shader multiplies through. Quad *size* rides on
    /// the same number, so if a material is ever swapped for one that ignores vertex colour the effect
    /// degrades to a white glow that still grows with strain rather than to nothing at all.
    /// </summary>
    [DefaultExecutionOrder(78)]
    public class StressSkeleton : MonoBehaviour
    {
        const string VisibleKey = "podecath.stressskeleton";

        static bool _visible;
        static bool _visibleResolved;

        /// <summary>
        /// Whether the overlay is drawn at all, across every athlete. Persisted, and off by default on the
        /// mobile tier where sixteen extra transparent draw calls are not affordable against a triangle
        /// budget that is already over.
        /// </summary>
        public static bool Visible
        {
            get
            {
                if (!_visibleResolved)
                {
                    _visibleResolved = true;
                    _visible = PlayerPrefs.GetInt(VisibleKey, RenderTier.IsMobile ? 0 : 1) != 0;
                }
                return _visible;
            }
            set
            {
                _visibleResolved = true;
                _visible = value;
                PlayerPrefs.SetInt(VisibleKey, value ? 1 : 0);
            }
        }

        [Header("Wiring")]
        public AthleteRig rig;
        public EffortMeter effort;
        [Tooltip("Additive particle material. Without one this component does nothing.")]
        public Material material;

        [Header("Look")]
        [Tooltip("Marker diameter at rest, in metres. Joints are a few centimetres apart on a 1.8 m body.")]
        public float restSize = 0.055f;
        [Tooltip("Marker diameter at full torque. The growth is most of what reads at distance.")]
        public float strainedSize = 0.16f;
        [Tooltip("Saturation below which a joint is drawn at all. Everything idles a little above zero and "
               + "a body-wide constellation of faint dots is noise, not information.")]
        public float floor = 0.12f;
        public Color coolColor = new Color(0.35f, 0.78f, 1f, 0.55f);
        public Color hotColor = new Color(1f, 0.28f, 0.16f, 1f);

        [Header("Budget")]
        [Tooltip("Beyond this the markers are a few pixels and not worth the transparent overdraw.")]
        public float maxCameraDistance = 45f;

        Mesh _mesh;
        MeshRenderer _renderer;
        Transform _host;
        Camera _view;

        Vector3[] _vertices;
        Color[] _colors;
        Vector2[] _uv;
        int[] _triangles;

        void Start()
        {
            if (material == null || rig == null) { enabled = false; return; }
            if (effort == null) effort = GetComponentInParent<EffortMeter>();
            if (effort == null) { enabled = false; return; }

            // A child at the athlete's base rather than a world-space object: the mesh is rebuilt in the
            // host's local space every frame, so parking the host on the base keeps the vertex magnitudes
            // small and the bounds tight wherever on the roof the athlete is.
            var go = new GameObject("StressSkeleton");
            go.transform.SetParent(transform, false);
            _host = go.transform;

            _mesh = new Mesh { name = "StressSkeleton" };
            _mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;

            _renderer = go.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = material;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _renderer.enabled = false;
        }

        void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }

        void LateUpdate()
        {
            if (_renderer == null) return;

            int n = effort.JointCount;
            if (n == 0 || !Visible) { _renderer.enabled = false; return; }

            if (_view == null) _view = Camera.main;
            Vector3 basePos = rig.BasePosition;
            if (_view != null)
            {
                float d2 = (basePos - _view.transform.position).sqrMagnitude;
                if (d2 > maxCameraDistance * maxCameraDistance) { _renderer.enabled = false; return; }
            }

            EnsureArrays(n);
            _host.position = basePos;
            _host.rotation = Quaternion.identity;

            // Camera-facing axes, worked out once for the whole body: the markers are a constellation
            // around one athlete a few metres across, so per-quad billboarding would be a rounding error's
            // worth of difference for twenty-one times the trigonometry.
            Vector3 right = _view != null ? _view.transform.right : Vector3.right;
            Vector3 up = _view != null ? _view.transform.up : Vector3.up;

            float[] saturation = effort.Saturation;
            bool any = false;

            for (int i = 0; i < n; i++)
            {
                int v = i * 4;
                float s = saturation[i];
                if (s < floor)
                {
                    // Degenerate quad rather than a shorter index buffer: rewriting the triangles every
                    // frame would churn the mesh's topology, and four coincident vertices cost nothing to
                    // rasterise.
                    Vector3 hide = Vector3.zero;
                    _vertices[v] = _vertices[v + 1] = _vertices[v + 2] = _vertices[v + 3] = hide;
                    _colors[v] = _colors[v + 1] = _colors[v + 2] = _colors[v + 3] = Color.clear;
                    continue;
                }

                any = true;
                float t = Mathf.InverseLerp(floor, 1f, s);
                float half = Mathf.Lerp(restSize, strainedSize, t) * 0.5f;
                Vector3 centre = effort.JointPosition(i) - basePos;
                Vector3 r = right * half;
                Vector3 u = up * half;

                _vertices[v] = centre - r - u;
                _vertices[v + 1] = centre + r - u;
                _vertices[v + 2] = centre + r + u;
                _vertices[v + 3] = centre - r + u;

                Color c = Color.Lerp(coolColor, hotColor, t);
                _colors[v] = _colors[v + 1] = _colors[v + 2] = _colors[v + 3] = c;
            }

            _renderer.enabled = any;
            if (!any) return;

            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            // A fixed box around the athlete. Recalculating bounds every frame off twenty-one quads, most
            // of them collapsed on the origin, would give a box that snaps about as joints drop in and out
            // and make the renderer cull itself at the edge of frame.
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(2.5f, 2.5f, 2.5f));
        }

        void EnsureArrays(int n)
        {
            if (_vertices != null && _vertices.Length == n * 4) return;

            _vertices = new Vector3[n * 4];
            _colors = new Color[n * 4];
            _uv = new Vector2[n * 4];
            _triangles = new int[n * 6];

            for (int i = 0; i < n; i++)
            {
                int v = i * 4, t = i * 6;
                _uv[v] = new Vector2(0f, 0f);
                _uv[v + 1] = new Vector2(1f, 0f);
                _uv[v + 2] = new Vector2(1f, 1f);
                _uv[v + 3] = new Vector2(0f, 1f);
                _triangles[t] = v; _triangles[t + 1] = v + 2; _triangles[t + 2] = v + 1;
                _triangles[t + 3] = v; _triangles[t + 4] = v + 3; _triangles[t + 5] = v + 2;
            }

            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetUVs(0, _uv);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_triangles, 0);
        }
    }
}
