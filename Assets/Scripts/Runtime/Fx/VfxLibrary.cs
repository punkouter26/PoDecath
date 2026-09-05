using UnityEngine;
using PoDecath.Env;

namespace PoDecath.Fx
{
    /// <summary>
    /// Builds the game's particle systems in code and hands out pooled one-shot emitters.
    ///
    /// They are built rather than authored because a particle prefab is a hundred serialised fields that
    /// cannot be read in a diff, and the eight effects here are each about ten numbers. Building them also
    /// means the per-tier budgets in <see cref="RenderTier"/> can be applied at construction, so a phone
    /// gets a twenty-four particle sand burst and a desktop gets ninety without two sets of assets.
    ///
    /// Everything is pooled and pre-warmed. A footfall puff fires several times a second per athlete
    /// across a field of sixteen, and allocating a system per step would cost more than the effect is
    /// worth.
    /// </summary>
    public class VfxLibrary : MonoBehaviour
    {
        /// <summary>The effects the game can fire. One pooled particle system per entry.</summary>
        public enum Effect
        {
            /// <summary>A footfall on the deck: a low, fast, small puff.</summary>
            FootDust,
            /// <summary>An athlete going down: a wider, slower cloud that hangs.</summary>
            FallDust,
            /// <summary>Landing in the pit: sand thrown forward and up, falling back under gravity.</summary>
            SandBurst,
            /// <summary>A hurdle frame scraping over: sparks and a scuff of dust.</summary>
            HurdleScuff,
            /// <summary>The starter's pistol.</summary>
            GunSmoke,
            /// <summary>Over the line.</summary>
            Confetti,
        }

        [Tooltip("Materials for every effect. Without a bank this component does nothing at all.")]
        public VfxBank bank;

        [Tooltip("Simultaneous live instances of each effect. Beyond this the oldest is recycled.")]
        public int poolSize = 6;

        static VfxLibrary _instance;

        /// <summary>
        /// The library in the current scene, if one was built. Effects are fire-and-forget from a dozen
        /// places (feet, hurdles, the pit, the event), and none of them should have to hold a reference.
        /// </summary>
        public static VfxLibrary Instance => _instance;

        ParticleSystem[][] _pools;
        int[] _next;

        void Awake()
        {
            _instance = this;
            int kinds = System.Enum.GetValues(typeof(Effect)).Length;
            _pools = new ParticleSystem[kinds][];
            _next = new int[kinds];
            if (bank == null || !bank.IsUsable) { enabled = false; return; }

            for (int k = 0; k < kinds; k++)
            {
                var effect = (Effect)k;
                int count = effect == Effect.FootDust ? poolSize * 3 : poolSize;   // feet fire far more often
                _pools[k] = new ParticleSystem[count];
                for (int i = 0; i < count; i++) _pools[k][i] = Create(effect, i);
            }
        }

        void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        /// <summary>
        /// Fires one effect at a world point. <paramref name="direction"/> orients anything with a
        /// preferred way to go (sand goes down the pit, sparks go with the knock); pass
        /// <see cref="Vector3.up"/> for the ones that do not care. <paramref name="strength"/> in 0..1
        /// scales the count and the speed, so a heavy footfall throws more up than a light one.
        /// </summary>
        public static void Play(Effect effect, Vector3 at, Vector3 direction, float strength = 1f)
        {
            VfxLibrary lib = _instance;
            if (lib == null || !lib.enabled) return;
            lib.Fire(effect, at, direction, strength);
        }

        void Fire(Effect effect, Vector3 at, Vector3 direction, float strength)
        {
            int k = (int)effect;
            ParticleSystem[] pool = _pools[k];
            if (pool == null || pool.Length == 0) return;

            ParticleSystem ps = pool[_next[k]];
            _next[k] = (_next[k] + 1) % pool.Length;
            if (ps == null) return;

            ps.transform.position = at;
            if (direction.sqrMagnitude > 1e-4f) ps.transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);

            ParticleSystem.MainModule main = ps.main;
            float s = Mathf.Clamp(strength, 0.15f, 1.5f);
            main.startSpeedMultiplier = _baseSpeed[k] * s;
            main.startSizeMultiplier = _baseSize[k] * Mathf.Lerp(0.7f, 1.15f, s);

            int count = Mathf.Max(1, Mathf.RoundToInt(_baseCount[k] * s));
            ps.Emit(count);
        }

        // Per-effect construction values, kept as arrays so Fire can scale them without a switch.
        readonly float[] _baseSpeed = new float[6];
        readonly float[] _baseSize = new float[6];
        readonly int[] _baseCount = new int[6];

        /// <summary>
        /// One pooled system. Every effect is the same handful of decisions — how many, how fast, how big,
        /// how long, which way, and what it is drawn with — so they are written out in full rather than
        /// hidden behind a parameter struct nobody would read twice.
        /// </summary>
        ParticleSystem Create(Effect effect, int index)
        {
            var go = new GameObject($"Vfx_{effect}_{index}");
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            var renderer = go.GetComponent<ParticleSystemRenderer>();

            ParticleSystem.MainModule main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;   // a puff stays where it was made
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = false;   // everything here is fired by Emit(), never on a rate
            ParticleSystem.ShapeModule shape = ps.shape;

            int burst = RenderTier.BurstParticles;
            int k = (int)effect;

            switch (effect)
            {
                case Effect.FootDust:
                    main.startLifetime = new ParticleSystem.MinMaxCurve(0.28f, 0.5f);
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.62f, 0.6f, 0.56f, 0.32f));
                    main.gravityModifier = -0.04f;    // it drifts up as it thins, like real dust off tarmac
                    main.maxParticles = 128;
                    shape.shapeType = ParticleSystemShapeType.Cone;
                    shape.angle = 55f;
                    shape.radius = 0.06f;
                    shape.rotation = new Vector3(-90f, 0f, 0f);   // out of the ground, not into it
                    _baseSpeed[k] = 0.9f; _baseSize[k] = 0.24f; _baseCount[k] = Mathf.Max(2, burst / 12);
                    break;

                case Effect.FallDust:
                    main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.6f);
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.66f, 0.64f, 0.6f, 0.42f));
                    main.gravityModifier = -0.02f;
                    main.maxParticles = 256;
                    shape.shapeType = ParticleSystemShapeType.Hemisphere;
                    shape.radius = 0.35f;
                    _baseSpeed[k] = 1.6f; _baseSize[k] = 0.7f; _baseCount[k] = Mathf.Max(6, burst / 3);
                    break;

                case Effect.SandBurst:
                    main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 1.1f);
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.88f, 0.8f, 0.6f, 0.95f));
                    main.gravityModifier = 1.1f;      // sand comes back down; that is what makes it sand
                    main.maxParticles = 320;
                    shape.shapeType = ParticleSystemShapeType.Cone;
                    shape.angle = 32f;
                    shape.radius = 0.12f;
                    _baseSpeed[k] = 4.2f; _baseSize[k] = 0.13f; _baseCount[k] = burst;
                    break;

                case Effect.HurdleScuff:
                    main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.65f);
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.72f, 0.32f, 1f));
                    main.gravityModifier = 0.9f;
                    main.maxParticles = 128;
                    shape.shapeType = ParticleSystemShapeType.Cone;
                    shape.angle = 25f;
                    shape.radius = 0.05f;
                    _baseSpeed[k] = 5.5f; _baseSize[k] = 0.07f; _baseCount[k] = Mathf.Max(4, burst / 3);
                    break;

                case Effect.GunSmoke:
                    main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.4f);
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.86f, 0.87f, 0.9f, 0.5f));
                    main.gravityModifier = -0.08f;
                    main.maxParticles = 96;
                    shape.shapeType = ParticleSystemShapeType.Sphere;
                    shape.radius = 0.08f;
                    _baseSpeed[k] = 1.1f; _baseSize[k] = 0.55f; _baseCount[k] = Mathf.Max(5, burst / 4);
                    break;

                default:   // Confetti
                    main.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 3.4f);
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.85f, 0.25f), new Color(0.3f, 0.75f, 1f));
                    main.gravityModifier = 0.45f;
                    main.maxParticles = 400;
                    main.startRotation3D = true;
                    main.startRotationX = new ParticleSystem.MinMaxCurve(0f, 6.28f);
                    main.startRotationY = new ParticleSystem.MinMaxCurve(0f, 6.28f);
                    shape.shapeType = ParticleSystemShapeType.Cone;
                    shape.angle = 42f;
                    shape.radius = 0.4f;
                    shape.rotation = new Vector3(-90f, 0f, 0f);
                    _baseSpeed[k] = 6f; _baseSize[k] = 0.1f; _baseCount[k] = burst * 2;
                    break;
            }

            // Everything fades out rather than vanishing, and everything but confetti slows as it goes:
            // a puff that keeps its speed to the last frame reads as a spray, not as dust.
            ParticleSystem.ColorOverLifetimeModule fade = ps.colorOverLifetime;
            fade.enabled = true;
            fade.color = new ParticleSystem.MinMaxGradient(Fade());

            if (effect != Effect.Confetti)
            {
                ParticleSystem.LimitVelocityOverLifetimeModule drag = ps.limitVelocityOverLifetime;
                drag.enabled = true;
                drag.dampen = effect == Effect.SandBurst ? 0.06f : 0.22f;
            }

            if (effect == Effect.FootDust || effect == Effect.FallDust || effect == Effect.GunSmoke)
            {
                ParticleSystem.SizeOverLifetimeModule grow = ps.sizeOverLifetime;
                grow.enabled = true;
                grow.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 1.6f));
            }

            renderer.material = MaterialFor(effect);
            renderer.renderMode = effect == Effect.Confetti ? ParticleSystemRenderMode.Mesh : ParticleSystemRenderMode.Billboard;
            if (effect == Effect.Confetti) renderer.mesh = ConfettiMesh();
            renderer.alignment = ParticleSystemRenderSpace.View;
            renderer.sortingFudge = 0f;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            main.startSpeed = _baseSpeed[k];
            main.startSize = _baseSize[k];
            return ps;
        }

        Material MaterialFor(Effect effect) => effect switch
        {
            Effect.SandBurst => bank.sand,
            Effect.HurdleScuff => bank.spark,
            Effect.GunSmoke => bank.smoke,
            Effect.Confetti => bank.confetti,
            _ => bank.dust,
        };

        static Gradient Fade()
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.12f), new GradientAlphaKey(0f, 1f) });
            return g;
        }

        static Mesh _confetti;

        /// <summary>A single quad. Confetti is billboard-free on purpose: it has to tumble to read as paper.</summary>
        static Mesh ConfettiMesh()
        {
            if (_confetti != null) return _confetti;
            _confetti = new Mesh { name = "Confetti_Quad" };
            _confetti.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.28f, 0f), new Vector3(0.5f, -0.28f, 0f),
                new Vector3(0.5f, 0.28f, 0f), new Vector3(-0.5f, 0.28f, 0f),
            });
            _confetti.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) });
            _confetti.SetTriangles(new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 }, 0);   // both faces: it tumbles
            _confetti.RecalculateNormals();
            return _confetti;
        }
    }
}
