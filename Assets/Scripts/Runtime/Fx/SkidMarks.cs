using UnityEngine;
using PoDecath.Env;
using PoDecath.Sim;

namespace PoDecath.Fx
{
    /// <summary>
    /// The black streaks a foot leaves when it slides on the deck instead of gripping.
    ///
    /// This is the cheapest way to make the physics readable after the fact. A bend taken too fast does not
    /// look much different from a bend taken well while it is happening — the athlete is upright in both —
    /// but one of them scrubs its feet sideways the whole way round, and afterwards the line it took is
    /// written on the asphalt. It is also an honest read on the policy: the project's own measurements put
    /// the fall threshold at about 0.35 g of lateral acceleration on the 8.8 m bend, and the marks appear
    /// well before that, so a run that leaves them is a run that is close to the limit.
    ///
    /// One pooled quad per mark, laid flat on the surface the contact reported, fading out over
    /// <see cref="lifeSeconds"/>. Pooled rather than pushed into a persistent decal buffer because the
    /// deck is re-used every attempt and marks that survived a restart would read as this race's.
    ///
    /// Nothing here is a decal in the URP sense: it is a ground-hugging quad, the same trick
    /// <c>LongJumpPit</c> uses for the mark in the sand, which needs no renderer feature to be enabled on
    /// the pipeline asset.
    /// </summary>
    [DefaultExecutionOrder(79)]
    public class SkidMarks : MonoBehaviour
    {
        [Header("Wiring")]
        public AthleteRig rig;
        [Tooltip("A multiplying unlit material. Without one this component does nothing.")]
        public Material material;

        [Header("When a contact counts as a skid")]
        [Tooltip("Sliding slower than this is a foot rolling through its plant, not a skid.")]
        public float minSlipSpeed = 1.6f;
        [Tooltip("Contacts lighter than this are a brush; a foot has to be loaded to mark anything.")]
        public float minImpulse = 8f;
        [Tooltip("Slip speed at which a mark is laid at full length and opacity.")]
        public float referenceSlipSpeed = 5f;
        [Tooltip("Shortest gap between two marks from the same foot.")]
        public float minInterval = 0.06f;

        [Header("Look")]
        [Tooltip("Marks live at once, per athlete. The oldest is recycled beyond this.")]
        public int poolSize = 14;
        public float lifeSeconds = 4.5f;
        [Tooltip("Streak width in metres. About the width of the ball of a foot.")]
        public float width = 0.1f;
        [Tooltip("Streak length at the reference slip speed.")]
        public float maxLength = 0.55f;
        [Range(0f, 1f)] public float opacity = 0.55f;

        struct Mark
        {
            public Transform transform;
            public MeshRenderer renderer;
            public float born;
        }

        Mark[] _pool;
        int _next;
        float[] _lastMark;
        GameObject _host;
        MaterialPropertyBlock _block;
        static readonly int ColorId = Shader.PropertyToID("_BaseColor");

        void Start()
        {
            if (material == null || rig == null) { enabled = false; return; }
            // A phone is already ten times over its triangle budget on this scene; a field of sixteen each
            // holding fourteen transparent quads is not where the next frame is going to come from.
            if (RenderTier.IsMobile) poolSize = Mathf.Min(poolSize, 4);

            // Parented to the scene root, not to the athlete: a mark belongs to the deck it was left on and
            // must not follow the foot that made it.
            _host = new GameObject($"SkidMarks_{name}");
            _pool = new Mark[Mathf.Max(1, poolSize)];
            for (int i = 0; i < _pool.Length; i++)
            {
                var go = new GameObject($"Skid_{i}");
                go.transform.SetParent(_host.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = Quad();
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = material;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                mr.enabled = false;
                _pool[i] = new Mark { transform = go.transform, renderer = mr, born = -999f };
            }
            _block = new MaterialPropertyBlock();

            int feet = rig.feet != null ? rig.feet.Length : 0;
            _lastMark = new float[feet];
            for (int i = 0; i < feet; i++) _lastMark[i] = -999f;
        }

        /// <summary>The marks live outside this athlete's hierarchy, so they have to be cleared up by hand.</summary>
        void OnDestroy()
        {
            if (_host != null) Destroy(_host);
        }

        void FixedUpdate()
        {
            if (_pool == null || rig == null || rig.feet == null) return;
            for (int i = 0; i < rig.feet.Length && i < _lastMark.Length; i++)
            {
                FootContactSensor f = rig.feet[i];
                if (f == null || !f.InContact) continue;
                if (f.SlipSpeed < minSlipSpeed || f.Impulse < minImpulse) continue;
                if (Time.fixedTime - _lastMark[i] < minInterval) continue;
                _lastMark[i] = Time.fixedTime;
                Lay(f);
            }
        }

        void Lay(FootContactSensor f)
        {
            float weight = Mathf.Clamp01(f.SlipSpeed / Mathf.Max(0.01f, referenceSlipSpeed));
            Vector3 along = f.SlipDirection;
            if (along.sqrMagnitude < 1e-4f) return;

            int slot = _next;
            _next = (_next + 1) % _pool.Length;
            Mark m = _pool[slot];
            if (m.transform == null) return;

            // Just clear of the surface, so it does not z-fight the deck it is drawn on. The quad spans its
            // own local X and Y, and LookRotation puts local +Z on the surface normal and local +Y along
            // the slide, so width and length go in that order.
            m.transform.position = f.Point + f.Normal * 0.012f;
            m.transform.rotation = Quaternion.LookRotation(f.Normal, along);
            m.transform.localScale = new Vector3(width, Mathf.Lerp(width, maxLength, weight), 1f);
            m.renderer.enabled = true;

            // Mark is a struct, so the age has to be written back into the array rather than onto the copy.
            m.born = Time.time;
            _pool[slot] = m;
        }

        void LateUpdate()
        {
            if (_pool == null) return;
            for (int i = 0; i < _pool.Length; i++)
            {
                Mark m = _pool[i];
                if (m.renderer == null || !m.renderer.enabled) continue;
                float age = Time.time - m.born;
                if (age >= lifeSeconds) { m.renderer.enabled = false; continue; }
                // Rubber on asphalt does not fade, but the deck is re-used every attempt, so these do.
                float fade = 1f - age / lifeSeconds;
                _block.SetColor(ColorId, new Color(0f, 0f, 0f, opacity * fade * fade));
                m.renderer.SetPropertyBlock(_block);
            }
        }

        static Mesh _quad;

        /// <summary>A unit quad in its own XY plane facing local +Z, shared by every mark in the scene.</summary>
        static Mesh Quad()
        {
            if (_quad != null) return _quad;
            _quad = new Mesh { name = "Skid_Quad" };
            _quad.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
            });
            _quad.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) });
            _quad.SetNormals(new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward });
            _quad.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            _quad.RecalculateBounds();
            return _quad;
        }
    }
}
