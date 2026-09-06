using UnityEngine;
using PoDecath.Env;
using PoDecath.Sim;

namespace PoDecath.Fx
{
    /// <summary>
    /// A soft dark disc on the deck under one athlete.
    ///
    /// It exists for the mobile tier, where the render pipeline asset has no additional-light shadows, one
    /// cascade over 55 m and no soft filtering — which in practice means the only shadow an athlete gets is
    /// a hard-edged blob that flickers as the cascade shifts, and at 0.8 render scale it is barely there at
    /// all. Without something under the feet the athletes look like they are hovering over the roof, and on
    /// a deck this narrow that is the difference between watching a race and watching a bug.
    ///
    /// It is also useful on the desktop tier, where it is kept small and faint as a contact shadow: the
    /// cascaded shadow map cannot resolve the few centimetres where a foot actually meets the asphalt.
    ///
    /// The disc is raycast down onto whatever is beneath, so it follows the deck over the bends, drops onto
    /// the runway on the infield, and vanishes when there is nothing below (which on a rooftop track
    /// happens the moment somebody goes over the edge).
    /// </summary>
    [DefaultExecutionOrder(75)]
    public class BlobShadow : MonoBehaviour
    {
        [Header("Wiring")]
        public AthleteRig rig;
        public HeuristicRunner heuristic;
        public Material material;

        [Header("Look")]
        [Tooltip("Diameter directly under the athlete, in metres.")]
        public float size = 0.9f;
        [Tooltip("Opacity when the athlete is on the ground. Halved as they rise off it.")]
        public float opacity = 0.5f;
        [Tooltip("Height above the athlete's origin the ray starts from.")]
        public float rayStart = 1.4f;
        [Tooltip("How far down to look for something to land on.")]
        public float rayLength = 6f;
        [Tooltip("Above this height off the ground the shadow has faded out entirely — a jumper in flight "
               + "should not drag a hard disc along the sand under them.")]
        public float fadeHeight = 1.6f;

        Transform _quad;
        MeshRenderer _renderer;
        MaterialPropertyBlock _block;
        static readonly int ColorId = Shader.PropertyToID("_BaseColor");

        void Start()
        {
            // Built by hand rather than with CreatePrimitive: a primitive quad arrives with a MeshCollider,
            // Destroy() on it is deferred to the end of the frame, and the ArticulationBody this hangs off
            // rejects the concave collider before that happens. Nothing here should have a collider at all —
            // it is a picture of a shadow, not a thing in the world.
            var go = new GameObject("BlobShadow");
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * size;
            go.AddComponent<MeshFilter>().sharedMesh = Disc();

            _quad = go.transform;
            _renderer = go.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            if (material != null) _renderer.sharedMaterial = material;
            _block = new MaterialPropertyBlock();

            // On the desktop tier the real shadow does the work, so this shrinks to a contact darkening
            // right under the feet rather than a second shadow arguing with the first.
            if (!RenderTier.IsMobile)
            {
                size *= 0.55f;
                opacity *= 0.45f;
                go.transform.localScale = Vector3.one * size;
            }
        }

        void LateUpdate()
        {
            if (_quad == null) return;
            Vector3 origin = Origin();

            if (!Physics.Raycast(origin + Vector3.up * rayStart, Vector3.down, out RaycastHit hit, rayStart + rayLength,
                                 ~0, QueryTriggerInteraction.Ignore))
            {
                _renderer.enabled = false;
                return;
            }

            float above = Mathf.Max(0f, origin.y - hit.point.y);
            float fade = 1f - Mathf.Clamp01(above / Mathf.Max(0.01f, fadeHeight));
            if (fade <= 0.01f) { _renderer.enabled = false; return; }

            _renderer.enabled = true;
            // A shadow spreads and softens as its caster rises; scaling with height is the cheapest
            // approximation of that and it is the cue that sells a jumper leaving the board.
            _quad.position = hit.point + hit.normal * 0.02f;
            // The mesh's face points along its local +Z, so the surface normal is what the transform looks
            // along; the world +Z reference only fixes the spin, which for a radial disc is invisible anyway.
            _quad.rotation = Quaternion.LookRotation(hit.normal, Vector3.forward);
            _quad.localScale = Vector3.one * size * Mathf.Lerp(1f, 1.6f, 1f - fade);

            _block.SetColor(ColorId, new Color(0f, 0f, 0f, opacity * fade));
            _renderer.SetPropertyBlock(_block);
        }

        static Mesh _disc;

        /// <summary>A unit quad in its own XY plane, facing local +Z. Shared by every athlete.</summary>
        static Mesh Disc()
        {
            if (_disc != null) return _disc;
            _disc = new Mesh { name = "BlobShadow_Quad" };
            _disc.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
            });
            _disc.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) });
            _disc.SetNormals(new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward });
            _disc.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            _disc.RecalculateBounds();
            return _disc;
        }

        Vector3 Origin()
        {
            if (rig != null) return rig.BasePosition;
            if (heuristic != null) return heuristic.transform.position;
            return transform.position;
        }
    }
}
