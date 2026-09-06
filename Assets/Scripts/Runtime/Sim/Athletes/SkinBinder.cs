using System;
using System.Collections.Generic;
using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Drives a skinned mesh's armature bones from ArticulationBody transforms.
    /// Bind() must run while the physics rig is in its zero-angle configuration (right after import),
    /// which matches the skin's rest pose because the MJCF was generated from the same bones.
    /// Unmapped bones (fingers, spine chain, neck, head, toes) follow their mapped parents rigidly.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public class SkinBinder : MonoBehaviour
    {
        [Serializable]
        public struct BoneMap { public string body; public string bone; }

        [Tooltip("Physics body name -> armature bone name. Defaults match the Matt (Mixamo) rig.")]
        public List<BoneMap> map = new List<BoneMap>
        {
            new BoneMap { body = "pelvis", bone = "Hips" },
            new BoneMap { body = "torso", bone = "Spine" },
            new BoneMap { body = "upper_arm_l", bone = "LeftArm" },
            new BoneMap { body = "forearm_l", bone = "LeftForeArm" },
            new BoneMap { body = "upper_arm_r", bone = "RightArm" },
            new BoneMap { body = "forearm_r", bone = "RightForeArm" },
            new BoneMap { body = "thigh_l", bone = "LeftUpLeg" },
            new BoneMap { body = "shin_l", bone = "LeftLeg" },
            new BoneMap { body = "foot_l", bone = "LeftFoot" },
            new BoneMap { body = "thigh_r", bone = "RightUpLeg" },
            new BoneMap { body = "shin_r", bone = "RightLeg" },
            new BoneMap { body = "foot_r", bone = "RightFoot" },
        };
        [Tooltip("Rotation applied to the skin root so its rest pose faces the rig's forward axis (+X).")]
        public Vector3 skinRootEuler = new Vector3(0f, 90f, 0f);
        public GameObject skin;

        struct Link { public Transform body; public Transform bone; public Quaternion rotOffset; public Vector3 posOffset; }
        readonly List<Link> _links = new List<Link>();
        public bool IsBound => _links.Count > 0;

        public void Bind(AthleteRig rig, GameObject skinInstance)
        {
            skin = skinInstance;
            _links.Clear();
            if (rig == null || skin == null) return;

            skin.transform.SetParent(rig.transform, false);
            skin.transform.localPosition = Vector3.zero;
            skin.transform.localRotation = Quaternion.Euler(skinRootEuler);
            skin.transform.localScale = Vector3.one;

            var bodies = new Dictionary<string, Transform>();
            foreach (var t in rig.GetComponentsInChildren<Transform>(true))
                if (!bodies.ContainsKey(t.name)) bodies[t.name] = t;
            // MjcfImporter renames joint-bearing bodies to their last joint name; keep a body-name lookup too.
            var bones = new Dictionary<string, Transform>();
            foreach (var t in skin.GetComponentsInChildren<Transform>(true))
                if (!bones.ContainsKey(t.name)) bones[t.name] = t;

            foreach (BoneMap m in map)
            {
                Transform body = FindBody(rig, bodies, m.body);
                if (body == null || !bones.TryGetValue(m.bone, out Transform bone))
                {
                    Debug.LogWarning($"[SkinBinder] Could not bind body '{m.body}' to bone '{m.bone}'.", this);
                    continue;
                }
                Quaternion inv = Quaternion.Inverse(body.rotation);
                _links.Add(new Link { body = body, bone = bone, rotOffset = inv * bone.rotation, posOffset = inv * (bone.position - body.position) });
            }

            foreach (var smr in skin.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                smr.updateWhenOffscreen = true;
        }

        static Transform FindBody(AthleteRig rig, Dictionary<string, Transform> byName, string bodyName)
        {
            if (byName.TryGetValue(bodyName, out var t)) return t;
            // Importer convention: a body with joints is named after its last joint; search by tag stored on ArticulationBody.
            foreach (var ab in rig.GetComponentsInChildren<ArticulationBody>(true))
            {
                var tag = ab.GetComponent<MjcfBodyTag>();
                if (tag != null && tag.bodyName == bodyName) return ab.transform;
            }
            return null;
        }

        void LateUpdate()
        {
            for (int i = 0; i < _links.Count; i++)
            {
                Link l = _links[i];
                if (l.body == null || l.bone == null) continue;
                l.bone.rotation = l.body.rotation * l.rotOffset;
                l.bone.position = l.body.position + l.body.rotation * l.posOffset;
            }
        }
    }

    /// <summary>Records the original MJCF body name on an ArticulationBody GameObject.</summary>
    public class MjcfBodyTag : MonoBehaviour
    {
        public string bodyName;
    }
}
