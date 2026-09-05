using System.Collections.Generic;
using UnityEngine;
using PoDecath.Audio;
using PoDecath.Cam;
using PoDecath.Env;
using PoDecath.Fx;

namespace PoDecath.Sim
{
    /// <summary>
    /// Spawns the roster (house rules: RED heuristic bot, GREEN reference RL bot, custom bots with their
    /// own textures) and registers everyone with the DashEvent. RL athletes are built from the MJCF used
    /// for training, skinned with the glTF character, and driven by PolicyRunner in TargetVector mode.
    /// </summary>
    [DefaultExecutionOrder(-60)]
    public class AthleteSpawner : MonoBehaviour
    {
        public TextAsset defaultMjcf;
        public TextAsset defaultPolicyJson;
        public GameObject defaultSkin;
        public List<AthleteDefinition> roster = new List<AthleteDefinition>();
        public DashEvent dash;
        public CameraRig cameraRig;
        public PhysicsMaterial footMaterial;
        public Material debugVisualMaterial;
        public string creatureLayerName = "Creature";
        public bool debugVisuals = false;
        public int controlDecimation = 4;
        [Tooltip("Show the RED heuristic pacer bot. Off = only the RL athletes run.")]
        public bool includeHeuristic = true;
        [Tooltip("Suffix every athlete with a unique 1-based number, so a field of eight copies of two policies still has eight distinct names.")]
        public bool numberRunners = false;
        [Range(0f, 1f)]
        [Tooltip("How strongly a house colour is pushed onto the skin. 0 (default) = the imported model's own "
               + "textures, untouched, per the owner decision of 2026-09-05. 1 = a flat house colour. The "
               + "colour multiplies the base map rather than replacing it, so values in between keep the "
               + "texture readable while still telling policies apart.")]
        public float skinTintStrength = 0f;
        [Tooltip("Baked by PoDecath/Bake Audio Clips. Gives every athlete its own footsteps; leave it empty "
               + "for a field that runs in silence.")]
        public AudioBank audioBank;
        [Tooltip("Materials for the trails, blob shadows and dust. Without it athletes get no presentation.")]
        public VfxBank vfxBank;
        [Tooltip("Ribbon behind each athlete in its own colour; the only thing that tells a field of "
               + "identically skinned Matts apart, since the house rule forbids tinting them.")]
        public bool trails = true;
        [Tooltip("Soft disc under each athlete. Essential on mobile, a contact shadow on desktop.")]
        public bool blobShadows = true;
        [Tooltip("A puff off every heavy footfall, driven by the same contact detection as the sound.")]
        public bool footDust = true;

        [System.Serializable]
        class PolicyJson { public float action_scale = 0.5f; public int control_decimation = 4; public int physics_hz = 200; }

        void Awake()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = SessionSettings.TargetFrameRate > 0 ? SessionSettings.TargetFrameRate : 60;
            int layer = LayerMask.NameToLayer(creatureLayerName);
            if (layer < 0) layer = 0;
            // MJCF rigs have self-collision disabled (contype/conaffinity). PhysX only skips parent-child links, and the
            // intermediate hinge links break adjacency, so pelvis/thigh capsules would collide and force the hips apart.
            if (layer != 0) Physics.IgnoreLayerCollision(layer, layer, true);
            var pj = new PolicyJson();
            if (defaultPolicyJson != null) { try { pj = JsonUtility.FromJson<PolicyJson>(defaultPolicyJson.text) ?? pj; } catch { } }

            int number = 0;
            foreach (AthleteDefinition def in BuildSpawnList())
            {
                if (def == null) continue;
                DashEvent.Athlete a = def.kind == AthleteKind.Heuristic ? SpawnHeuristic(def) : SpawnRL(def, layer, pj);
                if (a == null) continue;
                a.number = ++number;
                if (numberRunners)
                {
                    a.name = $"{def.displayName} {a.number}";
                    if (a.go != null) a.go.name = a.name;
                }
                AddFootsteps(a);
                AddPresentation(a, number - 1);
                if (dash != null) dash.Register(a);
            }
            if (cameraRig != null && dash != null && dash.Reference != null)
                cameraRig.SetTarget(dash.Reference.IsRL ? dash.Reference.rig.root.transform : dash.Reference.go.transform);
        }

        void Start()
        {
            if (dash != null) dash.StartRace();
        }

        /// <summary>
        /// The field to put on the grid: the menu's selection when there is one (one entry per grid slot,
        /// so the same definition can appear several times), otherwise the scene's own roster.
        /// </summary>
        List<AthleteDefinition> BuildSpawnList()
        {
            var list = new List<AthleteDefinition>();
            if (RaceRoster.Chosen)
            {
                foreach (string wanted in RaceRoster.Selection)
                {
                    AthleteDefinition def = roster.Find(d => d != null && d.displayName == wanted);
                    if (def != null) list.Add(def);
                    else Debug.LogWarning($"[AthleteSpawner] Menu asked for '{wanted}', which is not in this scene's roster.", this);
                }
                if (list.Count > 0) return list;
            }
            foreach (AthleteDefinition def in roster)
            {
                if (def == null) continue;
                if (def.kind == AthleteKind.Heuristic && !includeHeuristic) continue;
                list.Add(def);
            }
            return list;
        }

        /// <summary>
        /// Gives one athlete its own feet, on the body that actually moves — the articulation root for a
        /// physics athlete, the runner object for the kinematic bot — so a step is heard from where the
        /// runner is on the deck rather than from the middle of the stadium.
        ///
        /// The dust comes off the same detection: <see cref="FootstepAudio"/> raises an event per step and
        /// <see cref="FootstepDust"/> listens, so the puff and the sound are always on the same frame.
        /// </summary>
        void AddFootsteps(DashEvent.Athlete a)
        {
            GameObject host = a.IsRL && a.rig.root != null ? a.rig.root.gameObject : a.go;
            if (host == null) return;
            bool haveSound = audioBank != null && audioBank.HasFootfalls;
            if (!haveSound && !footDust) return;

            var steps = host.AddComponent<FootstepAudio>();
            steps.bank = haveSound ? audioBank : null;
            steps.rig = a.rig;
            steps.heuristic = a.heuristic;
            if (footDust) host.AddComponent<FootstepDust>().steps = steps;
        }

        /// <summary>
        /// The presentation an athlete carries with it: a trail ribbon in its own colour, a soft disc on
        /// the deck under it, and dust off its feet.
        ///
        /// The trail is the answer to a constraint rather than a decoration. Athletes keep the textures
        /// they were imported with (see CLAUDE.md), so a field of eight identical Matts cannot be told
        /// apart by looking at them; the ribbon puts each one's colour on screen without tinting a single
        /// pixel of skin, and shows the line they took through the bend into the bargain.
        ///
        /// Only the first few athletes get a ribbon on the mobile tier — <see cref="RenderTier.TrailedAthletes"/> —
        /// because sixteen trail renderers is sixteen dynamic meshes rebuilt every frame.
        /// </summary>
        void AddPresentation(DashEvent.Athlete a, int index)
        {
            if (vfxBank == null) return;
            GameObject host = a.IsRL && a.rig.root != null ? a.rig.root.gameObject : a.go;
            if (host == null) return;

            if (trails && index < RenderTier.TrailedAthletes && vfxBank.trail != null)
            {
                var trail = host.AddComponent<AthleteTrail>();
                trail.rig = a.rig;
                trail.heuristic = a.heuristic;
                trail.material = vfxBank.trail;
                trail.color = a.color;
            }
            if (blobShadows && vfxBank.blobShadow != null)
            {
                var blob = host.AddComponent<BlobShadow>();
                blob.rig = a.rig;
                blob.heuristic = a.heuristic;
                blob.material = vfxBank.blobShadow;
            }
        }

        DashEvent.Athlete SpawnRL(AthleteDefinition def, int layer, PolicyJson pj)
        {
            TextAsset xml = def.mjcfOverride != null ? def.mjcfOverride : defaultMjcf;
            if (xml == null) { Debug.LogError("[AthleteSpawner] No MJCF assigned.", this); return null; }
            GameObject skinPrefab = def.skinOverride != null ? def.skinOverride : defaultSkin;

            var opt = new MjcfImporter.Options
            {
                layer = layer,
                debugVisuals = debugVisuals || skinPrefab == null,
                visualMaterial = debugVisualMaterial,
                physicsMaterial = footMaterial,
            };
            MjcfImporter.Result res = MjcfImporter.Build(xml.text, opt);
            res.root.name = def.displayName;
            res.root.transform.SetParent(transform, false);

            var cfg = ScriptableObject.CreateInstance<PolicyConfig>();
            cfg.name = def.displayName + "_config";
            cfg.joints = res.joints;
            cfg.physicsHz = Mathf.RoundToInt(1f / Mathf.Max(1e-4f, res.physicsTimestep));
            cfg.controlDecimation = pj.control_decimation > 0 ? pj.control_decimation : controlDecimation;
            cfg.actionScale = def.actionScale > 0f ? def.actionScale : pj.action_scale;
            cfg.actionClip = 5f;
            cfg.observationClip = 100f;
            cfg.spawnHeight = res.keyframeRootHeight > 0f ? res.keyframeRootHeight + 0.02f : 0.95f;
            cfg.minBaseHeight = cfg.spawnHeight * 0.6f;
            cfg.minUprightDot = 0.4f;
            cfg.stiffness = 100f; cfg.damping = 5f; cfg.forceLimit = 100f;   // fallbacks; per-joint values come from the MJCF actuators
            cfg.clampTargetsToJointLimits = true;

            var cmd = res.root.AddComponent<VelocityCommandSource>();
            cmd.mode = CommandMode.TargetVector;
            var runner = res.root.AddComponent<PolicyRunner>();
            runner.rig = res.rig;
            runner.commandSource = cmd;
            runner.creatureLayerName = creatureLayerName;

            TrackFollower follower = null;
            if (dash is LapEvent lap && lap.path != null)
            {
                follower = res.root.AddComponent<TrackFollower>();
                follower.path = lap.path;
                follower.command = cmd;
                follower.rig = res.rig;
                follower.lookahead = lap.lookahead;
            }

            GameObject skinInstance = null;
            if (skinPrefab != null)
            {
                skinInstance = Instantiate(skinPrefab);
                skinInstance.name = "Skin";
                SetLayerRecursive(skinInstance, layer);
                var binder = res.root.AddComponent<SkinBinder>();
                if (def.boneMap != null && def.boneMap.Count > 0) binder.map = def.boneMap;   // per-glb skeleton names
                binder.skinRootEuler = def.skinRootEuler;
                binder.Bind(res.rig, skinInstance);   // must happen before ResetPose moves the rig out of the rest pose
                Tint(skinInstance, def, skinTintStrength);
            }
            if (opt.debugVisuals) TintPrimitives(res.root, def.Tint);

            runner.Initialize(cfg, def.model);
            if (def.model == null) Debug.LogWarning($"[AthleteSpawner] '{def.displayName}' has no ONNX model; it will hold its default pose.", this);

            return new DashEvent.Athlete
            {
                name = def.displayName, kind = def.kind, color = def.Tint, go = res.root, rig = res.rig,
                runner = runner, command = cmd, follower = follower, spawnHeight = cfg.spawnHeight,
            };
        }

        DashEvent.Athlete SpawnHeuristic(AthleteDefinition def)
        {
            var go = new GameObject(def.displayName);
            go.transform.SetParent(transform, false);
            var hr = go.AddComponent<HeuristicRunner>();
            hr.topSpeed = def.topSpeed;
            hr.accelSeconds = def.accelSeconds;
            GameObject skinPrefab = def.skinOverride != null ? def.skinOverride : defaultSkin;
            if (skinPrefab != null)
            {
                var skin = Instantiate(skinPrefab, go.transform);
                skin.name = "Skin";
                skin.transform.localPosition = Vector3.zero;
                skin.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);   // skin faces +Z; runner forward is +X
                Tint(skin, def, skinTintStrength);
            }
            else
            {
                var cap = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                cap.transform.SetParent(go.transform, false);
                cap.transform.localPosition = Vector3.up * 0.9f;
                cap.transform.localScale = new Vector3(0.4f, 0.9f, 0.4f);
                Destroy(cap.GetComponent<Collider>());
                TintPrimitives(go, def.Tint);
            }
            return new DashEvent.Athlete { name = def.displayName, kind = def.kind, color = def.Tint, go = go, heuristic = hr };
        }

        static void Tint(GameObject skin, AthleteDefinition def, float strength)
        {
            bool ownTexture = def.kind == AthleteKind.CustomRL && def.customTexture != null;
            strength = Mathf.Clamp01(strength);
            if (strength <= 0f && !ownTexture) return;   // leave the glb materials exactly as authored

            var block = new MaterialPropertyBlock();
            Color c = Color.Lerp(Color.white, def.Tint, strength);   // white is a no-op against the base map
            foreach (var r in skin.GetComponentsInChildren<Renderer>(true))
            {
                r.GetPropertyBlock(block);
                block.SetColor("_BaseColor", c);
                block.SetColor("baseColorFactor", c);
                block.SetColor("_Color", c);
                if (ownTexture)
                {
                    block.SetTexture("_BaseMap", def.customTexture);
                    block.SetTexture("baseColorTexture", def.customTexture);
                    block.SetTexture("_MainTex", def.customTexture);
                }
                r.SetPropertyBlock(block);
            }
        }

        static void TintPrimitives(GameObject root, Color c)
        {
            Shader s = Shader.Find("Universal Render Pipeline/Lit");
            var m = new Material(s != null ? s : Shader.Find("Standard")) { color = c };
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true)) r.sharedMaterial = m;
        }

        static void SetLayerRecursive(GameObject go, int layer)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }
    }
}
