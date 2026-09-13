using UnityEngine;
using PoDecath.Audio;
using PoDecath.Cam;
using PoDecath.Sim;

namespace PoDecath.Fx
{
    /// <summary>
    /// What the featured athlete wears on top of its own textures: a rim of light on the one the gallery
    /// has cut to, a sheen of sweat that rises with the fatigue <see cref="EffortMeter"/> already
    /// computes, and a spray of it off a hard footfall once it is tired.
    ///
    /// None of it touches the model's materials. The rim is a second skinned renderer sharing the mesh
    /// and bones, enabled only while this athlete is on air, so a field of sixteen pays for one extra
    /// skin at most. The sheen is a property block on the existing renderers that lowers roughness
    /// (<c>roughnessFactor</c> on the glTF shader, <c>_Smoothness</c> on URP Lit), which is what wet skin
    /// is to a renderer; the base value is read off each material so an athlete that starts glossy does
    /// not get duller as it tires.
    /// </summary>
    [DefaultExecutionOrder(82)]
    public class AthleteSheen : MonoBehaviour
    {
        [Header("Wiring")]
        public AthleteRig rig;
        [Tooltip("Optional; without one there is no sweat, only the rim.")]
        public EffortMeter effort;
        [Tooltip("The additive rim material from the VfxBank.")]
        public Material rimMaterial;
        [Tooltip("Steps, for the spray. Found on this object if not set.")]
        public FootstepAudio steps;

        [Header("Rim")]
        public Color rimColor = new Color(0.55f, 0.9f, 1f, 1f);
        [Tooltip("Rim intensity while featured.")]
        public float rimIntensity = 1.1f;
        [Tooltip("Seconds for the rim to arrive and leave; a cut is instant, the light on it should not be.")]
        public float rimSeconds = 0.3f;

        [Header("Sweat")]
        [Tooltip("Fatigue at which the skin starts to shine.")]
        [Range(0f, 1f)] public float sweatStart = 0.2f;
        [Tooltip("Roughness at full fatigue, as a fraction of the material's own. 0.25 is wet.")]
        [Range(0.05f, 1f)] public float wetRoughness = 0.28f;
        [Tooltip("Footfall weight (0..1) that throws a spray once the athlete is tired.")]
        public float sprayMinWeight = 0.7f;
        [Range(0f, 1f)] public float sprayFatigue = 0.4f;
        public float sprayInterval = 0.45f;
        public float sprayCameraDistance = 26f;

        SkinnedMeshRenderer[] _skins;
        SkinnedMeshRenderer[] _rims;
        float[][] _baseRoughness;     // per skin, per material; NaN where the material has neither property
        bool[][] _gltf;
        MaterialPropertyBlock _block;
        BroadcastDirector _director;
        RaceEvent.Athlete _self;
        RaceEvent _race;
        float _rim;
        float _sheen = -1f;
        float _lastSpray = -10f;
        Camera _view;

        static readonly int RimIntensityId = Shader.PropertyToID("_RimIntensity");
        static readonly int RimColorId = Shader.PropertyToID("_RimColor");
        static readonly int RoughnessId = Shader.PropertyToID("roughnessFactor");
        static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");

        void Start()
        {
            if (rig == null) rig = GetComponentInChildren<AthleteRig>();
            if (effort == null) effort = GetComponent<EffortMeter>();
            if (steps == null) steps = GetComponent<FootstepAudio>();
            _director = FindFirstObjectByType<BroadcastDirector>();
            _race = FindFirstObjectByType<RaceEvent>();
            _block = new MaterialPropertyBlock();

            _skins = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (_skins.Length == 0) { enabled = false; return; }

            _baseRoughness = new float[_skins.Length][];
            _gltf = new bool[_skins.Length][];
            _rims = new SkinnedMeshRenderer[_skins.Length];
            for (int i = 0; i < _skins.Length; i++)
            {
                SkinnedMeshRenderer smr = _skins[i];
                Material[] mats = smr.sharedMaterials;
                _baseRoughness[i] = new float[mats.Length];
                _gltf[i] = new bool[mats.Length];
                for (int m = 0; m < mats.Length; m++)
                {
                    Material mat = mats[m];
                    if (mat != null && mat.HasProperty(RoughnessId)) { _gltf[i][m] = true; _baseRoughness[i][m] = mat.GetFloat(RoughnessId); }
                    else if (mat != null && mat.HasProperty(SmoothnessId)) { _gltf[i][m] = false; _baseRoughness[i][m] = 1f - mat.GetFloat(SmoothnessId); }
                    else _baseRoughness[i][m] = float.NaN;
                }
                if (rimMaterial != null) _rims[i] = MakeRim(smr);
            }

            if (steps != null) steps.Stepped += OnStep;
        }

        void OnDestroy()
        {
            if (steps != null) steps.Stepped -= OnStep;
        }

        /// <summary>
        /// A second skinned renderer over the first: same mesh, same bones, every slot on the rim material,
        /// disabled until this athlete is on camera. Sharing the bones means it costs no animation; it
        /// costs one more skinning pass while enabled and nothing while not.
        /// </summary>
        SkinnedMeshRenderer MakeRim(SkinnedMeshRenderer source)
        {
            if (source.sharedMesh == null) return null;
            var go = new GameObject("Rim");
            go.layer = source.gameObject.layer;
            go.transform.SetParent(source.transform, false);
            var rim = go.AddComponent<SkinnedMeshRenderer>();
            rim.sharedMesh = source.sharedMesh;
            rim.bones = source.bones;
            rim.rootBone = source.rootBone;
            rim.localBounds = source.localBounds;
            rim.updateWhenOffscreen = true;
            rim.quality = source.quality;
            var mats = new Material[source.sharedMesh.subMeshCount];
            for (int i = 0; i < mats.Length; i++) mats[i] = rimMaterial;
            rim.sharedMaterials = mats;
            rim.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rim.receiveShadows = false;
            rim.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            rim.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            rim.enabled = false;
            return rim;
        }

        RaceEvent.Athlete Self()
        {
            if (_self != null || _race == null) return _self;
            foreach (RaceEvent.Athlete a in _race.Athletes)
                if (a.rig == rig) { _self = a; break; }
            return _self;
        }

        void LateUpdate()
        {
            Rim();
            Sweat();
        }

        void Rim()
        {
            bool featured = _director != null && _director.OnIndividual && _director.Featured != null
                            && _director.Featured == Self();
            float target = featured ? rimIntensity : 0f;
            float k = rimSeconds > 0f ? 1f - Mathf.Exp(-Time.deltaTime / rimSeconds) : 1f;
            float next = Mathf.Lerp(_rim, target, k);
            if (Mathf.Abs(next - _rim) < 0.002f && (next < 0.01f) == (_rim < 0.01f)) { _rim = next; return; }
            _rim = next;
            bool on = _rim > 0.01f;
            for (int i = 0; i < _rims.Length; i++)
            {
                SkinnedMeshRenderer r = _rims[i];
                if (r == null) continue;
                r.enabled = on;
                if (!on) continue;
                r.GetPropertyBlock(_block);
                _block.SetFloat(RimIntensityId, _rim);
                _block.SetColor(RimColorId, rimColor);
                r.SetPropertyBlock(_block);
            }
        }

        void Sweat()
        {
            if (effort == null) return;
            float sheen = Mathf.InverseLerp(sweatStart, 1f, effort.Fatigue);
            if (Mathf.Abs(sheen - _sheen) < 0.01f) return;
            _sheen = sheen;
            for (int i = 0; i < _skins.Length; i++)
            {
                SkinnedMeshRenderer smr = _skins[i];
                if (smr == null) continue;
                for (int m = 0; m < _baseRoughness[i].Length; m++)
                {
                    float baseR = _baseRoughness[i][m];
                    if (float.IsNaN(baseR)) continue;
                    float rough = Mathf.Lerp(baseR, baseR * wetRoughness, sheen);
                    smr.GetPropertyBlock(_block, m);
                    if (_gltf[i][m]) _block.SetFloat(RoughnessId, rough);
                    else _block.SetFloat(SmoothnessId, 1f - rough);
                    smr.SetPropertyBlock(_block, m);
                }
            }
        }

        void OnStep(Vector3 at, float weight)
        {
            if (effort == null || effort.Fatigue < sprayFatigue || weight < sprayMinWeight) return;
            if (Time.time - _lastSpray < sprayInterval) return;
            if (_view == null) _view = Camera.main;
            Vector3 head = (rig != null ? rig.BasePosition : transform.position) + Vector3.up * 0.55f;
            if (_view != null && (head - _view.transform.position).sqrMagnitude > sprayCameraDistance * sprayCameraDistance) return;
            _lastSpray = Time.time;
            Vector3 back = rig != null ? -rig.BaseLinearVelocityWorld.normalized : -transform.forward;
            VfxLibrary.Play(VfxLibrary.Effect.Sweat, head, Vector3.up + back * 0.5f, Mathf.Clamp01(effort.Fatigue));
        }
    }
}
