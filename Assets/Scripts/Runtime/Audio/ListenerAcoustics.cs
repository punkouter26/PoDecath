using UnityEngine;
using PoDecath.Cam;
using PoDecath.Sim;

namespace PoDecath.Audio
{
    /// <summary>
    /// What the room does to everything the listener hears: the reverb of a stone courtyard, the dullness
    /// of distance, and the Doppler a chase camera earns.
    ///
    /// The White House rooftop is a hard surface surrounded by stone wings and a portico, so anything loud
    /// on it comes back — the pistol synthesis already fakes three slap-backs off the facade for exactly
    /// this reason, and a real reverb zone generalises that to every cue without baking it into each clip.
    ///
    /// The low pass is the other half. Air absorbs high frequencies over distance; a crowd forty metres
    /// below a rooftop deck is duller as well as quieter, and without that the wide shot sounds identical
    /// to the close-up at a lower volume, which is the giveaway that nothing is really far away.
    /// </summary>
    [RequireComponent(typeof(AudioListener))]
    [DefaultExecutionOrder(150)]
    public class ListenerAcoustics : MonoBehaviour
    {
        [Header("Distance")]
        [Tooltip("What the camera is listening to. The featured athlete if a race is wired, else the "
               + "listener's own position, which makes the low pass a no-op.")]
        public RaceEvent race;
        [Tooltip("Closer than this, nothing is filtered.")]
        public float nearDistance = 12f;
        [Tooltip("At this distance the low pass is fully closed to closedHz.")]
        public float farDistance = 60f;
        public float openHz = 22000f;
        public float closedHz = 4200f;
        [Tooltip("Seconds for the filter to follow a cut. A cut is instant; the filter should not be, or "
               + "every cut clicks.")]
        public float glide = 0.25f;

        [Header("Occlusion")]
        [Tooltip("Optional. With it the filter follows whoever the gallery is on rather than the leader.")]
        public BroadcastDirector director;
        [Tooltip("What can stand between the camera and the athlete: the building, the deck, the rails. "
               + "The builder clears the Creature layer out of it so a runner cannot occlude itself.")]
        public LayerMask occluders = ~0;
        [Tooltip("Cutoff while something solid is in the way.")]
        public float occludedHz = 1400f;
        [Tooltip("How far the effects bus is pulled down while occluded.")]
        [Range(0f, 1f)] public float occlusionDuck = 0.6f;

        [Header("Room")]
        [Tooltip("Reverb over the deck. Off for a scene that is not on the roof.")]
        public bool reverb = true;
        public float reverbRadius = 90f;

        AudioLowPassFilter _lowPass;
        float _cutoff;

        void Start()
        {
            _lowPass = gameObject.GetComponent<AudioLowPassFilter>();
            if (_lowPass == null) _lowPass = gameObject.AddComponent<AudioLowPassFilter>();
            _lowPass.cutoffFrequency = openHz;
            _lowPass.lowpassResonanceQ = 1f;
            _cutoff = openHz;

            if (!reverb) return;
            var zone = gameObject.AddComponent<AudioReverbZone>();
            zone.minDistance = reverbRadius * 0.35f;
            zone.maxDistance = reverbRadius;
            // A stone courtyard: short pre-delay, a real tail, and not much of it in the highs, because
            // the surfaces are hard but the space is open to the sky above.
            zone.reverbPreset = AudioReverbPreset.StoneCorridor;
            zone.room = -900;
            zone.roomHF = -1400;
            zone.decayTime = 1.9f;
            zone.reflections = -600;
            zone.reverb = -300;
        }

        void Update()
        {
            if (_lowPass == null) return;

            float target = openHz;
            Vector3 subject;
            if (Subject(out subject))
            {
                float d = Vector3.Distance(transform.position, subject);
                float t = Mathf.Clamp01(Mathf.InverseLerp(nearDistance, farDistance, d));
                // Logarithmic in frequency, not linear: hearing is, and a linear sweep spends most of its
                // travel in the range nothing is happening in.
                target = Mathf.Exp(Mathf.Lerp(Mathf.Log(openHz), Mathf.Log(closedHz), t));
                // Occlusion: a line from the listener to the athlete's chest that hits something solid
                // means the sound is coming round the building rather than across the deck.
                bool occluded = Physics.Linecast(transform.position, subject + Vector3.up * 0.9f, occluders, QueryTriggerInteraction.Ignore);
                if (occluded) { target = Mathf.Min(target, occludedHz); AudioMix.Duck(AudioMix.Bus.Sfx, occlusionDuck); }
                else AudioMix.Release(AudioMix.Bus.Sfx);
            }

            float k = glide > 0f ? 1f - Mathf.Exp(-Time.unscaledDeltaTime / glide) : 1f;
            _cutoff = Mathf.Lerp(_cutoff, target, k);
            _lowPass.cutoffFrequency = _cutoff;
        }

        /// <summary>Whatever the shot is about; distance to it is what the filter follows.</summary>
        bool Subject(out Vector3 position)
        {
            position = Vector3.zero;
            if (race == null) return false;
            RaceEvent.Athlete a = director != null ? director.Featured : null;
            if (a == null) foreach (RaceEvent.Athlete candidate in race.Athletes)
            {
                if (candidate.fell || candidate.finished) continue;
                if (a == null || candidate.distance > a.distance) a = candidate;
            }
            if (a == null) a = race.Reference;
            if (a == null) return false;
            position = a.IsRL ? a.rig.BasePosition : (a.go != null ? a.go.transform.position : Vector3.zero);
            return true;
        }
    }
}
