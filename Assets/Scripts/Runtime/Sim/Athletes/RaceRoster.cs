using System.Collections.Generic;

namespace PoDecath.Sim
{
    /// <summary>
    /// The field chosen on the race setup menu, carried across the scene load (same job as
    /// <see cref="SessionSettings"/>, kept separate because it is a list rather than a preference).
    /// One entry per grid slot in starting order; <see cref="AthleteSpawner"/> resolves the names
    /// against its roster and numbers the runners 1..N so every athlete has a unique name.
    /// </summary>
    public static class RaceRoster
    {
        public const int MaxRunners = 16;

        /// <summary>AthleteDefinition display names, one per grid slot. Empty = use the scene's own roster.</summary>
        public static readonly List<string> Selection = new List<string>();

        public static bool Chosen => Selection.Count > 0;

        public static void Set(IEnumerable<string> names)
        {
            Selection.Clear();
            foreach (string n in names)
            {
                if (Selection.Count >= MaxRunners) break;
                if (!string.IsNullOrEmpty(n)) Selection.Add(n);
            }
        }

        public static void Clear() => Selection.Clear();
    }
}
