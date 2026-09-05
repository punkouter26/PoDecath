using UnityEngine;
using PoDecath.Cam;

namespace PoDecath.Sim
{
    /// <summary>
    /// Entry point of the Arena scene. Reads SessionSettings, builds the arena, loads the
    /// selected checkpoint from the PolicyLibrary, and starts the evaluation loop.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class ArenaBootstrap : MonoBehaviour
    {
        public ArenaGenerator arena;
        public PolicyRunner runner;
        public EpisodeManager episodes;
        public CreatureRig creature;
        public CameraRig cameraRig;

        [Tooltip("Optional override; when null the PolicyLibrary entry selected on the menu is used.")]
        public PolicyConfig configOverride;
        [Tooltip("Optional override; when null the PolicyLibrary entry selected on the menu is used.")]
        public Unity.InferenceEngine.ModelAsset modelOverride;
        [Tooltip("Used when the scene is played directly without visiting the menu.")]
        public ArenaType editorArena = ArenaType.FlatTrack;

        public PolicyEntry ActiveEntry { get; private set; }

        void Awake()
        {
            SessionSettings.ApplyQuality();

            PolicyLibrary lib = PolicyLibrary.Load();
            PolicyEntry entry = lib != null ? lib.Get(SessionSettings.PolicyIndex) : null;
            ActiveEntry = entry;

            PolicyConfig cfg = configOverride != null ? configOverride
                : entry != null && entry.config != null ? entry.config
                : lib != null ? lib.defaultConfig : null;
            if (cfg == null)
            {
                cfg = ScriptableObject.CreateInstance<PolicyConfig>();
                cfg.ApplyGo2Defaults();
                Debug.LogWarning("[ArenaBootstrap] No PolicyConfig found; using in-memory Go2 defaults.");
            }

            var model = modelOverride != null ? modelOverride : entry?.model;

            ArenaType arenaType = SessionSettings.VisitedMenu ? SessionSettings.Arena : editorArena;
            if (arena != null) arena.Generate(arenaType);

            if (creature == null) creature = FindAnyObjectByType<CreatureRig>();
            if (runner != null)
            {
                runner.rig = creature;
                runner.Initialize(cfg, model);
            }
            if (cameraRig != null && creature != null) cameraRig.SetTarget(creature.root != null ? creature.root.transform : creature.transform);
        }

        void Start()
        {
            if (episodes != null)
            {
                episodes.runner = runner;
                episodes.rig = creature;
                episodes.arena = arena;
                episodes.Begin();
            }
        }
    }
}
