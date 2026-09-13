using UnityEngine;
using PoDecath.Sim;

namespace PoDecath.Env
{
    /// <summary>
    /// Four masts round the loop for the floodlit preset.
    ///
    /// The night preset used to live on ambient alone, which made the deck a dark grey slab under a dark
    /// blue sky. Stadium lighting is a handful of spots on tall masts aimed down at the track, each with
    /// a soft cookie so the pools of light have edges, and that is what this builds: one mast mid-way down
    /// each straight and one at each bend apex, outside the rail, lamps at <see cref="mastHeight"/>.
    ///
    /// The masts are furniture and stay up by day; only the lights come and go with the preset. Shadows
    /// from the spots are a PC-tier cost (four extra shadow maps) and the mobile asset does not support
    /// additional-light shadows anyway.
    /// </summary>
    public class Floodlights : MonoBehaviour
    {
        [Header("Wiring")]
        public TrackPath path;
        [Tooltip("Soft radial cookie; Fx_soft from the effects bake works. Without one the pool is a hard cone.")]
        public Texture cookie;
        public Material mastMaterial;

        [Header("Masts")]
        public float mastHeight = 14f;
        [Tooltip("Metres past the outer rail.")]
        public float outward = 7.5f;
        public float mastRadius = 0.22f;

        [Header("Lamps")]
        public Color color = new Color(1f, 0.93f, 0.8f);
        public float intensity = 48f;
        public float range = 80f;
        public float spotAngle = 74f;

        Light[] _lights;
        bool _on;

        /// <summary>Whether the lamps are lit. Set by SceneLook with the preset.</summary>
        public bool On => _on;

        void Awake()
        {
            if (path == null) { enabled = false; return; }
            Build();
            Set(_on);
        }

        /// <summary>Lights on or off. The masts stay.</summary>
        public void Set(bool on)
        {
            _on = on;
            if (_lights == null) return;
            bool shadows = RenderTier.AthleteShadows;
            foreach (Light l in _lights)
            {
                if (l == null) continue;
                l.enabled = on;
                l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
            }
        }

        void Build()
        {
            float L = path.StraightLength, A = path.ArcLength;
            float[] at = { L * 0.5f, L + A * 0.5f, 1.5f * L + A, 2f * L + 1.5f * A };
            _lights = new Light[at.Length];
            float lateral = path.deckWidth * 0.5f + outward;

            for (int i = 0; i < at.Length; i++)
            {
                Vector3 top = path.Position(at[i], lateral) + Vector3.up * mastHeight;
                Vector3 foot = top;
                foot.y = path.deckTopY - 30f;
                if (Physics.Raycast(top, Vector3.down, out RaycastHit hit, 60f, ~0, QueryTriggerInteraction.Ignore))
                    foot = hit.point;

                var mast = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                mast.name = $"Mast_{i}";
                Object.Destroy(mast.GetComponent<Collider>());
                mast.transform.SetParent(transform, false);
                float h = top.y - foot.y;
                mast.transform.position = (top + foot) * 0.5f;
                mast.transform.localScale = new Vector3(mastRadius * 2f, h * 0.5f, mastRadius * 2f);
                if (mastMaterial != null) mast.GetComponent<MeshRenderer>().sharedMaterial = mastMaterial;

                var head = GameObject.CreatePrimitive(PrimitiveType.Cube);
                head.name = $"Lamp_{i}";
                Object.Destroy(head.GetComponent<Collider>());
                head.transform.SetParent(transform, false);
                Vector3 aim = path.Position(at[i], 0f) + Vector3.up * 0.5f;
                head.transform.position = top;
                head.transform.rotation = Quaternion.LookRotation(aim - top, Vector3.up);
                head.transform.localScale = new Vector3(1.4f, 0.5f, 0.7f);
                if (mastMaterial != null) head.GetComponent<MeshRenderer>().sharedMaterial = mastMaterial;

                var lightGo = new GameObject($"Flood_{i}");
                lightGo.transform.SetParent(head.transform, false);
                lightGo.transform.localPosition = new Vector3(0f, 0f, 0.5f);
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Spot;
                light.color = color;
                light.intensity = intensity;
                light.range = range;
                light.spotAngle = spotAngle;
                light.innerSpotAngle = spotAngle * 0.55f;
                light.cookie = cookie;
                light.shadowStrength = 0.75f;
                light.shadowBias = 0.05f;
                light.shadowNormalBias = 0.4f;
                // No lightmapBakeType here: that property is editor-only and does not exist in a player build.
                _lights[i] = light;
            }
        }
    }
}
