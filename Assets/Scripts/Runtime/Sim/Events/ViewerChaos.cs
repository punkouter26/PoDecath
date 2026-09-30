using System;
using System.Collections.Generic;
using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// The viewer's hand on the race: a crosswind, a shove, a wet patch. Three buttons on the HUD, each on
    /// a cooldown, each a real physical disturbance the athletes then have to deal with — which is the
    /// point. A policy fighting to stay up against a gust, or the get-up policy hauling a runner back off
    /// the deck, is the most watchable thing this project can put on screen, and until now it only
    /// happened by accident.
    ///
    /// This is the one component in the presentation layer that is allowed to push on a body, and it is
    /// held to two rules that keep it honest:
    ///
    ///  - **External forces only.** Nothing here touches a drive, a gain, a limit or a policy. The athlete
    ///    is exactly the athlete it was trained as; the world around it gets rougher. (AGENTS.md: the
    ///    broadcast layer must never change how a body behaves. A gust changes what the body is subjected
    ///    to, which is a different thing, and the one a real race has.)
    ///
    ///  - **Earth-sized numbers (house rule 14).** The gust is aerodynamic drag, ½ ρ C<sub>d</sub> A v²,
    ///    at sea-level air density on a runner's side-on area: 14 m/s is a Beaufort 7 gale and gives about
    ///    65 N, just under 1 m/s² on a 70 kg body. The shove is a change of 0.9 m/s at the chest, about the
    ///    impulse of a hard two-handed push. The wet patch is painted asphalt in the rain, μ ≈ 0.3 against
    ///    the 1.0 the deck is trained on.
    ///
    /// The wet patch is a real collider, 3 mm proud of the deck, on the default layer, so it collides with
    /// every body part (house rule 20) — nothing passes through it and nothing is switched off to fit it.
    /// </summary>
    [DefaultExecutionOrder(-20)]
    public class ViewerChaos : MonoBehaviour
    {
        public enum Act { Gust, Shove, Slick }

        [Header("Wiring")]
        public RaceEvent race;
        [Tooltip("Where a wet patch goes on the loop. Without it (and without a pit) SLICK is unavailable.")]
        public TrackPath path;
        [Tooltip("For the long jump: the patch goes on the runway instead of the loop.")]
        public LongJumpPit pit;

        [Header("Look (built by PoDecath/Bake Kenney Effects)")]
        public Material gustMaterial;
        public Material shoveMaterial;
        public Material slickMaterial;

        [Header("Gust: drag on the whole body")]
        [Tooltip("Peak wind speed, m/s. 14 is Beaufort 7, a near gale: hard to walk into.")]
        public float gustSpeed = 14f;
        public float gustSeconds = 1.8f;
        [Tooltip("Air density at sea level, kg/m³.")]
        public float airDensity = 1.225f;
        [Tooltip("Drag coefficient of an upright human, broadside. 1.0-1.3 in wind-tunnel data.")]
        public float dragCoefficient = 1.1f;
        [Tooltip("Side-on area of an adult runner, m². About 0.5-0.6.")]
        public float frontalArea = 0.55f;

        [Header("Shove: one push at the chest")]
        [Tooltip("Velocity change the push gives the whole body's mass, m/s. The impulse is this times the "
               + "athlete's own mass, so a heavier body is harder to move, as it should be.")]
        public float shoveDeltaV = 0.9f;

        [Header("Slick: a wet patch on the course")]
        public float slickLength = 3.2f;
        [Tooltip("Friction of the wet patch. The combine is Minimum, so this is what a foot actually gets.")]
        [Range(0.05f, 1f)] public float slickFriction = 0.3f;
        [Tooltip("How far ahead of the leader the patch is laid, metres of track.")]
        public float slickAhead = 7f;
        [Tooltip("How long a patch lasts before it dries.")]
        public float slickSeconds = 14f;

        [Header("Pacing")]
        [Tooltip("Seconds before the same button can be pressed again.")]
        public float cooldown = 5f;

        /// <summary>Raised when an act goes off. The athlete is who it was aimed at, or null for everyone.</summary>
        public event Action<Act, RaceEvent.Athlete> Fired;

        /// <summary>How many of each have gone off this attempt, for the race log and the commentary.</summary>
        public int Count(Act act) => _counts[(int)act];

        public bool GustActive => _gustLeft > 0f;
        public Vector3 GustDirection => _gustDir;

        readonly float[] _ready = new float[3];
        readonly int[] _counts = new int[3];
        readonly Dictionary<ArticulationBody, Link[]> _links = new Dictionary<ArticulationBody, Link[]>();

        struct Link
        {
            public ArticulationBody body;
            public float share;   // this link's fraction of the body's mass
        }

        float _gustLeft;
        Vector3 _gustDir;
        ParticleSystem _gustFx, _shoveFx;
        GameObject _slick;
        Material _sheen;
        Color _sheenColor;
        float _slickLeft;
        int _lastAttempt = -1;
        PhysicsMaterial _wet;

        // ---------------------------------------------------------------- the buttons

        /// <summary>Whether a press would do anything right now.</summary>
        public bool CanFire(Act act)
        {
            if (race == null || race.Current != RaceEvent.Phase.Running) return false;
            if (Time.time < _ready[(int)act]) return false;
            if (act == Act.Slick) return path != null || pit != null;
            if (act == Act.Shove) return Target() != null;
            return true;
        }

        /// <summary>Seconds until the button is ready again; 0 when it is.</summary>
        public float CooldownLeft(Act act) => Mathf.Max(0f, _ready[(int)act] - Time.time);

        public void Fire(Act act)
        {
            if (!CanFire(act)) return;
            _ready[(int)act] = Time.time + cooldown;
            _counts[(int)act]++;
            RaceEvent.Athlete who = null;
            switch (act)
            {
                case Act.Gust: StartGust(); break;
                case Act.Shove: who = Shove(); break;
                case Act.Slick: who = LaySlick(); break;
            }
            Fired?.Invoke(act, who);
        }

        // ---------------------------------------------------------------- lifecycle

        void Awake()
        {
            _wet = new PhysicsMaterial("Wet deck")
            {
                dynamicFriction = slickFriction,
                staticFriction = slickFriction * 1.15f,
                bounciness = 0f,
                // Minimum, not the project's average: water on a deck wins whatever the sole is made of.
                frictionCombine = PhysicsMaterialCombine.Minimum,
                bounceCombine = PhysicsMaterialCombine.Minimum,
            };
            _gustFx = BuildGustFx();
            _shoveFx = BuildShoveFx();
        }

        void Update()
        {
            if (race == null) return;
            if (race.Attempt != _lastAttempt || race.Current == RaceEvent.Phase.Countdown)
            {
                // A new race starts on a dry deck in still air. Checked on the countdown as well as the
                // attempt counter, because a restart mid-race does not bump the counter.
                if (race.Attempt != _lastAttempt) { _lastAttempt = race.Attempt; Array.Clear(_counts, 0, _counts.Length); }
                ClearAll();
            }

            if (_slick != null)
            {
                _slickLeft -= Time.deltaTime;
                if (_slickLeft <= 0f) ClearSlick();
                else
                {
                    // Dries from the edges in: the last two seconds fade the sheen out before the collider goes.
                    if (_sheen != null && _sheen.HasProperty("_BaseColor"))
                    {
                        Color c = _sheenColor;
                        c.a *= Mathf.Clamp01(_slickLeft / 2f);
                        _sheen.SetColor("_BaseColor", c);
                    }
                }
            }
        }

        void FixedUpdate()
        {
            if (_gustLeft <= 0f || race == null) return;
            _gustLeft -= Time.fixedDeltaTime;
            // Rises and falls rather than switching: a gust has a front and a tail, and a square pulse of
            // force is a kick, not weather.
            float t = 1f - Mathf.Clamp01(_gustLeft / Mathf.Max(0.01f, gustSeconds));
            float envelope = Mathf.Sin(Mathf.PI * t);
            Vector3 wind = _gustDir * (gustSpeed * envelope);

            foreach (RaceEvent.Athlete a in race.Athletes)
            {
                if (!Exposed(a)) continue;
                Link[] links = LinksOf(a);
                // Drag acts on the air speed relative to the body, so a runner already drifting with the
                // wind feels less of it, and one running into it feels more.
                Vector3 v = a.rig.BaseLinearVelocityWorld; v.y = 0f;
                Vector3 rel = wind - Vector3.Project(v, _gustDir);
                Vector3 drag = 0.5f * airDensity * dragCoefficient * frontalArea * rel.magnitude * rel;
                // Spread by mass, which is a fair stand-in for spread by area on a body this shape, and
                // keeps the whole figure being blown rather than one link being yanked.
                for (int i = 0; i < links.Length; i++)
                    if (links[i].body != null) links[i].body.AddForce(drag * links[i].share, ForceMode.Force);
            }

            if (_gustLeft <= 0f && _gustFx != null) _gustFx.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        void OnDestroy()
        {
            if (_wet != null) Destroy(_wet);
        }

        /// <summary>Somebody the weather can still act on: on its feet or getting up, not parked past the line.</summary>
        static bool Exposed(RaceEvent.Athlete a) =>
            a != null && a.IsRL && a.rig != null && a.rig.root != null && !a.finished && !a.stopping && !a.rig.root.immovable;

        // ---------------------------------------------------------------- gust

        void StartGust()
        {
            // Across the track, not along it: a tail wind just makes the race quicker, and a crosswind on a
            // 5.3 m deck with a rail on each side is the one that makes a runner fight for its line.
            RaceEvent.Athlete lead = Leader();
            Vector3 fwd = lead != null ? lead.rig.BaseForward : Vector3.right;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.right;
            Vector3 across = Vector3.Cross(Vector3.up, fwd.normalized) * (UnityEngine.Random.value < 0.5f ? -1f : 1f);
            _gustDir = (Quaternion.Euler(0f, UnityEngine.Random.Range(-25f, 25f), 0f) * across).normalized;
            _gustLeft = gustSeconds;

            if (_gustFx != null)
            {
                Vector3 centre = lead != null ? lead.rig.BasePosition : transform.position;
                _gustFx.transform.SetPositionAndRotation(centre - _gustDir * 6f + Vector3.up * 0.2f,
                                                        Quaternion.LookRotation(_gustDir, Vector3.up));
                var main = _gustFx.main;
                main.startSpeed = gustSpeed;
                _gustFx.Play(true);
            }
        }

        // ---------------------------------------------------------------- shove

        RaceEvent.Athlete Shove()
        {
            RaceEvent.Athlete a = Target();
            if (a == null) return null;
            Link[] links = LinksOf(a);
            float mass = 0f;
            ArticulationBody chest = null;
            foreach (Link l in links)
            {
                if (l.body == null) continue;
                mass += l.body.mass;
                if (l.body.name == "torso") chest = l.body;
            }
            if (chest == null) chest = a.rig.root;

            Vector3 fwd = a.rig.BaseForward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.right;
            Vector3 dir = Vector3.Cross(Vector3.up, fwd.normalized) * (UnityEngine.Random.value < 0.5f ? -1f : 1f);
            // At the chest, not the centre of mass: a push lands on the torso and tips the body as well as
            // moving it, which is what makes it hard to stand up to.
            chest.AddForce(dir * (mass * shoveDeltaV), ForceMode.Impulse);

            if (_shoveFx != null)
            {
                _shoveFx.transform.position = chest.transform.position - dir * 0.25f;
                _shoveFx.Emit(14);
            }
            return a;
        }

        /// <summary>Who gets shoved: the leader, while it is on its feet. Pushing the runner already down is not a game.</summary>
        RaceEvent.Athlete Target()
        {
            if (race == null) return null;
            foreach (RaceEvent.Athlete a in race.LiveOrder())
                if (Exposed(a) && !a.fell && !a.recovering) return a;
            return null;
        }

        RaceEvent.Athlete Leader()
        {
            if (race == null) return null;
            foreach (RaceEvent.Athlete a in race.LiveOrder())
                if (a != null && a.IsRL && !a.fell && !a.finished) return a;
            return null;
        }

        // ---------------------------------------------------------------- slick

        RaceEvent.Athlete LaySlick()
        {
            ClearSlick();
            RaceEvent.Athlete lead = Leader();
            Vector3 centre, along;
            float width, deckY;
            if (path != null)
            {
                float s = lead != null && lead.follower != null ? lead.follower.S : path.ProjectGlobal(lead != null ? lead.rig.BasePosition : path.transform.position);
                s += slickAhead;
                centre = path.Position(s, 0f);
                along = path.Tangent(s);
                width = path.deckWidth * 0.8f;
                deckY = path.deckTopY;
            }
            else if (pit != null)
            {
                float x = pit.takeoffX - 5f;
                centre = pit.Point(x, 0f);
                along = pit.Point(x + 1f, 0f) - centre;
                width = pit.runwayWidth;
                deckY = pit.surfaceY;
            }
            else return null;

            along.y = 0f;
            if (along.sqrMagnitude < 1e-4f) along = Vector3.right;
            Quaternion rot = Quaternion.LookRotation(along.normalized, Vector3.up);

            _slick = new GameObject("WetPatch");
            _slick.transform.SetPositionAndRotation(new Vector3(centre.x, deckY, centre.z), rot);

            // The collider: 3 mm proud of the deck, the top of it being what a foot lands on. Thin enough
            // that the step on to it is below anything a stride notices; a real puddle is deeper.
            const float proud = 0.003f, thick = 0.02f;
            var box = _slick.AddComponent<BoxCollider>();
            box.size = new Vector3(width, thick, slickLength);
            box.center = new Vector3(0f, proud - thick * 0.5f, 0f);
            box.sharedMaterial = _wet;

            if (slickMaterial != null)
            {
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Destroy(quad.GetComponent<Collider>());
                quad.name = "Sheen";
                quad.transform.SetParent(_slick.transform, false);
                quad.transform.localPosition = new Vector3(0f, proud + 0.002f, 0f);
                quad.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                quad.transform.localScale = new Vector3(width * 1.1f, slickLength * 1.15f, 1f);
                var r = quad.GetComponent<MeshRenderer>();
                // Its own copy, because it fades as it dries; destroyed with the patch in ClearSlick.
                _sheen = new Material(slickMaterial);
                _sheenColor = _sheen.HasProperty("_BaseColor") ? _sheen.GetColor("_BaseColor") : Color.white;
                r.sharedMaterial = _sheen;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            _slickLeft = slickSeconds;
            return lead;
        }

        void ClearSlick()
        {
            if (_slick != null) Destroy(_slick);
            if (_sheen != null) Destroy(_sheen);
            _slick = null;
            _sheen = null;
        }

        void ClearAll()
        {
            _gustLeft = 0f;
            if (_gustFx != null) _gustFx.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ClearSlick();
        }

        // ---------------------------------------------------------------- helpers

        Link[] LinksOf(RaceEvent.Athlete a)
        {
            ArticulationBody root = a.rig.root;
            if (_links.TryGetValue(root, out Link[] cached)) return cached;
            ArticulationBody[] bodies = root.GetComponentsInChildren<ArticulationBody>();
            float total = 0f;
            foreach (ArticulationBody b in bodies) total += b.mass;
            var links = new Link[bodies.Length];
            for (int i = 0; i < bodies.Length; i++)
                links[i] = new Link { body = bodies[i], share = total > 0f ? bodies[i].mass / total : 1f / bodies.Length };
            _links[root] = links;
            return links;
        }

        /// <summary>Streaks blown across the deck: stretched billboards moving at the wind's own speed.</summary>
        ParticleSystem BuildGustFx()
        {
            if (gustMaterial == null) return null;
            var go = new GameObject("GustFx");
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = true;
            main.duration = 1f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.1f);
            main.startSpeed = gustSpeed;
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.2f);
            main.startColor = new Color(1f, 1f, 1f, 0.35f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 160;
            var emission = ps.emission;
            emission.rateOverTime = 70f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(14f, 1.8f, 0.5f);   // a sheet across the deck, 1.8 m tall
            shape.position = new Vector3(0f, 0.9f, 0f);
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.4f, 0.25f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Stretch;
            r.velocityScale = 0.12f;
            r.lengthScale = 2f;
            r.sharedMaterial = gustMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return ps;
        }

        /// <summary>A short starburst where the push lands.</summary>
        ParticleSystem BuildShoveFx()
        {
            if (shoveMaterial == null) return null;
            var go = new GameObject("ShoveFx");
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.18f, 0.35f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f, 3.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.15f, 0.4f);
            main.startColor = new Color(1f, 0.85f, 0.45f, 0.9f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 40;
            var emission = ps.emission;
            emission.enabled = false;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.1f;
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = shoveMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return ps;
        }
    }
}
