using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Everything the game remembers between races and between launches: the athlete cards, their
    /// personal bests, and the season in progress, if there is one. One JSON file in
    /// <c>Application.persistentDataPath</c>, written after every event, and mirrored to Unity Cloud Save
    /// by <see cref="SeasonCloud"/> when the project is linked to a cloud project.
    ///
    /// Plain serialisable classes and <see cref="JsonUtility"/>, because the file has to survive an app
    /// update: a field added later loads as its default from an older file, and nothing here depends on
    /// a type name.
    /// </summary>
    public static class SeasonStore
    {
        /// <summary>One event of a season: what the picker would have sent the field to.</summary>
        [Serializable]
        public class Leg
        {
            public string label;
            public string scene;
            public int laps;
            public bool hurdles;
            public SeasonEvent evt;
        }

        /// <summary>One runner's line in the season standings.</summary>
        [Serializable]
        public class Standing
        {
            public string name;          // the numbered name, "Matt RL 1": a season is one fixed field
            public string athlete;       // the roster entry, for the card
            public float r, g, b;        // its colour, so the table can be drawn without the scene
            public int total;
            public List<int> points = new List<int>();   // one per leg, in order; -1 = not yet run
            public Color Colour => new Color(r, g, b);
        }

        [Serializable]
        public class Season
        {
            public bool active;
            public string started;
            public int next;                               // index of the next leg to run
            public List<Leg> legs = new List<Leg>();
            public List<string> field = new List<string>(); // RaceRoster order, restored on "continue"
            public List<Standing> standings = new List<Standing>();
            public string champion = "";
            public bool Finished => legs.Count > 0 && next >= legs.Count;
        }

        [Serializable]
        public class Best
        {
            public SeasonEvent evt;
            public float value;          // seconds, or metres for the jump
            public int points;
            public string when;
        }

        /// <summary>An athlete card: who this athlete has been across every race on this device.</summary>
        [Serializable]
        public class Card
        {
            public string athlete;
            public float r, g, b;
            public int races, wins, podiums, finishes, falls, recoveries;
            public float topSpeed;       // m/s, the fastest it has been measured going in a race
            public float joules;         // mechanical work its joints have done, all races together
            public int bestPoints;       // its best single-event score
            public List<Best> bests = new List<Best>();
            public Color Colour => new Color(r, g, b);

            public Best BestFor(SeasonEvent evt)
            {
                foreach (Best b in bests) if (b.evt == evt) return b;
                return null;
            }
        }

        [Serializable]
        public class Data
        {
            public int version = 1;
            public Season season = new Season();
            public List<Card> cards = new List<Card>();
            public string saved;
        }

        static Data _data;
        const string FileName = "podecath_season.json";

        public static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

        public static Data Current
        {
            get
            {
                if (_data == null) Load();
                return _data;
            }
        }

        public static void Load()
        {
            _data = null;
            try
            {
                if (File.Exists(FilePath)) _data = JsonUtility.FromJson<Data>(File.ReadAllText(FilePath));
            }
            catch (Exception e)
            {
                // A file that will not parse is kept aside, not deleted: it is somebody's season.
                Debug.LogWarning($"[SeasonStore] could not read {FilePath} ({e.Message}); starting fresh and keeping the old file as .bad");
                try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
            }
            _data ??= new Data();
            _data.season ??= new Season();
            _data.cards ??= new List<Card>();
        }

        public static void Save()
        {
            if (_data == null) return;
            _data.saved = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            try
            {
                string json = JsonUtility.ToJson(_data, true);
                // Written beside and swapped in, so a crash mid-write cannot leave half a file.
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
                SeasonCloud.Push(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SeasonStore] could not write {FilePath}: {e.Message}");
            }
        }

        /// <summary>Replaces what is held with a copy from somewhere else (Cloud Save), if it is newer.</summary>
        public static bool Adopt(string json)
        {
            try
            {
                var incoming = JsonUtility.FromJson<Data>(json);
                if (incoming == null) return false;
                if (_data != null && string.CompareOrdinal(incoming.saved ?? "", _data.saved ?? "") <= 0) return false;
                _data = incoming;
                _data.season ??= new Season();
                _data.cards ??= new List<Card>();
                return true;
            }
            catch { return false; }
        }

        public static Card CardFor(string athlete, Color colour)
        {
            foreach (Card c in Current.cards)
                if (c.athlete == athlete) { c.r = colour.r; c.g = colour.g; c.b = colour.b; return c; }
            var card = new Card { athlete = athlete, r = colour.r, g = colour.g, b = colour.b };
            Current.cards.Add(card);
            return card;
        }

        // ---------------------------------------------------------------- the season itself

        public static bool SeasonActive => Current.season.active && !Current.season.Finished;

        /// <summary>The leg the next race should be, or null if no season is running.</summary>
        public static Leg NextLeg => SeasonActive ? Current.season.legs[Current.season.next] : null;

        public static void StartSeason(List<Leg> legs, List<string> field)
        {
            Current.season = new Season
            {
                active = true,
                started = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                next = 0,
                legs = new List<Leg>(legs),
                field = new List<string>(field),
            };
            Save();
        }

        public static void EndSeason()
        {
            Current.season.active = false;
            Save();
        }

        public static Standing StandingFor(string name, string athlete, Color colour)
        {
            Season s = Current.season;
            foreach (Standing st in s.standings) if (st.name == name) return st;
            var created = new Standing { name = name, athlete = athlete, r = colour.r, g = colour.g, b = colour.b };
            for (int i = 0; i < s.legs.Count; i++) created.points.Add(-1);
            s.standings.Add(created);
            return created;
        }

        /// <summary>Standings, best total first.</summary>
        public static List<Standing> Table()
        {
            var list = new List<Standing>(Current.season.standings);
            list.Sort((x, y) => y.total.CompareTo(x.total));
            return list;
        }
    }
}
