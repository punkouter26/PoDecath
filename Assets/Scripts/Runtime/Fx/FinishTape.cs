using UnityEngine;
using PoDecath.Sim;

namespace PoDecath.Fx
{
    /// <summary>
    /// The tape across the line, and what happens to it when the winner hits it.
    ///
    /// Before the gun it is a taut ribbon at chest height between the two rails. The first athlete to
    /// finish breaks it where they crossed, and the two halves fall and flutter on a small verlet rope
    /// pinned at the rails, then fade out. It is reset with the attempt, so every race gets a fresh tape.
    ///
    /// The break point is the winner's own lateral position on the line, not the middle, which is the
    /// detail that makes it read as something that happened to a tape rather than an animation that
    /// played.
    /// </summary>
    [DefaultExecutionOrder(126)]
    public class FinishTape : MonoBehaviour
    {
        [Header("Wiring")]
        public RaceEvent race;
        [Tooltip("The loop, when the race is on it; the tape spans the deck at the finish arc length.")]
        public TrackPath path;
        [Tooltip("Two-sided vertex-coloured material from the VfxBank.")]
        public Material material;

        [Header("Tape")]
        public float height = 1.15f;
        public float width = 0.09f;
        public int segments = 26;
        [Tooltip("Seconds the broken halves stay before fading out.")]
        public float fadeSeconds = 7f;
        [Tooltip("Metres in from each rail the tape is tied.")]
        public float inset = 0.35f;

        Vector3 _a, _b;
        Vector3[] _pos, _prev;
        bool[] _pinned;
        int _breakAt = -1;
        float _brokenTime = -1f;
        int _lastAttempt = -1;
        Mesh _mesh;
        MeshRenderer _renderer;
        Vector3[] _verts;
        Color[] _colors;
        bool _ready;

        void Start()
        {
            if (race == null || material == null || race is LongJumpEvent) { enabled = false; return; }
            if (!Ends(out _a, out _b)) { enabled = false; return; }
            BuildMesh();
            ResetTape();
        }

        void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }

        /// <summary>Where the tape is tied: the finish line, one rail to the other.</summary>
        bool Ends(out Vector3 a, out Vector3 b)
        {
            a = b = Vector3.zero;
            if (race is LapEvent lap && path != null)
            {
                float half = path.deckWidth * 0.5f - inset;
                a = path.Position(lap.startS, half) + Vector3.up * height;
                b = path.Position(lap.startS, -half) + Vector3.up * height;
                return true;
            }
            if (race.direction.sqrMagnitude < 1e-4f) return false;
            Vector3 dir = race.direction.normalized;
            Vector3 finish = race.startLine + dir * race.raceDistance;
            Vector3 across = Vector3.Cross(Vector3.up, dir).normalized;
            float halfSpan = race.laneSpacing * race.maxLanes * 0.5f + inset;
            a = finish + across * halfSpan + Vector3.up * height;
            b = finish - across * halfSpan + Vector3.up * height;
            return true;
        }

        void BuildMesh()
        {
            int n = segments + 1;
            _pos = new Vector3[n];
            _prev = new Vector3[n];
            _pinned = new bool[n];
            _verts = new Vector3[n * 2];
            _colors = new Color[n * 2];
            var uvs = new Vector2[n * 2];
            var tris = new int[segments * 6];
            for (int i = 0; i < n; i++)
            {
                // Red and white in blocks of three segments, the way a real tape is printed.
                Color c = (i / 3) % 2 == 0 ? new Color(0.9f, 0.12f, 0.1f, 1f) : Color.white;
                _colors[i * 2] = _colors[i * 2 + 1] = c;
                uvs[i * 2] = new Vector2(0.5f, 0.5f);
                uvs[i * 2 + 1] = new Vector2(0.5f, 0.5f);
            }
            for (int i = 0; i < segments; i++)
            {
                int v = i * 2, t = i * 6;
                tris[t] = v; tris[t + 1] = v + 2; tris[t + 2] = v + 1;
                tris[t + 3] = v + 1; tris[t + 4] = v + 2; tris[t + 5] = v + 3;
            }
            _mesh = new Mesh { name = "FinishTape" };
            _mesh.MarkDynamic();
            _mesh.SetVertices(_verts);
            _mesh.SetUVs(0, uvs);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(tris, 0);

            var go = new GameObject("FinishTapeMesh");
            go.transform.SetParent(transform, false);
            go.transform.position = Vector3.zero;
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = go.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = material;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _ready = true;
        }

        // Not named Reset: that is a Unity message the editor sends when the component is added, before Start.
        void ResetTape()
        {
            int n = _pos.Length;
            for (int i = 0; i < n; i++)
            {
                _pos[i] = _prev[i] = Vector3.Lerp(_a, _b, (float)i / segments);
                _pinned[i] = i == 0 || i == n - 1;
            }
            _breakAt = -1;
            _brokenTime = -1f;
            _renderer.enabled = true;
            Paint(1f);
        }

        void FixedUpdate()
        {
            if (!_ready) return;
            if (race.Attempt != _lastAttempt) { _lastAttempt = race.Attempt; ResetTape(); }
            if (_breakAt < 0)
            {
                if (race.Current != RaceEvent.Phase.Running && race.Current != RaceEvent.Phase.Finished) return;
                foreach (RaceEvent.Athlete a in race.Athletes)
                {
                    if (!a.finished) continue;
                    Break(a);
                    break;
                }
                return;
            }
            Simulate(Time.fixedDeltaTime);
        }

        void Break(RaceEvent.Athlete winner)
        {
            Vector3 p = winner.IsRL ? winner.rig.BasePosition : (winner.go != null ? winner.go.transform.position : (_a + _b) * 0.5f);
            Vector3 ab = _b - _a;
            float t = Mathf.Clamp01(Vector3.Dot(p - _a, ab) / Mathf.Max(1e-4f, ab.sqrMagnitude));
            _breakAt = Mathf.Clamp(Mathf.RoundToInt(t * segments), 1, segments - 2);
            _brokenTime = Time.time;
            // The winner carries the tape forward for a moment: the two points either side of the break
            // start with its velocity.
            Vector3 v = winner.IsRL ? winner.rig.BaseLinearVelocityWorld : Vector3.zero;
            _prev[_breakAt] = _pos[_breakAt] - v * Time.fixedDeltaTime * 0.8f;
            _prev[_breakAt + 1] = _pos[_breakAt + 1] - v * Time.fixedDeltaTime * 0.8f;
        }

        void Simulate(float dt)
        {
            int n = _pos.Length;
            float wind = Shader.GetGlobalFloat("_PoDecathWind");
            float tt = Time.time;
            Vector3 gust = new Vector3(Mathf.Sin(tt * 1.3f), 0.2f * Mathf.Sin(tt * 2.1f), Mathf.Cos(tt * 0.9f)) * (0.8f * wind);
            Vector3 g = Physics.gravity * 0.6f;   // a tape is light; it falls slower than a stone
            for (int i = 0; i < n; i++)
            {
                if (_pinned[i]) continue;
                Vector3 vel = (_pos[i] - _prev[i]) * 0.975f;
                _prev[i] = _pos[i];
                _pos[i] += vel + (g + gust) * dt * dt;
            }
            float rest = (_b - _a).magnitude / segments;
            for (int iter = 0; iter < 5; iter++)
            {
                for (int i = 0; i < n - 1; i++)
                {
                    if (i == _breakAt) continue;   // the two halves are not joined any more
                    Vector3 d = _pos[i + 1] - _pos[i];
                    float len = d.magnitude;
                    if (len < 1e-5f) continue;
                    Vector3 corr = d * ((len - rest) / len);
                    bool pa = _pinned[i], pb = _pinned[i + 1];
                    if (pa && pb) continue;
                    if (pa) _pos[i + 1] -= corr;
                    else if (pb) _pos[i] += corr;
                    else { _pos[i] += corr * 0.5f; _pos[i + 1] -= corr * 0.5f; }
                }
            }
        }

        void LateUpdate()
        {
            if (!_ready || !_renderer.enabled) return;
            float alpha = 1f;
            if (_brokenTime >= 0f)
            {
                float age = Time.time - _brokenTime;
                alpha = 1f - Mathf.Clamp01((age - fadeSeconds) / 1.5f);
                if (alpha <= 0f) { _renderer.enabled = false; return; }
            }
            Paint(alpha);
        }

        /// <summary>The ribbon: a vertical strip along the rope, with the gap left open at the break.</summary>
        void Paint(float alpha)
        {
            int n = _pos.Length;
            Vector3 up = Vector3.up * (width * 0.5f);
            for (int i = 0; i < n; i++)
            {
                _verts[i * 2] = _pos[i] - up;
                _verts[i * 2 + 1] = _pos[i] + up;
                Color c = _colors[i * 2];
                c.a = alpha;
                _colors[i * 2] = _colors[i * 2 + 1] = c;
            }
            if (_breakAt >= 0)
            {
                // Collapse the broken segment onto its near end so nothing is drawn across the gap; a
                // degenerate quad rasterises to nothing and the index buffer never has to change.
                _verts[(_breakAt + 1) * 2] = _verts[_breakAt * 2];
                _verts[(_breakAt + 1) * 2 + 1] = _verts[_breakAt * 2 + 1];
            }
            _mesh.SetVertices(_verts);
            _mesh.SetColors(_colors);
            _mesh.RecalculateBounds();
        }
    }
}
