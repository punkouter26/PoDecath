using System.Collections.Generic;
using UnityEngine;
using PoDecath.Audio;
using PoDecath.Env;
using PoDecath.Sim;

namespace PoDecath.Fx
{
    /// <summary>
    /// The crowd you can see, to go with the crowd you can already hear.
    ///
    /// <see cref="CrowdRing"/> has been ringing the deck with emitters and <see cref="RaceAudio"/> has
    /// been deciding what mood they are in, and nothing on screen showed a single spectator. This places
    /// rows of billboard figures on whatever roof surface lies outside the rail (the residence roof at
    /// the two bends, the lower wing roofs along the straights) and drives them off the same level the
    /// mix uses, so a crowd that gets louder also gets livelier, and a cheer is a jump.
    ///
    /// One mesh, one draw call. All the motion is in the vertex shader (<c>PoDecath/CrowdBillboard</c>);
    /// the only thing this component does per frame is push two floats into a property block.
    /// </summary>
    [DefaultExecutionOrder(122)]
    public class CrowdStands : MonoBehaviour
    {
        [Header("Wiring")]
        public TrackPath path;
        [Tooltip("The billboard material, from the VfxBank. Without it nothing is built.")]
        public Material material;
        [Tooltip("Optional. The stands read the crowd level the mix already computes; without it they idle.")]
        public RaceAudio audioMix;

        [Header("Placement")]
        [Tooltip("Metres past the outer rail for the front row.")]
        public float outward = 2.6f;
        public float rowPitch = 1.05f;
        public float spacing = 0.72f;
        public float figureWidth = 0.9f;
        public float figureHeight = 1.85f;
        [Tooltip("The ray that finds the roof starts this far above the deck and reaches this far below it. "
               + "The wing roofs are several metres under the residence roof and still count as standing room; "
               + "the ground 24 m down does not.")]
        public float rayAbove = 3f;
        public float rayBelow = 12f;
        public int seed = 7;

        [Header("Reaction")]
        [Tooltip("How fast a jump settles, per second.")]
        public float burstDecay = 1.4f;

        MeshRenderer _renderer;
        Mesh _mesh;
        MaterialPropertyBlock _block;
        float _burst;
        float _mood;

        static readonly int MoodId = Shader.PropertyToID("_Mood");
        static readonly int BurstId = Shader.PropertyToID("_Burst");

        // Shirts. Nothing in the house colours: the athletes' lane colours have to stay theirs.
        static readonly Color[] Shirts =
        {
            new Color(0.92f, 0.92f, 0.9f), new Color(0.2f, 0.22f, 0.3f), new Color(0.75f, 0.2f, 0.18f),
            new Color(0.18f, 0.4f, 0.75f), new Color(0.9f, 0.75f, 0.25f), new Color(0.25f, 0.55f, 0.35f),
            new Color(0.55f, 0.3f, 0.6f), new Color(0.95f, 0.55f, 0.3f), new Color(0.35f, 0.35f, 0.38f),
        };

        /// <summary>Spectators placed. Zero means no roof was found outside the rail.</summary>
        public int Count { get; private set; }

        void Start()
        {
            if (path == null || material == null) { enabled = false; return; }
            Build();
            if (audioMix != null) audioMix.Reaction += OnReaction;
        }

        void OnDestroy()
        {
            if (audioMix != null) audioMix.Reaction -= OnReaction;
            if (_mesh != null) Destroy(_mesh);
        }

        void OnReaction(float volume) => Burst(volume);

        /// <summary>A jump across the whole crowd, 0..1.</summary>
        public void Burst(float strength) => _burst = Mathf.Max(_burst, Mathf.Clamp01(strength));

        void Build()
        {
            int rows = RenderTier.CrowdRows;
            var rng = new System.Random(seed);
            var verts = new List<Vector3>();
            var uv0 = new List<Vector2>();
            var corner = new List<Vector2>();
            var seeds = new List<Vector2>();
            var colors = new List<Color>();
            var tris = new List<int>();

            int creature = LayerMask.NameToLayer("Creature");
            int mask = creature >= 0 ? ~(1 << creature) : ~0;
            float lap = path.LapLength;
            float half = path.deckWidth * 0.5f;

            for (int row = 0; row < rows; row++)
            {
                float lateral = half + outward + row * rowPitch;
                float step = spacing * (1f + row * 0.15f);   // the back rows thin out a little
                for (float s = 0f; s < lap; s += step)
                {
                    float js = (float)(rng.NextDouble() - 0.5) * 0.3f;
                    float jl = (float)(rng.NextDouble() - 0.5) * 0.25f;
                    Vector3 p = path.Position(s + js, lateral + jl);
                    Vector3 from = p + Vector3.up * rayAbove;
                    if (!Physics.Raycast(from, Vector3.down, out RaycastHit hit, rayAbove + rayBelow, mask, QueryTriggerInteraction.Ignore))
                        continue;                                   // over the edge: nothing to stand on
                    if (hit.point.y > path.deckTopY - 0.2f) continue;   // landed on the deck or the rail

                    int v = verts.Count;
                    int variant = rng.Next(4);
                    float u0 = variant * 0.25f, u1 = u0 + 0.25f;
                    Color shirt = Shirts[rng.Next(Shirts.Length)];
                    var seedv = new Vector2((float)rng.NextDouble(), (float)rng.NextDouble());
                    Vector3 pivot = hit.point;
                    for (int k = 0; k < 4; k++)
                    {
                        verts.Add(pivot);
                        colors.Add(shirt);
                        seeds.Add(seedv);
                    }
                    corner.Add(new Vector2(-0.5f, 0f)); corner.Add(new Vector2(0.5f, 0f));
                    corner.Add(new Vector2(0.5f, 1f)); corner.Add(new Vector2(-0.5f, 1f));
                    uv0.Add(new Vector2(u0, 0f)); uv0.Add(new Vector2(u1, 0f));
                    uv0.Add(new Vector2(u1, 1f)); uv0.Add(new Vector2(u0, 1f));
                    tris.Add(v); tris.Add(v + 2); tris.Add(v + 1);
                    tris.Add(v); tris.Add(v + 3); tris.Add(v + 2);
                }
            }

            Count = verts.Count / 4;
            if (Count == 0) { enabled = false; return; }

            _mesh = new Mesh { name = "CrowdStands" };
            if (verts.Count > 65000) _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            _mesh.SetVertices(verts);
            _mesh.SetUVs(0, uv0);
            _mesh.SetUVs(1, corner);
            _mesh.SetUVs(2, seeds);
            _mesh.SetColors(colors);
            _mesh.SetTriangles(tris, 0);
            _mesh.RecalculateBounds();
            // The shader moves every vertex up to a figure's height off its pivot; the bounds have to know.
            Bounds b = _mesh.bounds;
            b.Expand(new Vector3(figureWidth, figureHeight * 2f, figureWidth));
            _mesh.bounds = b;

            var go = new GameObject("CrowdStandsMesh");
            go.transform.SetParent(transform, false);
            go.transform.position = Vector3.zero;
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = go.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = material;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            _block = new MaterialPropertyBlock();
            _block.SetFloat(Shader.PropertyToID("_Width"), figureWidth);
            _block.SetFloat(Shader.PropertyToID("_Height"), figureHeight);
        }

        void Update()
        {
            if (_renderer == null) return;
            float target = audioMix != null ? audioMix.CrowdLevel : 0.15f;
            _mood = Mathf.MoveTowards(_mood, target, Time.deltaTime * 0.8f);
            _burst = Mathf.Max(0f, _burst - burstDecay * Time.deltaTime);
            _block.SetFloat(MoodId, _mood);
            _block.SetFloat(BurstId, _burst);
            _renderer.SetPropertyBlock(_block);
        }
    }
}
