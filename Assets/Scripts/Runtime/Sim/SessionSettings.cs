using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>Selections made on the main menu, carried across the scene load.</summary>
    public static class SessionSettings
    {
        public static int PolicyIndex = 0;
        public static ArenaType Arena = ArenaType.FlatTrack;
        public static int TargetFrameRate = 60;
        public static bool PerturbationEnabled = false;
        /// <summary>True once the main menu has been shown; the Arena scene falls back to its editor defaults otherwise.</summary>
        public static bool VisitedMenu = false;

        /// <summary>
        /// Laps the picked event runs. The rooftop loop is 100.1 m, so 1 lap is the 100 m, 4 is the 400 m
        /// and 15 is the 1500 m — one scene, one <see cref="LapEvent"/>, a different number here.
        /// 0 leaves the scene's own setting alone, which is what the development scenes want.
        /// </summary>
        public static int Laps = 0;

        /// <summary>Whether the picked event puts hurdles on the straights.</summary>
        public static bool Hurdles = false;

        /// <summary>Chosen on the event picker and read by the race scene as it loads.</summary>
        public static void SetEvent(int laps, bool hurdles)
        {
            Laps = Mathf.Max(0, laps);
            Hurdles = hurdles;
        }

        const string KeyPolicy = "podecath.policy";
        const string KeyArena = "podecath.arena";
        const string KeyFps = "podecath.fps";

        public static void Load()
        {
            PolicyIndex = PlayerPrefs.GetInt(KeyPolicy, 0);
            Arena = (ArenaType)PlayerPrefs.GetInt(KeyArena, 0);
            TargetFrameRate = PlayerPrefs.GetInt(KeyFps, 60);
        }

        public static void Save()
        {
            PlayerPrefs.SetInt(KeyPolicy, PolicyIndex);
            PlayerPrefs.SetInt(KeyArena, (int)Arena);
            PlayerPrefs.SetInt(KeyFps, TargetFrameRate);
            PlayerPrefs.Save();
        }

        public static void ApplyQuality()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = TargetFrameRate;
        }
    }
}
