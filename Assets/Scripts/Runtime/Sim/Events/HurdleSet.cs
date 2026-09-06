using System.Collections.Generic;
using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Puts hurdles on the rooftop loop and keeps the book on what happens to them.
    ///
    /// Nothing about the athletes changes for this event. No policy here has been trained to clear
    /// anything, and none is helped over: a hurdle is a rigid body with mass on the deck, an athlete runs
    /// into it, and whichever way that goes is what happens. A runner that keeps its balance carries on;
    /// one that does not goes down and is a DNF like any other fall, because the get-up policy is still a
    /// placeholder. Training a take-off for these is the next job, and this is the course it will run on.
    ///
    /// Hurdles go on the straights only. The bends have an 8.8 m radius and the whole deck is 5.3 m wide,
    /// so a hurdle on a curve would sit at an angle across a lane that is already fighting the barrier.
    /// The first straight also has the starting grid on it — up to sixteen runners in eight rows — so the
    /// grid, plus a clearance, is kept free: a hurdle two metres in front of a standing start is a wall,
    /// not an obstacle.
    ///
    /// Knocked hurdles stay down for the rest of the race. Over four laps that means the course opens up
    /// as it goes, which is exactly what happens on a real track, and it is why the results count how many
    /// each runner put down rather than penalising them.
    /// </summary>
    [DefaultExecutionOrder(-20)]
    public class HurdleSet : MonoBehaviour
    {
        [Header("Wiring")]
        public TrackPath path;
        public RaceEvent race;
        public Material frameMaterial;
        public Material barMaterial;
        [Tooltip("Played from the hurdle itself as it goes over.")]
        public AudioClip clatter;
        [Tooltip("The lighter sound of a hurdle clipped but left standing.")]
        public AudioClip clip;

        [Header("When")]
        [Tooltip("Ignore the event picker and always build the hurdles. For working on them in the editor.")]
        public bool alwaysOn = false;

        [Header("Course")]
        [Tooltip("Most hurdles on one straight. The straights are 22.4 m, so five is already tight.")]
        public int maxPerStraight = 4;
        [Tooltip("Gap between hurdles along a straight. Regulation is 9.14 m, which does not fit on a 22 m one.")]
        public float spacing = 5f;
        [Tooltip("Clear road between the front of the starting grid and the first hurdle.")]
        public float clearanceAfterGrid = 4f;
        [Tooltip("Clear road between the last hurdle on a straight and the bend it feeds into.")]
        public float endMargin = 2.5f;

        [Header("Hurdle")]
        [Tooltip("Bar height. Regulation is 1.067 m for the men's 110 m, which stops an athlete that "
               + "cannot jump dead every single time; 0.762 m is the lowest regulation height there is "
               + "(the women's 400 m) and leaves a runner that keeps its feet a chance of carrying on.")]
        public float height = 0.762f;
        public float mass = 9f;
        [Tooltip("How far short of the deck edges the bar stops, so it never fouls the barrier.")]
        public float widthMargin = 0.5f;
        [Tooltip("Half the length of the feet, along the direction of travel.")]
        public float footReach = 0.28f;
        [Tooltip("Slows a knocked hurdle so it lands and stays where it was hit instead of sliding on.")]
        public float linearDamping = 0.8f;
        public float angularDamping = 1.2f;

        /// <summary>How many hurdles are on the course. Zero when the event is not a hurdles event.</summary>
        public int Count => _hurdles.Count;

        /// <summary>How many are currently down.</summary>
        public int Down
        {
            get
            {
                int n = 0;
                foreach (Hurdle h in _hurdles) if (h != null && h.Knocked) n++;
                return n;
            }
        }

        readonly List<Hurdle> _hurdles = new List<Hurdle>();
        readonly Dictionary<RaceEvent.Athlete, int> _knocks = new Dictionary<RaceEvent.Athlete, int>();
        readonly List<float> _marks = new List<float>();

        /// <summary>How many hurdles this athlete has put down in the current attempt.</summary>
        public int KnockedBy(RaceEvent.Athlete a) => a != null && _knocks.TryGetValue(a, out int n) ? n : 0;

        void Awake()
        {
            if (!alwaysOn && !SessionSettings.Hurdles) return;
            if (path == null || race == null)
            {
                Debug.LogError("[HurdleSet] No TrackPath or event assigned; no hurdles built.", this);
                return;
            }

            Build();
            GiveBotsSomethingToHitWith();
            race.RaceStarted += StandThemAllUp;
            if (race is LapEvent lap) lap.hurdles = this;   // so the results can say what each runner did to them
        }

        void OnDestroy()
        {
            if (race != null) race.RaceStarted -= StandThemAllUp;
        }

        // ---------------------------------------------------------------- placement

        void Build()
        {
            float straight = path.StraightLength, arc = path.ArcLength;
            _marks.Clear();
            Fill(0f, straight, _marks);                                  // the straight the grid is on
            Fill(straight + arc, 2f * straight + arc, _marks);           // the back straight

            float startS = race is LapEvent lap ? lap.startS : 0f;
            float gridEnd = startS + race.GridLength + clearanceAfterGrid;

            int placed = 0;
            foreach (float s in _marks)
            {
                if (Between(s, startS - 1f, gridEnd)) continue;          // never on the grid itself
                _hurdles.Add(BuildHurdle(s, placed++));
            }
            Debug.Log($"[HurdleSet] {placed} hurdles on the straights at {height:F3} m, mass {mass:F0} kg.");
        }

        /// <summary>Spreads as many hurdles as will fit down one straight, centred in the room available.</summary>
        void Fill(float from, float to, List<float> into)
        {
            float lo = from + endMargin, hi = to - endMargin;
            float span = hi - lo;
            if (span <= 0f) return;
            int n = Mathf.Clamp(Mathf.FloorToInt(span / Mathf.Max(0.5f, spacing)) + 1, 1, Mathf.Max(1, maxPerStraight));
            float used = (n - 1) * spacing;
            float first = lo + (span - used) * 0.5f;
            for (int i = 0; i < n; i++) into.Add(first + i * spacing);
        }

        /// <summary>Whether an arc length falls in a window, allowing for the window wrapping the lap.</summary>
        bool Between(float s, float lo, float hi)
        {
            float lap = path.LapLength;
            float d = Mathf.Repeat(s - lo, lap);
            return d <= Mathf.Repeat(hi - lo, lap);
        }

        Hurdle BuildHurdle(float s, int index)
        {
            Vector3 pos = path.Position(s, 0f);
            Vector3 tan = path.Tangent(s);
            Vector3 flat = new Vector3(tan.x, 0f, tan.z);
            Quaternion rot = flat.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(flat.normalized, Vector3.up) : Quaternion.identity;

            var go = new GameObject($"Hurdle {index + 1}");
            go.transform.SetParent(transform, false);
            go.transform.SetPositionAndRotation(pos, rot);

            // Local frame: +Z is the way the athletes are running, so the bar spans local X.
            float span = Mathf.Max(0.8f, path.deckWidth - 2f * widthMargin);
            float halfSpan = span * 0.5f;
            const float postThick = 0.055f;

            Part(go.transform, "Bar", new Vector3(0f, height, 0f), new Vector3(span, 0.07f, 0.06f), barMaterial);
            Part(go.transform, "PostL", new Vector3(-halfSpan, height * 0.5f, 0f), new Vector3(postThick, height, postThick), frameMaterial);
            Part(go.transform, "PostR", new Vector3(halfSpan, height * 0.5f, 0f), new Vector3(postThick, height, postThick), frameMaterial);
            // Feet point back down the track, the way a real hurdle's do: easy to tip forwards, hard to walk backwards.
            Part(go.transform, "FootL", new Vector3(-halfSpan, 0.03f, -footReach), new Vector3(postThick, 0.06f, footReach * 2f), frameMaterial);
            Part(go.transform, "FootR", new Vector3(halfSpan, 0.03f, -footReach), new Vector3(postThick, 0.06f, footReach * 2f), frameMaterial);

            var body = go.AddComponent<Rigidbody>();
            body.mass = mass;
            // A knocked hurdle should clatter down and stop, not skate off down the deck. The deck is a
            // plain mesh collider with default friction, which on its own lets a light frame slide a long
            // way from a fast hit.
            body.linearDamping = linearDamping;
            body.angularDamping = angularDamping;

            var hurdle = go.AddComponent<Hurdle>();
            hurdle.clatter = clatter;
            hurdle.clip = clip;
            hurdle.SetMark(pos, rot);
            hurdle.KnockedOver += OnKnockedOver;
            return hurdle;
        }

        static void Part(Transform parent, string name, Vector3 localPos, Vector3 size, Material mat)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            if (mat != null) go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        /// <summary>
        /// The RED bot has no physics at all — it is a transform on a pace profile — so without a body of
        /// its own it would run straight through every hurdle while the RL field piles into them. A
        /// kinematic capsule gives it something to knock things over with. It goes on the Creature layer,
        /// where self-collision is already off, so the bot bowls hurdles over without ever barging an
        /// athlete: it cannot be pushed off its line, and it should not be able to push anyone off theirs.
        /// </summary>
        void GiveBotsSomethingToHitWith()
        {
            // Nothing to do any more, and that is the point.
            //
            // This used to give the heuristic bot a capsule and a kinematic Rigidbody so that a body
            // with no physics at all had something to knock a hurdle over with, swept rather than
            // teleported so PhysX did not shove the hurdle metres down the track. The bot is a real
            // articulated athlete now, with the same colliders as everyone else, so it hits a hurdle
            // with its actual shins.
        }

        // ---------------------------------------------------------------- the book

        void OnKnockedOver(Hurdle h)
        {
            RaceEvent.Athlete by = Culprit(h);
            if (by == null) return;
            _knocks.TryGetValue(by, out int n);
            _knocks[by] = n + 1;
        }

        RaceEvent.Athlete Culprit(Hurdle h)
        {
            foreach (RaceEvent.Athlete a in race.Athletes)
            {
                if (h.ByRig != null && a.rig == h.ByRig) return a;
                if (h.ByBot != null && a.heuristic == h.ByBot) return a;
            }
            return null;
        }

        void StandThemAllUp()
        {
            _knocks.Clear();
            foreach (Hurdle h in _hurdles) if (h != null) h.StandUp();
        }
    }
}
