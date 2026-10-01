using UnityEngine;
using PoDecath.Audio;
using PoDecath.Cam;
using PoDecath.Env;
using PoDecath.Sim;

namespace PoDecath.Fx
{
    /// <summary>
    /// Camera flashes in the stands, and on the PC tier a second, richer layer of streamers at the finish.
    ///
    /// A crowd that is only heard and seen bobbing is still missing the thing a televised race has in every
    /// wide shot: phones and cameras going off in the stands. The pops are tiny, bright and gone in a few
    /// frames, so they cost nothing to draw, and they are the cheapest way there is to tell a viewer that
    /// this moment is the one people wanted a picture of. So they follow the moment: a trickle while the
    /// race is running that rises with the crowd's level (<see cref="RaceAudio.CrowdLevel"/>) and the
    /// race's tension (<see cref="DramaMeter.Tension"/>), and a burst at the gun, at a change of leader, at
    /// the bell and on the line, crackling across the stands for a second or two rather than all in one
    /// frame, and bunched toward the part of the stands nearest the action.
    ///
    /// Every flash comes from a spectator. The positions are the pivots of the billboard figures
    /// <see cref="CrowdStands"/> already placed on the roofs, read once out of the mesh it built, lifted to
    /// head height; a flash floating over an empty roof would give the trick away. No stands, no flashes.
    ///
    /// One pooled Shuriken system, built in code for the same reason <see cref="VfxLibrary"/> builds its
    /// own: the effect is ten numbers, and the tier budget can be applied at construction. It is Shuriken
    /// and not VFX Graph on purpose (the package was removed 2026-09-30): a <c>.vfx</c> asset cannot be
    /// authored from code, and every effect in this project is generated rather than hand-made. The phone
    /// budget is <see cref="phoneMaxFlashes"/> live pops; the desktop gets <see cref="pcMaxFlashes"/>.
    ///
    /// The streamers are PC only. On the phone the finish is exactly what it was before: the single
    /// confetti burst <see cref="RaceVfx"/> fires through <see cref="VfxLibrary"/>, plus the flashes. On the
    /// desktop two cannons either side of the line throw long-lived paper strips that climb, stall and
    /// flutter down on top of that, in the house colours of the building they are fired over.
    ///
    /// Presentation only: it reads the race, the mix and the tension, and writes none of them.
    /// </summary>
    [DefaultExecutionOrder(126)]   // after RaceAudio (120) and CrowdStands (122): this frame's level, built stands
    public class CrowdFlashes : MonoBehaviour
    {
        [Header("Wiring")]
        [Tooltip("The event. The gun, the lead, the bell and the finish are all read off it.")]
        public RaceEvent race;
        [Tooltip("The billboard crowd. Flashes only come from where a spectator is standing; without it there are none.")]
        public CrowdStands stands;
        [Tooltip("Optional. The crowd level paces the trickle and every reaction adds a small burst. "
               + "Without it the trickle stays at the quiet rate.")]
        public RaceAudio audioMix;
        [Tooltip("Optional. Tension raises the trickle before anything has happened, the way a crowd reaches "
               + "for its phones as a finish gets close.")]
        public DramaMeter drama;
        [Tooltip("The effects bank. The flash is drawn with its additive soft dot (stress) and the streamers "
               + "with its confetti material.")]
        public VfxBank bank;
        [Tooltip("Optional override for the flash. Must be additive and pass vertex colour through; the "
               + "bank's Fx_Stress is exactly that.")]
        public Material flashMaterial;

        [Header("Budget")]
        [Tooltip("Live flashes at once on the Mobile tier. Each lives a few frames, so this is a ceiling for "
               + "the bursts, not a running cost.")]
        public int phoneMaxFlashes = 40;
        [Tooltip("Live flashes at once on the PC tier.")]
        public int pcMaxFlashes = 160;
        [Tooltip("Every rate and burst count is multiplied by this on the Mobile tier.")]
        [Range(0f, 1f)] public float phoneRateScale = 0.5f;

        [Header("Trickle")]
        [Tooltip("Flashes per second from the whole crowd when it is at its quietest.")]
        public float quietRate = 0.6f;
        [Tooltip("Flashes per second when the crowd is at full voice.")]
        public float loudRate = 7f;
        [Tooltip("Extra flashes per second at full tension, on top of the crowd's level.")]
        public float tensionRate = 3f;

        [Header("Bursts")]
        [Tooltip("Flashes at the gun, spread over burstSeconds.")]
        public int gunBurst = 30;
        [Tooltip("Flashes when the lead changes hands.")]
        public int leadBurst = 22;
        [Tooltip("Flashes as the leader takes the bell.")]
        public int bellBurst = 26;
        [Tooltip("Flashes as the winner crosses. Later finishers get a third of it each.")]
        public int finishBurst = 70;
        [Tooltip("Flashes per crowd reaction at full volume (falls, recoveries and cheers, from RaceAudio).")]
        public int reactionBurst = 10;
        [Tooltip("How long a burst crackles across the stands. A real one is not one frame.")]
        public float burstSeconds = 1.6f;
        [Tooltip("Race progress below which a change of leader is ordinary jostling and gets no burst.")]
        [Range(0f, 1f)] public float leadMinProgress = 0.08f;
        [Tooltip("Seconds after one lead-change burst before another can fire, so two runners swapping the "
               + "lead every stride do not keep the stands permanently lit.")]
        public float leadCooldown = 3f;

        [Header("Look")]
        [Tooltip("World size of a flash, in metres, before the random spread. Readable at 40 m on a phone.")]
        public float flashSize = 0.42f;
        [Tooltip("Seconds a flash lives, min and max. Two to seven frames at 60 FPS.")]
        public Vector2 flashLife = new Vector2(0.04f, 0.11f);
        [Tooltip("Smallest a flash may draw, as a fraction of the viewport, so the far stands still sparkle.")]
        public float minScreenSize = 0.004f;

        [Header("Streamers (PC tier only)")]
        [Tooltip("Paper strips per finish, split between the two cannons. 0 turns the layer off.")]
        public int streamers = 90;
        [Tooltip("Metres either side of the finisher the cannons stand; just outside a 5.3 m deck.")]
        public float cannonOffset = 3.2f;
        [Tooltip("Launch speed, min and max, in m/s. Eight metres a second puts paper about three metres up.")]
        public Vector2 streamerSpeed = new Vector2(6f, 9.5f);
        [Tooltip("Seconds a strip lives, min and max. Long, so they are still coming down under the results card.")]
        public Vector2 streamerLife = new Vector2(4f, 7f);

        // Red, white, blue and a little gold: the building's colours, not any athlete's lane colour.
        static readonly Color32[] Paper =
        {
            new Color32(200, 32, 46, 255), new Color32(245, 245, 240, 255), new Color32(30, 60, 150, 255),
            new Color32(236, 190, 70, 255), new Color32(245, 245, 240, 255), new Color32(200, 32, 46, 255),
        };

        ParticleSystem _pops, _paper;
        Vector3[] _pivots;
        float _headHeight = 1.6f;

        float _trickle;                 // fractional flashes owed by the trickle
        float _burstLeft, _burstRate;   // flashes owed by bursts, and how fast they are paid
        Vector3 _focus;
        float _focusWeight;             // 0 = anywhere in the stands, 1 = bunched on the focus

        RaceEvent.Phase _lastPhase = RaceEvent.Phase.Idle;
        RaceEvent.Athlete _leader;
        float _leadReadyAt;
        bool _bellDone;
        int _knownFinished;

        int _volleyLeft;
        float _volleyAt;
        Vector3 _volleyFrom, _volleyForward;

        static Mesh _strip;

        void Start()
        {
            Material mat = flashMaterial != null ? flashMaterial : bank != null ? (bank.stress != null ? bank.stress : bank.spark) : null;
            if (race == null || stands == null || mat == null) { enabled = false; return; }
            _pops = BuildPops(mat);
            if (audioMix != null) audioMix.Reaction += OnReaction;
            race.RaceStarted += OnRaceStarted;
            RenderTier.Changed += OnTierChanged;
        }

        void OnDestroy()
        {
            if (audioMix != null) audioMix.Reaction -= OnReaction;
            if (race != null) race.RaceStarted -= OnRaceStarted;
            RenderTier.Changed -= OnTierChanged;
        }

        void OnReaction(float volume) => Burst(Mathf.RoundToInt(reactionBurst * Mathf.Clamp01(volume)), Vector3.zero, 0f);

        void OnRaceStarted()
        {
            _leader = null;
            _bellDone = false;
            _knownFinished = 0;
            _volleyLeft = 0;
            _burstLeft = 0f;
        }

        void OnTierChanged(Tier t)
        {
            if (_pops != null)
            {
                ParticleSystem.MainModule main = _pops.main;
                main.maxParticles = MaxFlashes;
            }
            // Down to the phone: the streamers go at once, rather than finishing their fall on a budget
            // that no longer allows them.
            if (RenderTier.IsMobile && _paper != null) _paper.Clear();
        }

        int MaxFlashes => Mathf.Max(1, RenderTier.IsMobile ? phoneMaxFlashes : pcMaxFlashes);
        float TierScale => RenderTier.IsMobile ? phoneRateScale : 1f;

        // ---------------------------------------------------------------- per frame

        void Update()
        {
            if (_pops == null) return;
            if (_pivots == null && !ReadStands()) return;

            // Per-attempt state is cleared by RaceStarted, not by a change of Attempt: the event counts an
            // attempt up at the finish, so keying on it would clear the finish count mid-celebration and
            // fire the winner's burst a second time.
            float dt = Time.deltaTime;
            Events();

            // The trickle. Only once there is a race to photograph; before the grid is called the stands
            // are chatting, not watching.
            if (race.Current != RaceEvent.Phase.Idle)
            {
                float level = audioMix != null ? audioMix.CrowdLevel : 0f;
                float tension = drama != null ? drama.Tension : 0f;
                float rate = (Mathf.Lerp(quietRate, loudRate, level) + tensionRate * tension) * TierScale;
                _trickle += rate * dt;
            }
            int n = Mathf.Min((int)_trickle, 8);
            _trickle -= n;
            if (_trickle > 8f) _trickle = 0f;   // a long hitch owes nothing: flashes are not back pay
            for (int i = 0; i < n; i++) Flash(Vector3.zero, 0f);

            if (_burstLeft > 0f)
            {
                // Stochastic rounding: at 60 FPS a thirty-flash burst owes half a flash a frame, and paying a
                // whole one every frame would empty it in half the time it is meant to crackle for.
                float pay = Mathf.Min(_burstLeft, _burstRate * dt);
                int whole = Mathf.Min(Mathf.FloorToInt(pay + Random.value), 24);
                _burstLeft -= pay;
                if (_burstLeft < 0.01f) _burstLeft = 0f;
                for (int i = 0; i < whole; i++) Flash(_focus, _focusWeight);
            }

            Volley();
        }

        /// <summary>The gun, a new leader, the bell and the line, each noticed once per attempt.</summary>
        void Events()
        {
            RaceEvent.Phase phase = race.Current;
            if (phase == RaceEvent.Phase.Running && _lastPhase == RaceEvent.Phase.Countdown)
                Burst(gunBurst, FieldCentre(), 0.6f);
            _lastPhase = phase;
            if (phase != RaceEvent.Phase.Running && phase != RaceEvent.Phase.Finished) return;

            RaceEvent.Athlete leader = null, firstHome = null;
            float lead = -1f;
            int finished = 0;
            foreach (RaceEvent.Athlete a in race.Athletes)
            {
                if (a.finished)
                {
                    finished++;
                    if (firstHome == null || a.time < firstHome.time) firstHome = a;
                }
                if (!a.fell && a.distance > lead) { lead = a.distance; leader = a; }
            }

            float progress = race.raceDistance > 0f ? Mathf.Clamp01(lead / race.raceDistance) : 0f;
            if (phase == RaceEvent.Phase.Running && leader != null && leader != _leader)
            {
                RaceEvent.Athlete passed = _leader;
                _leader = leader;
                // The first leader of an attempt is not a change, a fall is not a pass (the groan already
                // got its reaction), and the opening strides are jostling rather than racing.
                if (passed != null && !passed.fell && !passed.finished && progress >= leadMinProgress && Time.time >= _leadReadyAt)
                {
                    _leadReadyAt = Time.time + leadCooldown;
                    Burst(leadBurst, Where(leader), 0.7f);
                }
            }

            if (!_bellDone && leader != null && race is LapEvent lap && lap.laps >= 2 && lap.path != null
                && lead >= (lap.laps - 1) * lap.path.LapLength)
            {
                _bellDone = true;
                Burst(bellBurst, Where(leader), 0.6f);
            }

            if (finished > _knownFinished)
            {
                bool winner = _knownFinished == 0;
                _knownFinished = finished;
                RaceEvent.Athlete who = winner && firstHome != null ? firstHome : leader;
                Vector3 at = who != null ? Where(who) : FieldCentre();
                Burst(winner ? finishBurst : Mathf.Max(1, finishBurst / 3), at, winner ? 0.8f : 0.5f);
                if (winner && who != null) Streamers(who);
            }
        }

        /// <summary>
        /// Queues a crackle of <paramref name="count"/> flashes over <see cref="burstSeconds"/>, bunched on
        /// <paramref name="focus"/> by <paramref name="weight"/>. Overlapping bursts add up and the newest
        /// focus wins, which is what a crowd does when the finish lands on top of the bell.
        /// </summary>
        void Burst(int count, Vector3 focus, float weight)
        {
            if (count <= 0) return;
            float scaled = count * TierScale;
            _burstLeft += scaled;
            _burstRate = Mathf.Max(_burstRate * 0.5f, _burstLeft / Mathf.Max(0.1f, burstSeconds));
            if (weight > 0f) { _focus = focus; _focusWeight = weight; }
        }

        /// <summary>
        /// One pop. The spectator is picked at random; with a focus, the best of three random picks is kept,
        /// which bunches the flashes toward the action without sorting anything.
        /// </summary>
        void Flash(Vector3 focus, float weight)
        {
            int n = _pivots.Length;
            Vector3 p = _pivots[Random.Range(0, n)];
            if (weight > 0f && Random.value < weight)
            {
                float best = (p - focus).sqrMagnitude;
                for (int i = 0; i < 2; i++)
                {
                    Vector3 q = _pivots[Random.Range(0, n)];
                    float d = (q - focus).sqrMagnitude;
                    if (d < best) { best = d; p = q; }
                }
            }
            // Hands up at head height, a little either side of the figure.
            p += new Vector3(Random.Range(-0.3f, 0.3f), _headHeight * Random.Range(0.8f, 1.08f), Random.Range(-0.3f, 0.3f));

            // Cool xenon mostly, the odd warm phone LED.
            Color c = Random.value < 0.8f ? new Color(0.9f, 0.95f, 1f) : new Color(1f, 0.93f, 0.82f);
            var ep = new ParticleSystem.EmitParams
            {
                position = p,
                velocity = Vector3.zero,
                startSize = flashSize * Random.Range(0.6f, 1.3f),
                startLifetime = Random.Range(flashLife.x, flashLife.y),
                startColor = c,
            };
            _pops.Emit(ep, 1);
        }

        // ---------------------------------------------------------------- streamers (PC)

        /// <summary>Two cannons either side of the finisher, fired in two volleys a third of a second apart.</summary>
        void Streamers(RaceEvent.Athlete who)
        {
            if (RenderTier.IsMobile || streamers <= 0 || bank == null || bank.confetti == null) return;
            if (_paper == null) _paper = BuildPaper(bank.confetti);
            if (_paper == null) return;

            // Along the track at the line: the loop's tangent at the start arc, or the dash's own direction.
            // Not the finisher's velocity, which is already being pulled to zero as it stops.
            Vector3 at = Where(who);
            Vector3 fwd = race is LapEvent lap && lap.path != null ? lap.path.Tangent(lap.startS) : race.direction;
            fwd.y = 0f;
            _volleyForward = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.right;
            _volleyFrom = at;

            int first = Mathf.CeilToInt(streamers * 0.6f);
            FireVolley(first);
            _volleyLeft = streamers - first;
            _volleyAt = Time.time + 0.35f;
        }

        void Volley()
        {
            if (_volleyLeft <= 0 || Time.time < _volleyAt) return;
            if (RenderTier.IsMobile || _paper == null) { _volleyLeft = 0; return; }
            FireVolley(_volleyLeft);
            _volleyLeft = 0;
        }

        void FireVolley(int count)
        {
            Vector3 side = Vector3.Cross(Vector3.up, _volleyForward).normalized;
            for (int i = 0; i < count; i++)
            {
                float s = (i & 1) == 0 ? 1f : -1f;   // alternate cannons
                Vector3 from = _volleyFrom + side * (cannonOffset * s) + Vector3.up * 0.4f;
                // Up, leaning in over the deck, with a little scatter along the track.
                Vector3 v = Vector3.up * Random.Range(streamerSpeed.x, streamerSpeed.y)
                          - side * (s * Random.Range(0.6f, 2.2f))
                          + _volleyForward * Random.Range(-1.2f, 1.6f);
                bool strip = Random.value < 0.55f;
                var ep = new ParticleSystem.EmitParams
                {
                    position = from,
                    velocity = v,
                    // A strip is long and thin; the rest are squarer flakes that tumble faster.
                    startSize3D = strip ? new Vector3(Random.Range(0.03f, 0.05f), Random.Range(0.35f, 0.75f), 1f)
                                        : new Vector3(Random.Range(0.05f, 0.08f), Random.Range(0.06f, 0.1f), 1f),
                    rotation3D = new Vector3(Random.Range(0f, 360f), Random.Range(0f, 360f), Random.Range(0f, 360f)),
                    angularVelocity3D = new Vector3(Random.Range(-240f, 240f), Random.Range(-240f, 240f), Random.Range(-120f, 120f)) * (strip ? 0.6f : 1.4f),
                    startLifetime = Random.Range(streamerLife.x, streamerLife.y),
                    startColor = Paper[Random.Range(0, Paper.Length)],
                };
                _paper.Emit(ep, 1);
            }
        }

        // ---------------------------------------------------------------- construction

        /// <summary>
        /// The spectators' feet, out of the mesh <see cref="CrowdStands"/> built: four vertices per figure,
        /// all at its pivot. Read once, the first frame the stands exist; the only allocation after Start.
        /// </summary>
        bool ReadStands()
        {
            if (stands == null || stands.Count == 0) return false;
            MeshFilter mf = stands.GetComponentInChildren<MeshFilter>();
            if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) return false;
            Vector3[] verts = mf.sharedMesh.vertices;
            int figures = verts.Length / 4;
            if (figures == 0) return false;
            _pivots = new Vector3[figures];
            for (int i = 0; i < figures; i++) _pivots[i] = mf.transform.TransformPoint(verts[i * 4]);
            _headHeight = stands.figureHeight * 0.88f;
            return true;
        }

        ParticleSystem BuildPops(Material mat)
        {
            var go = new GameObject("CrowdFlashes_Pops");
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.startSpeed = 0f;
            main.gravityModifier = 0f;
            main.maxParticles = MaxFlashes;
            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = false;   // every flash is placed by Emit(), never on a rate
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = false;

            // A pop: full brightness at once, gone by the end, and shrinking as it goes, which is what makes
            // three frames read as a flash rather than as a dot that blinked.
            ParticleSystem.ColorOverLifetimeModule fade = ps.colorOverLifetime;
            fade.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.7f, 0.35f), new GradientAlphaKey(0f, 1f) });
            fade.color = new ParticleSystem.MinMaxGradient(g);
            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.35f));

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.material = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.alignment = ParticleSystemRenderSpace.View;
            r.minParticleSize = minScreenSize;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return ps;
        }

        ParticleSystem BuildPaper(Material mat)
        {
            var go = new GameObject("CrowdFlashes_Streamers");
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.startSpeed = 0f;
            main.startSize3D = true;
            main.startRotation3D = true;
            main.gravityModifier = 0.35f;
            main.maxParticles = Mathf.Max(1, streamers * 2);   // two finishes' worth overlapping
            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = false;
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = false;

            // Paper has a terminal speed of about a metre a second: it goes up fast, stalls, and then falls
            // slowly instead of dropping like the confetti's heavier cousin.
            ParticleSystem.LimitVelocityOverLifetimeModule drag = ps.limitVelocityOverLifetime;
            drag.enabled = true;
            drag.limit = new ParticleSystem.MinMaxCurve(1.3f);
            drag.dampen = 0.06f;

            // Flutter: the thing that separates a streamer from a falling rectangle.
            ParticleSystem.NoiseModule noise = ps.noise;
            noise.enabled = true;
            noise.strength = new ParticleSystem.MinMaxCurve(0.9f);
            noise.frequency = 0.45f;
            noise.scrollSpeed = 0.35f;
            noise.quality = ParticleSystemNoiseQuality.Low;

            ParticleSystem.ColorOverLifetimeModule fade = ps.colorOverLifetime;
            fade.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.85f), new GradientAlphaKey(0f, 1f) });
            fade.color = new ParticleSystem.MinMaxGradient(g);

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.material = mat;
            r.renderMode = ParticleSystemRenderMode.Mesh;
            r.mesh = Strip();
            r.alignment = ParticleSystemRenderSpace.World;   // it tumbles in the world, not toward the lens
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        /// <summary>A unit quad facing both ways; the particle's 3D size makes it a strip or a flake.</summary>
        static Mesh Strip()
        {
            if (_strip != null) return _strip;
            _strip = new Mesh { name = "Streamer_Quad" };
            _strip.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
            });
            _strip.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) });
            _strip.SetTriangles(new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 }, 0);
            _strip.RecalculateNormals();
            return _strip;
        }

        // ---------------------------------------------------------------- where things are

        static Vector3 Where(RaceEvent.Athlete a)
        {
            if (a == null) return Vector3.zero;
            if (a.IsRL && a.rig != null) return a.rig.BasePosition;
            return a.go != null ? a.go.transform.position : Vector3.zero;
        }

        Vector3 FieldCentre()
        {
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (RaceEvent.Athlete a in race.Athletes) { sum += Where(a); n++; }
            return n > 0 ? sum / n : race.startLine;
        }
    }
}
