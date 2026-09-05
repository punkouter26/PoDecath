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
