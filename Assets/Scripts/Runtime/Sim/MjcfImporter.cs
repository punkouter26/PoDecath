using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Builds an ArticulationBody hierarchy from a MuJoCo MJCF file (the same file used for training),
    /// applying the external-to-Unity coordinate transform and filling a PolicyConfig with the
    /// joint order, limits, defaults (from the "stand" keyframe) and PD gains (from position actuators).
    ///
    /// Bodies with several hinge joints become a chain of massless intermediate links, one revolute
    /// joint each, which reproduces MuJoCo's sequential joint composition exactly.
    /// Supported: body pos/quat, freejoint, hinge joints, capsule/sphere/box geoms (fromto or pos/size),
    /// position actuators (kp, kv, forcerange), keyframe qpos. Unsupported elements are skipped.
    /// </summary>
    public static class MjcfImporter
    {
        public class Options
        {
            public int layer = 0;
            public bool debugVisuals = true;
            public Material visualMaterial;
            public PhysicsMaterial physicsMaterial;
            public float defaultDensity = 1000f;
            public string keyframeName = "stand";
        }

        public class Result
        {
            public GameObject root;
            public CreatureRig rig;
            public JointSpec[] joints;
            public string[] bodyNames;
            public float physicsTimestep = 0.005f;
            /// <summary>Root height (external z) in the keyframe, i.e. the standing pelvis height.</summary>
            public float keyframeRootHeight = 0f;
        }

        class JointInfo
        {
            public string name; public Vector3 axisExt; public float lowerRad; public float upperRad;
            public float kp; public float kv; public float forceLimit; public float damping;
            public ArticulationBody body;
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static Result Build(string xmlText, Options opt)
        {
            opt ??= new Options();
            var doc = new XmlDocument();
            doc.LoadXml(xmlText);
            XmlElement mujoco = doc.DocumentElement;

            bool degrees = true;
            XmlElement compiler = mujoco["compiler"];
            if (compiler != null && compiler.GetAttribute("angle") == "radian") degrees = false;

            float density = opt.defaultDensity;
            XmlElement def = mujoco["default"];
            if (def != null && def["geom"] != null && def["geom"].HasAttribute("density"))
                density = F(def["geom"].GetAttribute("density"));

            var result = new Result();
            XmlElement option = mujoco["option"];
            if (option != null && option.HasAttribute("timestep")) result.physicsTimestep = F(option.GetAttribute("timestep"));

            var actuators = new Dictionary<string, (float kp, float kv, float fl)>();
            XmlElement act = mujoco["actuator"];
            if (act != null)
                foreach (XmlNode n in act.ChildNodes)
                    if (n is XmlElement a && a.HasAttribute("joint"))
                    {
                        float kp = a.HasAttribute("kp") ? F(a.GetAttribute("kp")) : 0f;
                        float kv = a.HasAttribute("kv") ? F(a.GetAttribute("kv")) : 0f;
                        float fl = 0f;
                        if (a.HasAttribute("forcerange")) { var fr = Vec(a.GetAttribute("forcerange")); fl = Mathf.Max(Mathf.Abs(fr[0]), Mathf.Abs(fr[1])); }
                        actuators[a.GetAttribute("joint")] = (kp, kv, fl);
                    }

            float[] keyQpos = null;
            XmlElement keyframe = mujoco["keyframe"];
            if (keyframe != null)
                foreach (XmlNode n in keyframe.ChildNodes)
                    if (n is XmlElement k && (k.GetAttribute("name") == opt.keyframeName || keyQpos == null) && k.HasAttribute("qpos"))
                        keyQpos = Vec(k.GetAttribute("qpos"));

            XmlElement world = mujoco["worldbody"];
            if (world == null) throw new InvalidOperationException("MJCF has no <worldbody>");

            var joints = new List<JointInfo>();
            var bodyNames = new List<string>();
            var rootGo = new GameObject("MjcfCreature");
            rootGo.layer = opt.layer;
            var rig = rootGo.AddComponent<CreatureRig>();

            foreach (XmlNode n in world.ChildNodes)
            {
                if (n is XmlElement b && b.Name == "body")
                {
                    ArticulationBody ab = BuildBody(b, rootGo.transform, null, opt, density, degrees, actuators, joints, bodyNames);
                    if (rig.root == null) rig.root = ab;
                }
            }

            // Joint specs (policy order = document order). qpos layout: 7 root + one per hinge.
            int hasFree = rig.root != null && !rig.root.immovable ? 7 : 0;
            var specs = new JointSpec[joints.Count];
            for (int i = 0; i < joints.Count; i++)
            {
                JointInfo j = joints[i];
                float defaultPos = 0f;
                if (keyQpos != null && hasFree + i < keyQpos.Length) defaultPos = keyQpos[hasFree + i];
                specs[i] = new JointSpec
                {
                    name = j.name, defaultPos = defaultPos, lower = j.lowerRad, upper = j.upperRad, sign = -1f,
                    stiffness = j.kp, damping = j.kv + j.damping, forceLimit = j.forceLimit,
                };
            }
            if (keyQpos != null && hasFree == 7 && keyQpos.Length >= 3) result.keyframeRootHeight = keyQpos[2];
            result.root = rootGo;
            result.rig = rig;
            result.joints = specs;
            result.bodyNames = bodyNames.ToArray();
            rig.feet = rootGo.GetComponentsInChildren<FootContactSensor>(true);
            return result;
        }

        static ArticulationBody BuildBody(XmlElement b, Transform parent, ArticulationBody parentAb, Options opt, float density,
            bool degrees, Dictionary<string, (float kp, float kv, float fl)> actuators, List<JointInfo> joints, List<string> bodyNames)
        {
            string name = b.HasAttribute("name") ? b.GetAttribute("name") : "body" + bodyNames.Count;
            Vector3 posExt = b.HasAttribute("pos") ? V3(b.GetAttribute("pos")) : Vector3.zero;
            Quaternion rotExt = Quaternion.identity;
            if (b.HasAttribute("quat")) { var q = Vec(b.GetAttribute("quat")); rotExt = new Quaternion(q[1], q[2], q[3], q[0]); }
            Vector3 posU = CoordinateTransform.ExternalToUnity(posExt);
            Quaternion rotU = CoordinateTransform.ExternalToUnity(rotExt);

            // Collect this body's joints in order
            var hinges = new List<XmlElement>();
            bool free = false;
            foreach (XmlNode n in b.ChildNodes)
            {
                if (n is XmlElement e)
                {
                    if (e.Name == "freejoint" || (e.Name == "joint" && e.GetAttribute("type") == "free")) free = true;
                    else if (e.Name == "joint" && (e.GetAttribute("type") == "hinge" || !e.HasAttribute("type"))) hinges.Add(e);
                }
            }

            Transform chainParent = parent;
            ArticulationBody chainParentAb = parentAb;
            ArticulationBody last = null;
            int count = Mathf.Max(1, hinges.Count);
            for (int k = 0; k < count; k++)
            {
                bool isReal = k == count - 1;
                var go = new GameObject(isReal ? name : $"{name}__link{k}");
                go.layer = opt.layer;
                go.transform.SetParent(chainParent, false);
                go.transform.localPosition = k == 0 ? posU : Vector3.zero;
                go.transform.localRotation = k == 0 ? rotU : Quaternion.identity;
                var ab = go.AddComponent<ArticulationBody>();
                ab.useGravity = true;
                ab.linearDamping = 0f;
                ab.angularDamping = 0f;
                if (parentAb == null)
                {
                    ab.immovable = !free;
                }
                if (hinges.Count > 0)
                {
                    XmlElement jn = hinges[k];
                    Vector3 axisExt = jn.HasAttribute("axis") ? V3(jn.GetAttribute("axis")).normalized : Vector3.forward;
                    Vector3 axisU = CoordinateTransform.ExternalToUnity(axisExt);
                    float lo = -Mathf.PI, hi = Mathf.PI;
                    if (jn.HasAttribute("range")) { var r = Vec(jn.GetAttribute("range")); lo = r[0]; hi = r[1]; if (degrees) { lo *= Mathf.Deg2Rad; hi *= Mathf.Deg2Rad; } }
                    string jname = jn.HasAttribute("name") ? jn.GetAttribute("name") : name + "_j" + k;
                    float jd = jn.HasAttribute("damping") ? F(jn.GetAttribute("damping")) : 0f;
                    var info = new JointInfo { name = jname, axisExt = axisExt, lowerRad = lo, upperRad = hi, damping = jd, body = ab };
                    if (actuators.TryGetValue(jname, out var g)) { info.kp = g.kp; info.kv = g.kv; info.forceLimit = g.fl; }
                    joints.Add(info);
                    go.name = jname;   // CreatureRig binds joints by GameObject name
                    ab.jointType = ArticulationJointType.RevoluteJoint;
                    ab.matchAnchors = true;
                    ab.anchorPosition = Vector3.zero;
                    ab.anchorRotation = Quaternion.FromToRotation(Vector3.right, axisU);
                    ab.twistLock = ArticulationDofLock.LimitedMotion;
                    float s = -1f;
                    float a = s * lo * Mathf.Rad2Deg, c = s * hi * Mathf.Rad2Deg;
                    var d = ab.xDrive;
                    d.lowerLimit = Mathf.Min(a, c); d.upperLimit = Mathf.Max(a, c);
                    d.stiffness = info.kp; d.damping = info.kv + jd; d.forceLimit = info.forceLimit > 0 ? info.forceLimit : float.MaxValue;
                    d.driveType = ArticulationDriveType.Force;   // real PD spring; Target would position-lock the joint
                    ab.xDrive = d;
                }
                if (!isReal)
                {
                    ab.mass = 0.02f;
                    ab.inertiaTensor = Vector3.one * 1e-4f;
                    ab.inertiaTensorRotation = Quaternion.identity;
                }
                chainParent = go.transform;
                chainParentAb = ab;
                last = ab;
            }

            // Geoms on the real body
            float mass = 0f;
            bool anyFoot = false;
            foreach (XmlNode n in b.ChildNodes)
            {
                if (n is XmlElement g && g.Name == "geom")
                {
                    mass += AddGeom(g, last.gameObject, opt, density, out bool isFoot);
                    anyFoot |= isFoot;
                }
            }
            if (b.HasAttribute("mass")) mass = F(b.GetAttribute("mass"));
            last.mass = Mathf.Max(mass, 0.05f);
            last.gameObject.AddComponent<MjcfBodyTag>().bodyName = name;
            if (anyFoot)
            {
                var sensor = last.gameObject.AddComponent<FootContactSensor>();
                sensor.footCollider = null; // any contact on this body counts
            }
            bodyNames.Add(name);

            foreach (XmlNode n in b.ChildNodes)
                if (n is XmlElement child && child.Name == "body")
                    BuildBody(child, last.transform, last, opt, density, degrees, actuators, joints, bodyNames);
            return last;
        }

        static float AddGeom(XmlElement g, GameObject body, Options opt, float density, out bool isFoot)
        {
            string type = g.HasAttribute("type") ? g.GetAttribute("type") : "sphere";
            string gname = g.HasAttribute("name") ? g.GetAttribute("name") : type;
            isFoot = gname.StartsWith("foot", StringComparison.OrdinalIgnoreCase);
            float d = g.HasAttribute("density") ? F(g.GetAttribute("density")) : density;
            float[] size = g.HasAttribute("size") ? Vec(g.GetAttribute("size")) : new[] { 0.05f };
            var holder = new GameObject("geom_" + gname);
            holder.layer = opt.layer;
            holder.transform.SetParent(body.transform, false);
            float mass = 0f;

            if (type == "capsule" || type == "cylinder")
            {
                float r = size[0];
                Vector3 a, bpt;
                if (g.HasAttribute("fromto")) { var ft = Vec(g.GetAttribute("fromto")); a = new Vector3(ft[0], ft[1], ft[2]); bpt = new Vector3(ft[3], ft[4], ft[5]); }
                else { Vector3 p = g.HasAttribute("pos") ? V3(g.GetAttribute("pos")) : Vector3.zero; float h = size.Length > 1 ? size[1] : r; a = p - Vector3.forward * h; bpt = p + Vector3.forward * h; }
                Vector3 aU = CoordinateTransform.ExternalToUnity(a), bU = CoordinateTransform.ExternalToUnity(bpt);
                Vector3 mid = 0.5f * (aU + bU);
                Vector3 dir = bU - aU;
                float len = dir.magnitude;
                holder.transform.localPosition = mid;
                holder.transform.localRotation = len > 1e-5f ? Quaternion.FromToRotation(Vector3.up, dir / len) : Quaternion.identity;
                var col = holder.AddComponent<CapsuleCollider>();
                col.direction = 1; col.radius = r; col.height = len + 2f * r; col.center = Vector3.zero;
                if (opt.physicsMaterial != null) col.sharedMaterial = opt.physicsMaterial;
                mass = d * (Mathf.PI * r * r * len + 4f / 3f * Mathf.PI * r * r * r);
                if (opt.debugVisuals) Visual(holder.transform, PrimitiveType.Capsule, new Vector3(2 * r, (len + 2 * r) * 0.5f, 2 * r), opt);
            }
            else if (type == "sphere")
            {
                float r = size[0];
                Vector3 p = g.HasAttribute("pos") ? V3(g.GetAttribute("pos")) : Vector3.zero;
                holder.transform.localPosition = CoordinateTransform.ExternalToUnity(p);
                var col = holder.AddComponent<SphereCollider>();
                col.radius = r;
                if (opt.physicsMaterial != null) col.sharedMaterial = opt.physicsMaterial;
                mass = d * 4f / 3f * Mathf.PI * r * r * r;
                if (opt.debugVisuals) Visual(holder.transform, PrimitiveType.Sphere, Vector3.one * 2 * r, opt);
            }
            else if (type == "box")
            {
                Vector3 half = new Vector3(size[0], size.Length > 1 ? size[1] : size[0], size.Length > 2 ? size[2] : size[0]);
                Vector3 p = g.HasAttribute("pos") ? V3(g.GetAttribute("pos")) : Vector3.zero;
                holder.transform.localPosition = CoordinateTransform.ExternalToUnity(p);
                Vector3 halfU = CoordinateTransform.ExternalToUnity(half);
                var col = holder.AddComponent<BoxCollider>();
                col.size = 2f * halfU;
                if (opt.physicsMaterial != null) col.sharedMaterial = opt.physicsMaterial;
                mass = d * 8f * half.x * half.y * half.z;
                if (opt.debugVisuals) Visual(holder.transform, PrimitiveType.Cube, 2f * halfU, opt);
            }
            else
            {
                UnityEngine.Object.Destroy(holder);
            }
            if (g.HasAttribute("mass")) mass = F(g.GetAttribute("mass"));
            return mass;
        }

        static void Visual(Transform parent, PrimitiveType type, Vector3 scale, Options opt)
        {
            var v = GameObject.CreatePrimitive(type);
            v.name = "vis";
            v.layer = opt.layer;
            var c = v.GetComponent<Collider>();
            if (c != null) { if (Application.isPlaying) UnityEngine.Object.Destroy(c); else UnityEngine.Object.DestroyImmediate(c); }
            v.transform.SetParent(parent, false);
            v.transform.localScale = scale;
            if (opt.visualMaterial != null) v.GetComponent<MeshRenderer>().sharedMaterial = opt.visualMaterial;
        }

        static float F(string s) => float.Parse(s.Trim(), Inv);

        static float[] Vec(string s)
        {
            string[] parts = s.Trim().Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var v = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++) v[i] = float.Parse(parts[i], Inv);
            return v;
        }

        static Vector3 V3(string s) { var v = Vec(s); return new Vector3(v[0], v.Length > 1 ? v[1] : 0f, v.Length > 2 ? v[2] : 0f); }
    }
}
