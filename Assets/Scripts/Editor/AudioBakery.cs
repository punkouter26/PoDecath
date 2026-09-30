using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using PoDecath.Audio;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// Bakes the game's whole sound set from scratch into <c>Assets/Audio/</c> and points an
    /// <see cref="AudioBank"/> at it.
    ///
    /// The project ships no recordings, so every clip here is synthesised: filtered noise for anything
    /// crowd- or contact-shaped, decaying inharmonic partials for the bell, a noise transient over a low
    /// thump for the pistol. It is not a sample library and does not pretend to be one — it is a complete,
    /// committed, reproducible placeholder set that makes the game audible today, and every clip is one
    /// drag-and-drop away from being replaced by a real recording on the bank.
    ///
    /// Everything is deterministic: each clip draws from its own fixed seed, so re-baking produces
    /// identical files and does not churn the repository.
    /// </summary>
    public static class AudioBakery
    {
        const string AudioDir = "Assets/Audio";
        const string BankPath = AudioDir + "/AudioBank.asset";
        const int Rate = 44100;

        [MenuItem("PoDecath/Bake Audio Clips", priority = 6)]
        public static void Bake()
        {
            AudioBank bank = BakeBank();
            Selection.activeObject = bank;
            EditorGUIUtility.PingObject(bank);
        }

        /// <summary>
        /// The bank, freshly resolved. Always load it through this rather than holding a reference across
        /// <see cref="UnityEditor.SceneManagement.EditorSceneManager.NewScene"/>: opening a scene unloads
        /// unused assets, and a wrapper that survives that still serialises correctly (it keeps a valid
        /// instance id) while comparing equal to null — so a held reference writes good references into the
        /// scene but sends every `bank != null` guard down the wrong branch.
        /// </summary>
        public static AudioBank LoadBank() => AssetDatabase.LoadAssetAtPath<AudioBank>(BankPath);

        /// <summary>Bakes every clip and returns the bank, creating both if they do not exist yet.</summary>
        public static AudioBank BakeBank()
        {
            PolicyLibraryTools.EnsureFolder(AudioDir);

            var bank = AssetDatabase.LoadAssetAtPath<AudioBank>(BankPath);
            if (bank == null)
            {
                bank = ScriptableObject.CreateInstance<AudioBank>();
                AssetDatabase.CreateAsset(bank, BankPath);
            }

            // Slots already holding real recordings (PoDecath/Audio/Import Sound Pack, which writes under
            // Assets/Audio/Real) are put back after the synthesis below. Every scene build runs this bake, so
            // without it a rebuild silently swapped the imported crowd, pistol and bell back to the
            // synthesised placeholders.
            var real = RealSlots(bank);

            bank.crowdBed = Save("crowd_bed", CrowdBed());
            bank.crowdSwell = Save("crowd_swell", CrowdSwell());
            bank.crowdGroan = Save("crowd_groan", CrowdGroan());
            bank.pistol = Save("pistol", Pistol());
            bank.countBeep = Save("count_beep", CountBeep());
            bank.lapBell = Save("lap_bell", LapBell());
            bank.crowdApplause = Save("crowd_applause", CrowdApplause());
            bank.crowdChant = Save("crowd_chant", CrowdChant());
            bank.windBed = Save("wind_bed", WindBed());
            bank.hurdleClatter = Save("hurdle_clatter", HurdleClatter());
            bank.hurdleClip = Save("hurdle_clip", HurdleClip());
            bank.sandThud = Save("sand_thud", SandThud());
            bank.whoosh = Save("whoosh", Whoosh());
            bank.sting = Save("sting", Sting());
            bank.breath = Save("breath", Breath());
            bank.footfalls = new[]
            {
                Save("footfall_1", Footfall(0, Ground.Asphalt)),
                Save("footfall_2", Footfall(1, Ground.Asphalt)),
                Save("footfall_3", Footfall(2, Ground.Asphalt)),
                Save("footfall_4", Footfall(3, Ground.Asphalt)),
            };
            bank.footfallsSand = new[]
            {
                Save("footfall_sand_1", Footfall(0, Ground.Sand)),
                Save("footfall_sand_2", Footfall(1, Ground.Sand)),
                Save("footfall_sand_3", Footfall(2, Ground.Sand)),
            };
            bank.footfallsRubber = new[]
            {
                Save("footfall_rubber_1", Footfall(0, Ground.Rubber)),
                Save("footfall_rubber_2", Footfall(1, Ground.Rubber)),
                Save("footfall_rubber_3", Footfall(2, Ground.Rubber)),
            };
            // The score's three stems. Same tempo, key and length, so MusicDirector can start them on one
            // DSP sample and only ever move their faders.
            bank.musicCalm = Save("music_calm", MusicCalm());
            bank.musicDrive = Save("music_drive", MusicDrive());
            bank.musicPeak = Save("music_peak", MusicPeak());

            foreach (var kv in real) kv.Key.SetValue(bank, kv.Value);
            if (real.Count > 0) Debug.Log($"[PoDecath] Kept {real.Count} bank slot(s) that hold real recordings: {string.Join(", ", real.Keys.Select(f => f.Name))}.");

            EditorUtility.SetDirty(bank);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PoDecath] Baked {AudioDir}: crowd bed/swell/groan/applause/chant, wind, pistol, beep, bell, "
                    + "clatter, clip, thud, whoosh, sting, breath, 10 footfalls across three surfaces, and the "
                    + "three music stems (calm, drive, peak).");

            // Deliberately not `return bank`. Every clip above went through SaveAndReimport, and the
            // reimport unloads the native side of anything already in memory — including this asset. The
            // managed wrapper that survives still serialises correctly (it keeps a valid instance id) but
            // compares equal to null, so a caller holding it writes good references into a scene while
            // every `bank != null` guard quietly takes the wrong branch. Loading it back resolves it.
            return AssetDatabase.LoadAssetAtPath<AudioBank>(BankPath);
        }

        /// <summary>The bank's clip fields (single or array) whose every clip lives under Assets/Audio/Real, with their values.</summary>
        static Dictionary<System.Reflection.FieldInfo, object> RealSlots(AudioBank bank)
        {
            var kept = new Dictionary<System.Reflection.FieldInfo, object>();
            foreach (var f in typeof(AudioBank).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                object v = f.GetValue(bank);
                if (v is AudioClip c && IsReal(c)) kept[f] = c;
                else if (v is AudioClip[] a && a.Length > 0 && a.All(IsReal)) kept[f] = a;
            }
            return kept;
        }

        static bool IsReal(AudioClip c) => c != null && AssetDatabase.GetAssetPath(c).Replace('\\', '/').Contains("/Audio/Real/");

        // ------------------------------------------------------------------ recipes

        /// <summary>
        /// The bed under everything: two bands of noise (the mass of it, and the hiss on top) breathing
        /// against each other. Generated longer than it needs to be and folded back on itself, so it loops
        /// without a seam.
        /// </summary>
        static float[] CrowdBed()
        {
            const float seconds = 6f, fade = 0.9f;
            int n = (int)(seconds * Rate), f = (int)(fade * Rate);
            float[] raw = Crowd(n + f, seed: 1001, lowHz: 520f, highHz: 1900f, breathHz: 0.23f, depth: 0.45f);

            var outp = new float[n];
            Array.Copy(raw, outp, n);
            for (int i = 0; i < f; i++)
            {
                float w = (float)i / f;                    // 0 at the seam, 1 once the head is clear
                outp[i] = raw[i] * w + raw[n + i] * (1f - w);
            }
            return Normalize(outp, 0.72f);
        }

        /// <summary>A cheer: the same crowd, brighter, opened fast and let fall away.</summary>
        static float[] CrowdSwell()
        {
            int n = (int)(2.4f * Rate);
            float[] s = Crowd(n, seed: 2002, lowHz: 900f, highHz: 3200f, breathHz: 1.4f, depth: 0.3f);
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / n;
                s[i] *= Mathf.Min(1f, t / 0.09f) * Mathf.Exp(-2.6f * t);
            }
            return Normalize(s, 0.9f);
        }

        /// <summary>The other noise a crowd makes. Darker, slower to arrive, and it sags as it goes.</summary>
        static float[] CrowdGroan()
        {
            int n = (int)(2.2f * Rate);
            float[] noise = White(n, 3003);
            var band = new Biquad();
            var outp = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / n;
                if ((i & 255) == 0) band.SetBandpass(Mathf.Lerp(430f, 190f, t), 0.8f, Rate);   // sags as it goes
                outp[i] = band.Process(noise[i]) * Mathf.Min(1f, t / 0.22f) * Mathf.Exp(-2.0f * t);
            }
            return Normalize(outp, 0.8f);
        }

        /// <summary>
        /// Starter's pistol: a crack, a low thump under it, and three attenuated returns off the building.
        /// The returns are what makes it read as a shot fired outdoors rather than a click.
        /// </summary>
        static float[] Pistol()
        {
            int n = (int)(0.85f * Rate);
            float[] noise = White(n, 4004);
            var hp = new Biquad(); hp.SetHighpass(900f, 0.7f, Rate);
            var lp = new Biquad(); lp.SetLowpass(5200f, 0.7f, Rate);
            var outp = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float crack = lp.Process(hp.Process(noise[i])) * Mathf.Exp(-42f * t);
                float thump = Mathf.Sin(2f * Mathf.PI * 58f * t) * Mathf.Exp(-26f * t) * 0.55f;
                outp[i] = crack + thump;
            }
            // Slap-back off the facade and the wings.
            AddDelayed(outp, 0.085f, 0.34f);
            AddDelayed(outp, 0.161f, 0.19f);
            AddDelayed(outp, 0.248f, 0.10f);
            return Normalize(outp, 0.95f);
        }

        /// <summary>One tick of the countdown.</summary>
        static float[] CountBeep()
        {
            int n = (int)(0.16f * Rate);
            var outp = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                outp[i] = Mathf.Sin(2f * Mathf.PI * 860f * t) * Mathf.Min(1f, t / 0.006f) * Mathf.Exp(-26f * t);
            }
            return Normalize(outp, 0.55f);
        }

        /// <summary>
        /// Bell lap. A bell is inharmonic, so these ratios are deliberately not a harmonic series, and the
        /// higher partials get faster decays — that is what makes a strike ring rather than hum.
        /// </summary>
        static float[] LapBell()
        {
            int n = (int)(2.8f * Rate);
            float[] ratios = { 1f, 2.02f, 2.41f, 3.03f, 4.18f, 5.44f };
            float[] gains = { 1f, 0.62f, 0.48f, 0.34f, 0.2f, 0.12f };
            float[] decays = { 2.1f, 2.9f, 3.6f, 4.6f, 6.5f, 8.5f };
            const float f0 = 720f;

            float[] noise = White(n, 5005);
            var hp = new Biquad(); hp.SetHighpass(2600f, 0.7f, Rate);
            var outp = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float s = 0f;
                for (int p = 0; p < ratios.Length; p++)
                    s += Mathf.Sin(2f * Mathf.PI * f0 * ratios[p] * t) * gains[p] * Mathf.Exp(-decays[p] * t);
                s += hp.Process(noise[i]) * Mathf.Exp(-140f * t) * 0.5f;   // the strike itself
                outp[i] = s * Mathf.Min(1f, t / 0.002f);
            }
            return Normalize(outp, 0.85f);
        }

        /// <summary>
        /// A hurdle going over: the bar knocked, then the frame landing on the deck behind it. Six impacts
        /// scattered over the first third of a second, each a wooden knock plus a short ring.
        /// </summary>
        static float[] HurdleClatter()
        {
            int n = (int)(1.0f * Rate);
            var rng = new System.Random(6006);
            float[] noise = White(n, 6007);
            var lp = new Biquad(); lp.SetLowpass(1100f, 0.9f, Rate);
            var outp = new float[n];

            for (int hit = 0; hit < 6; hit++)
            {
                int at = (int)((0.004f + 0.055f * hit + 0.03f * (float)rng.NextDouble()) * Rate);
                float gain = hit == 0 ? 1f : 0.75f - 0.09f * hit;
                float ring = 900f + 1500f * (float)rng.NextDouble();
                float decay = 22f + 26f * (float)rng.NextDouble();
                for (int i = at; i < n; i++)
                {
                    float t = (float)(i - at) / Rate;
                    if (t > 0.35f) break;
                    outp[i] += (Mathf.Sin(2f * Mathf.PI * ring * t) * 0.35f + noise[i] * 0.65f) * gain * Mathf.Exp(-decay * t);
                }
            }
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                outp[i] = lp.Process(outp[i]) + outp[i] * 0.35f;                              // the body, plus its edge
                outp[i] += Mathf.Sin(2f * Mathf.PI * 96f * t) * Mathf.Exp(-14f * t) * 0.3f;   // the deck taking the weight
            }
            return Normalize(outp, 0.85f);
        }

        /// <summary>Landing in the pit: a dull thump and the sand thrown up by it.</summary>
        static float[] SandThud()
        {
            int n = (int)(0.6f * Rate);
            float[] noise = White(n, 7007);
            var lp = new Biquad(); lp.SetLowpass(760f, 0.8f, Rate);
            var hp = new Biquad(); hp.SetHighpass(3400f, 0.7f, Rate);
            var outp = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float body = lp.Process(noise[i]) * Mathf.Exp(-17f * t);
                float grains = hp.Process(noise[i]) * Mathf.Exp(-7f * t) * 0.32f;
                float thump = Mathf.Sin(2f * Mathf.PI * 68f * t) * Mathf.Exp(-19f * t) * 0.6f;
                outp[i] = body + grains + thump;
            }
            return Normalize(outp, 0.85f);
        }

        /// <summary>Under a camera cut. Noise swept up and back down through a resonant band.</summary>
        static float[] Whoosh()
        {
            int n = (int)(0.42f * Rate);
            float[] noise = White(n, 8008);
            var band = new Biquad();
            var outp = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / n;
                float hump = Mathf.Sin(t * Mathf.PI);
                if ((i & 127) == 0) band.SetBandpass(Mathf.Lerp(420f, 2900f, hump), 1.6f, Rate);
                outp[i] = band.Process(noise[i]) * hump;
            }
            return Normalize(outp, 0.6f);
        }

        /// <summary>What an athlete is running on. Each ground gets its own footfall recipe.</summary>
        enum Ground { Asphalt, Sand, Rubber }

        /// <summary>
        /// One footfall. Several variants of the same shape per surface, because a field of sixteen all
        /// playing one clip reads as a single runner however it is pitched — and one set for every surface
        /// the athletes actually cross, because a foot landing in a sand pit and a foot landing on asphalt
        /// are not the same event with the volume changed.
        ///
        /// The three differ in the three things that matter: how much of the sound is a hard slap (a lot on
        /// asphalt, none in sand), how much is loose grain hissing after it (none on asphalt, most of it in
        /// sand), and how quickly the body of the sound dies (fast on a hard deck, faster still on rubber,
        /// which is built to swallow it).
        /// </summary>
        static float[] Footfall(int variant, Ground ground)
        {
            float seconds = ground == Ground.Sand ? 0.34f : 0.22f;
            int n = (int)(seconds * Rate);
            int seed = 9009 + variant + (int)ground * 130;
            float[] noise = White(n, seed);

            float bodyHz, decay, slapHz, slapGain, grainGain, grainDecay;
            switch (ground)
            {
                case Ground.Sand:
                    bodyHz = 380f + 90f * variant; decay = 26f;
                    slapHz = 0f; slapGain = 0f;
                    grainGain = 0.55f; grainDecay = 8f;      // the grain outlasts the impact; that is sand
                    break;
                case Ground.Rubber:
                    bodyHz = 520f + 120f * variant; decay = 46f;
                    slapHz = 86f + 9f * variant; slapGain = 0.28f;
                    grainGain = 0.08f; grainDecay = 22f;
                    break;
                default:
                    bodyHz = 620f + 190f * variant; decay = 34f + 7f * variant;
                    slapHz = 110f + 14f * variant; slapGain = 0.4f;
                    grainGain = 0.22f; grainDecay = 16f;
                    break;
            }

            var lp = new Biquad(); lp.SetLowpass(bodyHz, 0.9f, Rate);
            var hp = new Biquad(); hp.SetHighpass(ground == Ground.Sand ? 3900f : 2900f, 0.7f, Rate);
            var outp = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float body = lp.Process(noise[i]) * Mathf.Exp(-decay * t);
                float grain = hp.Process(noise[i]) * Mathf.Exp(-grainDecay * t) * grainGain;
                float slap = slapGain > 0f
                    ? Mathf.Sin(2f * Mathf.PI * slapHz * t) * Mathf.Exp(-45f * t) * slapGain
                    : 0f;
                outp[i] = body + grain + slap;
            }
            return Normalize(outp, ground == Ground.Sand ? 0.65f : 0.8f);
        }

        /// <summary>
        /// Applause rather than a roar: individual pairs of hands, scattered densely enough to blur into
        /// one texture but not so densely that the claps stop being audible as claps.
        /// </summary>
        static float[] CrowdApplause()
        {
            int n = (int)(3.0f * Rate);
            var rng = new System.Random(11011);
            float[] noise = White(n, 11012);
            var band = new Biquad(); band.SetBandpass(2100f, 0.9f, Rate);
            var outp = new float[n];

            for (int clap = 0; clap < 900; clap++)
            {
                int at = (int)(rng.NextDouble() * (n - 2000));
                float gain = 0.25f + 0.75f * (float)rng.NextDouble();
                float decay = 220f + 260f * (float)rng.NextDouble();
                for (int i = at; i < at + 2000 && i < n; i++)
                {
                    float t = (float)(i - at) / Rate;
                    outp[i] += noise[i] * gain * Mathf.Exp(-decay * t);
                }
            }
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / n;
                outp[i] = band.Process(outp[i]) * 0.7f + outp[i] * 0.3f;
                outp[i] *= Mathf.Min(1f, t / 0.05f) * Mathf.Min(1f, (1f - t) / 0.12f);
            }
            return Normalize(outp, 0.85f);
        }

        /// <summary>
        /// The clap a crowd falls into when a race is close: the same hands, but on a beat. Four beats at
        /// 120 bpm, looped, so it can be faded under the bed without ever landing off the pulse.
        /// </summary>
        static float[] CrowdChant()
        {
            const float bpm = 120f;
            float beat = 60f / bpm;
            int n = (int)(beat * 4f * Rate);
            var rng = new System.Random(12013);
            float[] noise = White(n, 12014);
            var outp = new float[n];

            for (int b = 0; b < 4; b++)
            {
                int centre = (int)(b * beat * Rate);
                // Eighty pairs of hands, none of them quite on the beat, which is what makes it a crowd.
                for (int hands = 0; hands < 80; hands++)
                {
                    int at = centre + (int)((rng.NextDouble() - 0.5) * 0.055f * Rate);
                    if (at < 0) at += n;
                    float gain = 0.3f + 0.7f * (float)rng.NextDouble();
                    float decay = 240f + 220f * (float)rng.NextDouble();
                    for (int i = 0; i < 1800; i++)
                    {
                        int k = (at + i) % n;   // wraps, so the loop has no seam at the bar line
                        outp[k] += noise[k] * gain * Mathf.Exp(-decay * i / Rate);
                    }
                }
            }
            var band = new Biquad(); band.SetBandpass(1900f, 0.8f, Rate);
            for (int i = 0; i < n; i++) outp[i] = band.Process(outp[i]) * 0.75f + outp[i] * 0.25f;
            return Normalize(outp, 0.8f);
        }

        /// <summary>
        /// Wind over the roof: broadband noise through a slowly wandering band, so it gusts. Folded back on
        /// itself like the crowd bed so it loops without a seam.
        /// </summary>
        static float[] WindBed()
        {
            const float seconds = 8f, fade = 1.2f;
            int n = (int)(seconds * Rate), f = (int)(fade * Rate);
            float[] noise = White(n + f, 13013);
            var band = new Biquad();
            var lp = new Biquad(); lp.SetLowpass(1400f, 0.6f, Rate);
            var raw = new float[n + f];
            for (int i = 0; i < n + f; i++)
            {
                float t = (float)i / Rate;
                if ((i & 255) == 0)
                {
                    float gust = 0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * 0.11f * t)
                                      * Mathf.Sin(2f * Mathf.PI * 0.037f * t + 0.9f);
                    band.SetBandpass(Mathf.Lerp(240f, 900f, gust), 0.7f, Rate);
                }
                raw[i] = lp.Process(band.Process(noise[i]));
            }
            var outp = new float[n];
            Array.Copy(raw, outp, n);
            for (int i = 0; i < f; i++)
            {
                float w = (float)i / f;
                outp[i] = raw[i] * w + raw[n + i] * (1f - w);
            }
            return Normalize(outp, 0.55f);
        }

        /// <summary>
        /// A hurdle clipped and left standing: one knock on the bar and a short ring, without the frame
        /// going over behind it. It is the sound the event makes most often and it was missing entirely.
        /// </summary>
        static float[] HurdleClip()
        {
            int n = (int)(0.45f * Rate);
            float[] noise = White(n, 14014);
            var lp = new Biquad(); lp.SetLowpass(1600f, 0.9f, Rate);
            var outp = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float knock = lp.Process(noise[i]) * Mathf.Exp(-38f * t);
                float ring = (Mathf.Sin(2f * Mathf.PI * 1180f * t) * 0.6f + Mathf.Sin(2f * Mathf.PI * 1790f * t) * 0.3f)
                             * Mathf.Exp(-13f * t) * 0.45f;
                outp[i] = knock + ring;
            }
            return Normalize(outp, 0.7f);
        }

        /// <summary>
        /// The stab under a lower third. Three notes of a rising fifth on a hard synthetic tone — short,
        /// because it plays on every camera cut that names somebody and must never become the mix.
        /// </summary>
        static float[] Sting()
        {
            int n = (int)(0.75f * Rate);
            float[] hz = { 392f, 587.33f, 783.99f };   // G4, D5, G5
            var outp = new float[n];
            for (int note = 0; note < hz.Length; note++)
            {
                int at = (int)(note * 0.075f * Rate);
                for (int i = at; i < n; i++)
                {
                    float t = (float)(i - at) / Rate;
                    float env = Mathf.Min(1f, t / 0.004f) * Mathf.Exp(-6.5f * t);
                    outp[i] += (Mathf.Sin(2f * Mathf.PI * hz[note] * t)
                              + Mathf.Sin(2f * Mathf.PI * hz[note] * 2f * t) * 0.28f
                              + Mathf.Sin(2f * Mathf.PI * hz[note] * 3f * t) * 0.12f) * env * 0.55f;
                }
            }
            return Normalize(outp, 0.7f);
        }

        /// <summary>
        /// One breath cycle: air in through a rising band, out through a falling one. Looped per athlete
        /// and pitched with how hard they are working, so a field near the end of a 1500 m is audible as a
        /// field near the end of a 1500 m.
        /// </summary>
        static float[] Breath()
        {
            const float seconds = 1.1f;
            int n = (int)(seconds * Rate);
            float[] noise = White(n, 15015);
            var band = new Biquad();
            var outp = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / n;
                bool inhale = t < 0.42f;
                float phase = inhale ? t / 0.42f : (t - 0.42f) / 0.58f;
                if ((i & 127) == 0)
                    band.SetBandpass(inhale ? Mathf.Lerp(500f, 1500f, phase) : Mathf.Lerp(1300f, 420f, phase), 0.85f, Rate);
                float env = inhale ? Mathf.Sin(phase * Mathf.PI) * 0.85f : Mathf.Sin(phase * Mathf.PI);
                outp[i] = band.Process(noise[i]) * env;
            }
            return Normalize(outp, 0.5f);
        }

        // ------------------------------------------------------------------ music

        //
        // Three stems of one piece: 120 bpm, 4/4, A minor, eight bars (16 s), the progression Am - F - C - G
        // at two bars a chord. MusicDirector plays all three at once from the same DSP sample and moves only
        // their volumes, so the stems are written as layers of one arrangement rather than as three tracks:
        // calm is complete on its own, drive adds the rhythm section under it, peak adds the colour on top.
        //
        // Every note is written with wrap-around, so a pad still releasing at the end of bar eight lands on
        // the start of bar one: the loop has no seam because the file has no ends. Nothing here is filtered
        // across the whole buffer, for the same reason — a filter's memory would not wrap, and would click.
        // They are quiet and plain on purpose; a real set drops onto the same three bank slots.
        //

        const float MusicBpm = 120f;
        const int MusicBars = 8;

        /// <summary>Am, F, C, G as MIDI note triads (voiced round middle C), two bars each.</summary>
        static readonly int[][] MusicChords =
        {
            new[] { 57, 60, 64 },   // A3 C4 E4
            new[] { 53, 57, 60 },   // F3 A3 C4
            new[] { 55, 60, 64 },   // G3 C4 E4 (C, second inversion: keeps the voices moving by step)
            new[] { 55, 59, 62 },   // G3 B3 D4
        };

        /// <summary>The bass under each chord: A2, F2, C3, G2.</summary>
        static readonly int[] MusicRoots = { 45, 41, 48, 43 };

        static float Beat => 60f / MusicBpm;

        /// <summary>Samples in one loop. 8 bars at 120 bpm is exactly 16 s, so all three stems are 705,600 samples.</summary>
        static int MusicLength => Mathf.RoundToInt(MusicBars * 4 * Beat * Rate);

        static float Hz(int midi) => 440f * Mathf.Pow(2f, (midi - 69) / 12f);

        static int[] ChordAtBar(int bar) => MusicChords[(bar / 2) % MusicChords.Length];
        static int RootAtBar(int bar) => MusicRoots[(bar / 2) % MusicRoots.Length];

        /// <summary>
        /// Calm: sustained pads on each chord (two voices a few cents apart, which is what makes a sine pad
        /// breathe instead of hum) and a soft pulse on the root in eighths. Heard alone before the race and
        /// under everything after it.
        /// </summary>
        static float[] MusicCalm()
        {
            int n = MusicLength;
            var outp = new float[n];
            float[] pad = { 1f, 0.35f, 0.18f, 0.08f };
            float[] pulse = { 1f, 0.25f };
            float bar = 4f * Beat;

            for (int b = 0; b < MusicBars; b += 2)
            {
                int[] chord = ChordAtBar(b);
                foreach (int note in chord)
                {
                    // Held for the chord's two bars, released across the next one's attack: a cross-fade
                    // between chords rather than a gap.
                    AddTone(outp, b * bar, Hz(note), pad, 0.075f, attack: 0.8f, hold: 2f * bar, release: 1.6f, detuneCents: -6f);
                    AddTone(outp, b * bar, Hz(note), pad, 0.075f, attack: 0.8f, hold: 2f * bar, release: 1.6f, detuneCents: 6f);
                }
            }
            for (int e = 0; e < MusicBars * 8; e++)
            {
                int b = e / 8;
                float accent = e % 2 == 0 ? 1f : 0.6f;   // on the beat, and softer between
                AddTone(outp, e * Beat * 0.5f, Hz(RootAtBar(b) + 12), pulse, 0.11f * accent,
                        attack: 0.006f, hold: 0.03f, release: 0.2f, decay: 9f);
            }
            return Normalize(outp, 0.5f);
        }

        /// <summary>
        /// Drive: kick on one and three (and a push on the "and" of four every second bar), a snare of
        /// band-limited noise on two and four, closed hats in eighths, and a bass in eighths on the root.
        /// The bass carries its second and third harmonics on purpose — a phone speaker has no 55 Hz, and a
        /// bass line that only exists on headphones is not a bass line on the platform this ships to.
        /// </summary>
        static float[] MusicDrive()
        {
            int n = MusicLength;
            var outp = new float[n];
            float bar = 4f * Beat;
            float[] bass = { 1f, 0.55f, 0.3f, 0.12f };
            int hit = 0;

            for (int b = 0; b < MusicBars; b++)
            {
                float t0 = b * bar;
                Kick(outp, t0, 0.9f);
                Kick(outp, t0 + 2f * Beat, 0.8f);
                if (b % 2 == 1) Kick(outp, t0 + 3.5f * Beat, 0.55f);

                NoiseHit(outp, t0 + 1f * Beat, 0.24f, 18f, 0.42f, 21000 + hit++, 900f, 5200f, toneHz: 185f, toneGain: 0.35f);
                NoiseHit(outp, t0 + 3f * Beat, 0.24f, 18f, 0.42f, 21000 + hit++, 900f, 5200f, toneHz: 185f, toneGain: 0.35f);

                for (int e = 0; e < 8; e++)
                {
                    float at = t0 + e * Beat * 0.5f;
                    NoiseHit(outp, at, 0.06f, 70f, e % 2 == 1 ? 0.2f : 0.11f, 22000 + hit++, 7000f, 16000f);
                    int root = RootAtBar(b);
                    // Octave jump on the last eighth of each bar: the one piece of movement the line has.
                    int note = e == 7 ? root + 12 : root;
                    AddTone(outp, at, Hz(note), bass, 0.3f, attack: 0.004f, hold: 0.17f, release: 0.06f, decay: 3f);
                }
            }
            return Normalize(outp, 0.6f);
        }

        /// <summary>
        /// Peak: brass-like stabs on the chord an octave up (a sawtooth's harmonic series, short and hard
        /// at the front) on a syncopated figure, and a sixteenth-note arpeggio over the chord tones. The
        /// part that says "this is the end of it", and the first thing to leave at the finish.
        /// </summary>
        static float[] MusicPeak()
        {
            int n = MusicLength;
            var outp = new float[n];
            float bar = 4f * Beat;
            float[] brass = { 1f, 0.6f, 0.45f, 0.34f, 0.25f, 0.18f, 0.12f, 0.08f };
            float[] arp = { 1f, 0.3f, 0.1f };
            int[] walk = { 0, 1, 2, 3, 2, 1, 2, 3 };   // up and back over four chord tones

            for (int b = 0; b < MusicBars; b++)
            {
                float t0 = b * bar;
                int[] chord = ChordAtBar(b);

                // One, the "and" of two, and on the second bar of each chord a third stab on four.
                float[] stabs = b % 2 == 0 ? new[] { 0f, 1.5f } : new[] { 0f, 1.5f, 3f };
                foreach (float s in stabs)
                    foreach (int note in chord)
                        AddTone(outp, t0 + s * Beat, Hz(note + 12), brass, 0.06f, attack: 0.022f, hold: 0.15f, release: 0.14f, decay: 2.5f);

                int[] tones = { chord[0] + 12, chord[1] + 12, chord[2] + 12, chord[0] + 24 };
                for (int x = 0; x < 16; x++)
                    AddTone(outp, t0 + x * Beat * 0.25f, Hz(tones[walk[x % walk.Length]]), arp, 0.07f,
                            attack: 0.003f, hold: 0.03f, release: 0.09f, decay: 14f);
            }
            return Normalize(outp, 0.5f);
        }

        /// <summary>
        /// One note of additive synthesis written into a loop: linear attack, hold, linear release to
        /// silence, an optional exponential decay across the whole note, and every sample written modulo
        /// the buffer length so a note that runs past the end finishes at the start.
        /// </summary>
        static void AddTone(float[] buf, float atSeconds, float hz, float[] partials, float gain,
                            float attack, float hold, float release, float decay = 0f, float detuneCents = 0f)
        {
            int n = buf.Length;
            int start = Mathf.RoundToInt(atSeconds * Rate);
            int len = Mathf.RoundToInt((hold + release) * Rate);
            float f = hz * Mathf.Pow(2f, detuneCents / 1200f);
            // Double for the phase: a pad is five seconds long, and a float phase that far in has lost
            // enough precision to be heard as a wobble in the upper partials.
            double w = 2.0 * Math.PI * f / Rate;
            for (int i = 0; i < len; i++)
            {
                float t = (float)i / Rate;
                float env = Mathf.Min(1f, t / Mathf.Max(1e-4f, attack));
                if (t > hold) env *= Mathf.Max(0f, 1f - (t - hold) / Mathf.Max(1e-4f, release));
                if (decay > 0f) env *= Mathf.Exp(-decay * t);
                float s = 0f;
                for (int p = 0; p < partials.Length; p++)
                {
                    float fp = f * (p + 1);
                    if (fp > Rate * 0.45f) break;   // above Nyquist the partial folds back as noise
                    s += (float)Math.Sin(w * (p + 1) * i) * partials[p];
                }
                buf[(start + i) % n] += s * env * gain;
            }
        }

        /// <summary>A kick: a sine swept down from about 115 Hz to 45 Hz, and a click so it reads on a small speaker.</summary>
        static void Kick(float[] buf, float atSeconds, float gain)
        {
            int n = buf.Length;
            int start = Mathf.RoundToInt(atSeconds * Rate);
            int len = Mathf.RoundToInt(0.34f * Rate);
            float phase = 0f;
            for (int i = 0; i < len; i++)
            {
                float t = (float)i / Rate;
                float f = 45f + 70f * Mathf.Exp(-30f * t);
                phase += 2f * Mathf.PI * f / Rate;
                float body = Mathf.Sin(phase) * Mathf.Exp(-9f * t);
                float click = Mathf.Sin(2f * Mathf.PI * 1600f * t) * Mathf.Exp(-400f * t) * 0.25f;
                buf[(start + i) % n] += (body + click) * Mathf.Min(1f, t / 0.0015f) * gain;
            }
        }

        /// <summary>
        /// A burst of band-limited noise with its own filters and its own seed — hats and snare. Each hit
        /// is filtered on its own, so no filter memory crosses the loop point, and written with wrap-around.
        /// </summary>
        static void NoiseHit(float[] buf, float atSeconds, float seconds, float decay, float gain, int seed,
                             float lowHz, float highHz, float toneHz = 0f, float toneGain = 0f)
        {
            int n = buf.Length;
            int start = Mathf.RoundToInt(atSeconds * Rate);
            int len = Mathf.RoundToInt(seconds * Rate);
            float[] noise = White(len, seed);
            var hp = new Biquad(); hp.SetHighpass(lowHz, 0.7f, Rate);
            var lp = new Biquad(); lp.SetLowpass(highHz, 0.7f, Rate);
            for (int i = 0; i < len; i++)
            {
                float t = (float)i / Rate;
                float s = lp.Process(hp.Process(noise[i])) * Mathf.Exp(-decay * t);
                if (toneGain > 0f) s += Mathf.Sin(2f * Mathf.PI * toneHz * t) * Mathf.Exp(-25f * t) * toneGain;
                // A short fade at the tail so a hit cut off by its length does not end on a step.
                float tail = Mathf.Min(1f, (len - i) / (0.004f * Rate));
                buf[(start + i) % n] += s * Mathf.Min(1f, t / 0.001f) * tail * gain;
            }
        }


        // ------------------------------------------------------------------ synthesis helpers

        /// <summary>
        /// The shared crowd texture: a low band for the mass of it and a high band for the hiss, each
        /// breathing on its own slow envelope. The two rates are deliberately not related, so they never
        /// line up and the loop never develops an audible pulse.
        /// </summary>
        static float[] Crowd(int n, int seed, float lowHz, float highHz, float breathHz, float depth)
        {
            float[] noise = White(n, seed);
            var low = new Biquad(); low.SetBandpass(lowHz, 0.55f, Rate);
            var high = new Biquad(); high.SetBandpass(highHz, 0.7f, Rate);
            var outp = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float breathA = 1f - depth * 0.5f * (1f + Mathf.Sin(2f * Mathf.PI * breathHz * t));
                float breathB = 1f - depth * 0.5f * (1f + Mathf.Sin(2f * Mathf.PI * breathHz * 1.63f * t + 1.1f));
                outp[i] = low.Process(noise[i]) * breathA + high.Process(noise[i]) * 0.45f * breathB;
            }
            return outp;
        }

        static float[] White(int n, int seed)
        {
            var rng = new System.Random(seed);
            var s = new float[n];
            for (int i = 0; i < n; i++) s[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
            return s;
        }

        static void AddDelayed(float[] s, float seconds, float gain)
        {
            int d = (int)(seconds * Rate);
            for (int i = s.Length - 1; i >= d; i--) s[i] += s[i - d] * gain;
        }

        static float[] Normalize(float[] s, float peak)
        {
            float max = 0f;
            for (int i = 0; i < s.Length; i++) max = Mathf.Max(max, Mathf.Abs(s[i]));
            if (max < 1e-6f) return s;
            float k = peak / max;
            for (int i = 0; i < s.Length; i++) s[i] *= k;
            return s;
        }

        /// <summary>Direct-form-1 biquad, RBJ cookbook coefficients. Cheap enough to retune mid-clip.</summary>
        struct Biquad
        {
            float _b0, _b1, _b2, _a1, _a2;
            float _x1, _x2, _y1, _y2;

            public void SetLowpass(float fc, float q, float sr)
            {
                Common(fc, q, sr, out float cos, out float alpha, out float a0);
                Set((1f - cos) * 0.5f, 1f - cos, (1f - cos) * 0.5f, cos, alpha, a0);
            }

            public void SetHighpass(float fc, float q, float sr)
            {
                Common(fc, q, sr, out float cos, out float alpha, out float a0);
                Set((1f + cos) * 0.5f, -(1f + cos), (1f + cos) * 0.5f, cos, alpha, a0);
            }

            /// <summary>Constant-peak-gain band pass, so changing Q does not change the level.</summary>
            public void SetBandpass(float fc, float q, float sr)
            {
                Common(fc, q, sr, out float cos, out float alpha, out float a0);
                Set(alpha, 0f, -alpha, cos, alpha, a0);
            }

            static void Common(float fc, float q, float sr, out float cos, out float alpha, out float a0)
            {
                float w0 = 2f * Mathf.PI * Mathf.Clamp(fc, 20f, sr * 0.45f) / sr;
                cos = Mathf.Cos(w0);
                alpha = Mathf.Sin(w0) / (2f * Mathf.Max(0.05f, q));
                a0 = 1f + alpha;
            }

            void Set(float b0, float b1, float b2, float cos, float alpha, float a0)
            {
                _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0;
                _a1 = -2f * cos / a0; _a2 = (1f - alpha) / a0;
            }

            public float Process(float x)
            {
                float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
                _x2 = _x1; _x1 = x;
                _y2 = _y1; _y1 = y;
                return y;
            }
        }

        // ------------------------------------------------------------------ writing

        static AudioClip Save(string name, float[] samples)
        {
            string path = $"{AudioDir}/{name}.wav";
            WriteWav(Path.GetFullPath(path), samples);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(path) as AudioImporter;
            if (importer != null)
            {
                AudioImporterSampleSettings s = importer.defaultSampleSettings;
                s.loadType = AudioClipLoadType.DecompressOnLoad;   // every clip here is short and fired on a cue
                s.preloadAudioData = true;                         // nothing should have to stream in to answer the gun
                importer.defaultSampleSettings = s;
                importer.forceToMono = true;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        }

        /// <summary>16-bit mono PCM. Nothing here needs more, and Unity compresses it for the build anyway.</summary>
        static void WriteWav(string fullPath, float[] samples)
        {
            int n = samples.Length, dataBytes = n * 2;
            using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataBytes);
                w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);            // PCM
                w.Write((short)1);            // mono
                w.Write(Rate);
                w.Write(Rate * 2);            // byte rate
                w.Write((short)2);            // block align
                w.Write((short)16);           // bits per sample
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);
                for (int i = 0; i < n; i++)
                    w.Write((short)Mathf.Clamp(Mathf.RoundToInt(samples[i] * 32767f), -32768, 32767));
            }
        }
    }
}
