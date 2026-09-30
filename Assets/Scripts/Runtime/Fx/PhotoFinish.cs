using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using PoDecath.Sim;

namespace PoDecath.Fx
{
    /// <summary>
    /// A photo-finish camera: a slit on the finish line, looking across the track, that records one column
    /// of pixels per frame, so the picture's horizontal axis is time rather than space. Every athlete appears
    /// in it at the moment their body crossed the line, which is how a real one settles a dead heat, and why
    /// the runners in one look stretched or squashed by their own speed.
    ///
    /// It only runs around the finish: from the moment the leader is within <see cref="armMetres"/> of the
    /// line to <see cref="tailSeconds"/> after the second finisher (or when the strip is full), so the cost is
    /// a 4 x 192 render for a few seconds per race. Columns are copied on the GPU into the strip
    /// (<c>Graphics.CopyTexture</c>), nothing is read back, and the results card shows the strip texture
    /// directly with a tick at each finisher's time.
    ///
    /// Reads the race, never writes it: the same rule as every other piece of the broadcast layer.
    /// </summary>
    public class PhotoFinish : MonoBehaviour
    {
        [Header("Wiring")]
        public RaceEvent race;
        [Tooltip("The loop, for a lap race: the line is at the event's start arc length. Empty for a straight dash.")]
        public TrackPath path;

        [Header("Camera")]
        [Tooltip("Height of the slit's view on the line, in metres from the deck: a whole standing athlete.")]
        public float viewHeight = 2.3f;
        [Tooltip("How far outside the deck edge the camera stands, looking back across it.")]
        public float standOff = 3f;
        [Tooltip("Strip height in pixels. Its width is the number of frames it can hold.")]
        public int stripHeight = 192;
        [Tooltip("Frames the strip can hold: at 60 FPS, 480 is eight seconds of finish.")]
        public int stripColumns = 480;

        [Header("When")]
        [Tooltip("Start recording when the leader is this close to the line.")]
        public float armMetres = 2.5f;
        [Tooltip("Keep recording this long after the second finisher crosses (or the first, in a field of one).")]
        public float tailSeconds = 0.6f;

        /// <summary>The finished picture, or null. Time runs left to right.</summary>
        public Texture Strip => _hasPicture ? _strip : null;
        /// <summary>Race time at the first and last recorded column, for placing a finisher on the strip.</summary>
        public float StartTime { get; private set; }
        public float EndTime { get; private set; }
        /// <summary>Recorded columns; the rest of the strip is blank and the card crops to this.</summary>
        public int Columns => _column;

        /// <summary>One body crossing the painted line while the strip was running, at the race time it crossed.</summary>
        public struct Crossing { public string name; public Color color; public float time; }

        /// <summary>
        /// Who crossed the line the camera stands on, in the order they crossed it: what the strip actually shows.
        /// Not always the result. The grid is staggered forward of the line and every runner covers the race
        /// distance from their own spot, so a back-row runner finishes a few metres past the painted line and
        /// the official order can differ from the order at the line.
        /// </summary>
        public IReadOnlyList<Crossing> Crossings => _crossings;
        readonly List<Crossing> _crossings = new List<Crossing>();
        readonly Dictionary<RaceEvent.Athlete, float> _side = new Dictionary<RaceEvent.Athlete, float>();
        Vector3 _lineAt, _lineAlong;

        RenderTexture _slit, _strip;
        Camera _cam;
        int _column;
        bool _recording, _hasPicture, _supported;
        int _lastAttempt = -1;
        float _stopAt = -1f;

        void OnEnable()
        {
            _supported = SystemInfo.copyTextureSupport != CopyTextureSupport.None;
            if (!_supported) return;
            _slit = new RenderTexture(4, stripHeight, 16, RenderTextureFormat.ARGB32) { name = "PhotoFinishSlit" };
            _strip = new RenderTexture(stripColumns, stripHeight, 0, RenderTextureFormat.ARGB32) { name = "PhotoFinishStrip" };
            _slit.Create();
            _strip.Create();

            var go = new GameObject("Photo Finish Camera");
            go.transform.SetParent(transform, false);
            _cam = go.AddComponent<Camera>();
            _cam.enabled = false;
            _cam.targetTexture = _slit;
            _cam.cullingMask = ~(1 << 5);
            _cam.nearClipPlane = 0.2f;
            _cam.farClipPlane = 60f;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.1f, 0.1f, 0.12f);
            var extra = go.AddComponent<UniversalAdditionalCameraData>();
            extra.renderShadows = false;
            extra.renderPostProcessing = true;   // the same grade as the picture, or a sunlit deck clips to white
            extra.antialiasing = AntialiasingMode.None;
        }

        /// <summary>
        /// Which side of the line each body is on this frame; a change from behind to past is a crossing,
        /// interpolated between the two frames so the tick lands where the body is in the strip.
        /// </summary>
        void TrackCrossings()
        {
            float now = race.RaceTime;
            foreach (RaceEvent.Athlete a in race.Athletes)
            {
                if (a == null) continue;
                Vector3 p = a.IsRL && a.rig != null ? a.rig.BasePosition : (a.go != null ? a.go.transform.position : _lineAt);
                float side = Vector3.Dot(p - _lineAt, _lineAlong);
                if (_side.TryGetValue(a, out float before) && before < 0f && side >= 0f && Mathf.Abs(side - before) < 2f)
                {
                    float f = before / (before - side);   // 0..1 of the way through this frame
                    _crossings.Add(new Crossing { name = a.name, color = a.color, time = Mathf.Lerp(_lastTime, now, f) });
                }
                _side[a] = side;
            }
            _lastTime = now;
        }

        float _lastTime;

        void OnDisable()
        {
            if (_cam != null) Destroy(_cam.gameObject);
            if (_slit != null) { _slit.Release(); Destroy(_slit); }
            if (_strip != null) { _strip.Release(); Destroy(_strip); }
        }

        /// <summary>Where the line is and which way the track runs through it. False when it cannot be found.</summary>
        bool Line(out Vector3 centre, out Vector3 along, out float halfWidth)
        {
            centre = along = Vector3.zero;
            halfWidth = 2.65f;
            if (race is LapEvent lap && path != null)
            {
                centre = path.Position(lap.startS, 0f);
                along = path.Tangent(lap.startS);
                halfWidth = path.deckWidth * 0.5f;
                return true;
            }
            if (race == null || race.direction.sqrMagnitude < 1e-4f) return false;
            along = race.direction.normalized;
            centre = race.startLine + along * race.raceDistance;
            halfWidth = race.laneSpacing * race.maxLanes * 0.5f;
            return true;
        }

        void LateUpdate()
        {
            if (!_supported || race == null || _cam == null) return;

            // A new race is a countdown on a new attempt, as HighlightClip reads it. Not a change of Attempt
            // alone: the event counts the attempt up at the finish, which would wipe the picture just as the
            // results card asks for it.
            if (race.Current == RaceEvent.Phase.Countdown && race.Attempt != _lastAttempt)
            {
                _lastAttempt = race.Attempt;
                _recording = false;
                _hasPicture = false;
                _column = 0;
                _stopAt = -1f;
                _crossings.Clear();
                _side.Clear();
            }
            if (race.Current != RaceEvent.Phase.Running && race.Current != RaceEvent.Phase.Finished) return;
            if (_hasPicture && !_recording) return;

            if (!_recording)
            {
                List<RaceEvent.Athlete> order = race.LiveOrder();
                if (order.Count == 0) return;
                RaceEvent.Athlete lead = order[0];
                // Armed early enough for the front row, which sits GridLength ahead of the line and so reaches
                // the painted line that much before its own finish.
                if (race.raceDistance - lead.distance > armMetres + race.GridLength && !lead.finished) return;
                if (!Line(out Vector3 c, out Vector3 along, out float half)) return;
                // Standing off the deck on one side, looking back across it along the line.
                Vector3 across = Vector3.Cross(Vector3.up, along).normalized;
                float distance = half + standOff;
                _cam.transform.position = c + across * distance + Vector3.up * (viewHeight * 0.5f);
                _cam.transform.rotation = Quaternion.LookRotation(-across, Vector3.up);
                _cam.fieldOfView = 2f * Mathf.Atan2(viewHeight * 0.55f, distance) * Mathf.Rad2Deg;
                _recording = true;
                _column = 0;
                StartTime = race.RaceTime;
                _lineAt = c;
                _lineAlong = along;
                _lastTime = race.RaceTime;
                _crossings.Clear();
                _side.Clear();
            }

            _cam.Render();
            Graphics.CopyTexture(_slit, 0, 0, _slit.width / 2, 0, 1, stripHeight, _strip, 0, 0, _column, 0);
            _column++;
            EndTime = race.RaceTime;
            _hasPicture = _column > 8;
            TrackCrossings();

            int finished = 0;
            foreach (RaceEvent.Athlete a in race.Athletes) if (a != null && a.finished) finished++;
            int needed = Mathf.Min(2, race.Athletes.Count);
            if (_stopAt < 0f && finished >= needed && needed > 0) _stopAt = race.RaceTime + tailSeconds;
            bool done = _column >= stripColumns
                     || (_stopAt > 0f && race.RaceTime >= _stopAt)
                     || race.Current == RaceEvent.Phase.Finished;
            if (done) _recording = false;
        }
    }
}
