using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Builds a placeholder 12-DoF quadruped (Unitree Go2 proportions) out of primitives and
    /// ArticulationBodies. Joint names and order match PolicyConfig.ApplyGo2Defaults().
    ///
    /// Frame convention: forward = Unity +X, left = Unity +Z, up = Unity +Y (see CoordinateTransform).
    /// Joint axes are the reflected external axes: hips rotate about Unity X, thighs/calves about Unity Z,
    /// so every JointSpec.sign is -1. The hierarchy is assembled in the zero-angle configuration;
    /// CreatureRig.ResetPose snaps it to the default posture.
    ///
    /// Swap visuals later by parenting a Blender .glb skin under each ArticulationBody (1 unit = 1 m).
    /// </summary>
    public static class QuadrupedFactory
    {
        public struct Dims
        {
            public Vector3 bodySize;      // Unity (length X, height Y, width Z)
            public float bodyMass;
            public Vector3 hipOffset;     // from body centre to hip joint (x, 0, z), mirrored per leg
            public float hipMass;
            public float thighOffsetZ;    // from hip joint to thigh joint along Unity Z (outward)
            public float thighLength;
            public float thighMass;
            public float calfLength;
            public float calfMass;
            public float footRadius;
            public float limbRadius;

            public static Dims Go2 => new Dims
            {
                bodySize = new Vector3(0.3762f, 0.114f, 0.194f),
                bodyMass = 6.921f,
                hipOffset = new Vector3(0.1934f, 0f, 0.0465f),
                hipMass = 0.678f,
                thighOffsetZ = 0.0955f,
                thighLength = 0.213f,
                thighMass = 1.152f,
                calfLength = 0.213f,
                calfMass = 0.214f,
                footRadius = 0.022f,
                limbRadius = 0.024f,
            };
        }

        public static CreatureRig Build(string name, Dims dims, int layer, PhysicsMaterial footMaterial, Material bodyMaterial, Material limbMaterial)
        {
            if (bodyMaterial == null) bodyMaterial = DefaultMaterial(new Color(0.9f, 0.85f, 0.75f));
            if (limbMaterial == null) limbMaterial = DefaultMaterial(new Color(0.25f, 0.27f, 0.3f));
            if (footMaterial == null)
                footMaterial = new PhysicsMaterial("Foot") { staticFriction = 1f, dynamicFriction = 1f, frictionCombine = PhysicsMaterialCombine.Average, bounciness = 0f };

            var rootGo = new GameObject(name);
            rootGo.layer = layer;
            var rig = rootGo.AddComponent<CreatureRig>();

            // ---- base ----
            var baseGo = new GameObject("base");
            baseGo.transform.SetParent(rootGo.transform, false);
            baseGo.layer = layer;
            var baseAb = baseGo.AddComponent<ArticulationBody>();
            baseAb.mass = dims.bodyMass;
            baseAb.immovable = false;
            baseAb.useGravity = true;
            baseAb.linearDamping = 0f;
            baseAb.angularDamping = 0f;
            var bodyCol = baseGo.AddComponent<BoxCollider>();
            bodyCol.size = dims.bodySize;
            AddVisual(baseGo.transform, PrimitiveType.Cube, Vector3.zero, dims.bodySize, bodyMaterial, layer);
            // Head marker so the forward direction is obvious
            AddVisual(baseGo.transform, PrimitiveType.Cube, new Vector3(dims.bodySize.x * 0.5f + 0.03f, 0.02f, 0f), new Vector3(0.06f, 0.06f, 0.09f), limbMaterial, layer);
            rig.root = baseAb;

            string[] legs = { "FL", "FR", "RL", "RR" };
            var feet = new FootContactSensor[4];
            for (int i = 0; i < 4; i++)
            {
                bool front = legs[i][0] == 'F';
                bool left = legs[i][1] == 'L';
                float sx = front ? 1f : -1f;
                float sz = left ? 1f : -1f;      // external +Y (left) = Unity +Z

                Vector3 hipPos = new Vector3(sx * dims.hipOffset.x, 0f, sz * dims.hipOffset.z);
                var hip = MakeLink(baseGo.transform, legs[i] + "_hip_joint", hipPos, Vector3.right, dims.hipMass, layer);
                var hipCol = hip.gameObject.AddComponent<BoxCollider>();
                hipCol.center = new Vector3(0f, 0f, sz * dims.thighOffsetZ * 0.5f);
                hipCol.size = new Vector3(0.06f, 0.05f, dims.thighOffsetZ);
                AddVisual(hip.transform, PrimitiveType.Cube, hipCol.center, hipCol.size, limbMaterial, layer);

                Vector3 thighPos = new Vector3(0f, 0f, sz * dims.thighOffsetZ);
                var thigh = MakeLink(hip.transform, legs[i] + "_thigh_joint", thighPos, Vector3.forward, dims.thighMass, layer);
                AddCapsule(thigh.gameObject, dims.thighLength, dims.limbRadius, limbMaterial, layer, null);

                Vector3 calfPos = new Vector3(0f, -dims.thighLength, 0f);
                var calf = MakeLink(thigh.transform, legs[i] + "_calf_joint", calfPos, Vector3.forward, dims.calfMass, layer);
                AddCapsule(calf.gameObject, dims.calfLength, dims.limbRadius * 0.85f, limbMaterial, layer, null);

                var foot = calf.gameObject.AddComponent<SphereCollider>();
                foot.center = new Vector3(0f, -dims.calfLength, 0f);
                foot.radius = dims.footRadius;
                foot.sharedMaterial = footMaterial;
                AddVisual(calf.transform, PrimitiveType.Sphere, foot.center, Vector3.one * dims.footRadius * 2f, bodyMaterial, layer);

                var sensor = calf.gameObject.AddComponent<FootContactSensor>();
                sensor.footCollider = foot;
                feet[i] = sensor;
            }
            rig.feet = feet;
            return rig;
        }

        static ArticulationBody MakeLink(Transform parent, string name, Vector3 localPos, Vector3 unityAxis, float mass, int layer)
        {
            var go = new GameObject(name);
            go.layer = layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;

            var ab = go.AddComponent<ArticulationBody>();
            ab.mass = mass;
            ab.useGravity = true;
            ab.linearDamping = 0f;
            ab.angularDamping = 0f;
            ab.jointType = ArticulationJointType.RevoluteJoint;
            ab.matchAnchors = true;
            ab.anchorPosition = Vector3.zero;
            // Revolute axis is the anchor's local X axis.
            ab.anchorRotation = Quaternion.FromToRotation(Vector3.right, unityAxis);
            ab.twistLock = ArticulationDofLock.LimitedMotion;
            var d = ab.xDrive;
            d.lowerLimit = -180f;
            d.upperLimit = 180f;
            d.stiffness = 25f;
            d.damping = 0.5f;
            d.forceLimit = 23.7f;
            d.driveType = ArticulationDriveType.Force;   // PD spring in N m/rad; Target would position-lock the joint
            ab.xDrive = d;
            return ab;
        }

        static void AddCapsule(GameObject link, float length, float radius, Material mat, int layer, PhysicsMaterial pm)
        {
            var col = link.AddComponent<CapsuleCollider>();
            col.direction = 1; // Y
            col.center = new Vector3(0f, -length * 0.5f, 0f);
            col.height = length;
            col.radius = radius;
            if (pm != null) col.sharedMaterial = pm;
            var vis = AddVisual(link.transform, PrimitiveType.Capsule, col.center, new Vector3(radius * 2f, length * 0.5f, radius * 2f), mat, layer);
            vis.name = "vis_capsule";
        }

        static GameObject AddVisual(Transform parent, PrimitiveType type, Vector3 localPos, Vector3 scale, Material mat, int layer)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = "vis_" + type;
            go.layer = layer;
            var c = go.GetComponent<Collider>();
            if (c != null) { if (Application.isPlaying) Object.Destroy(c); else Object.DestroyImmediate(c); }
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = scale;
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null && mat != null) mr.sharedMaterial = mat;
            return go;
        }

        static Material DefaultMaterial(Color c)
        {
            Shader s = Shader.Find("Universal Render Pipeline/Lit");
            var m = new Material(s != null ? s : Shader.Find("Standard"));
            m.color = c;
            return m;
        }
    }
}
