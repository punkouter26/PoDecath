using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using PoDecath.Sim;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// Works out how one rigged character's skeleton lines up with the twelve MJCF bodies, so a model can
    /// be dropped into <c>Assets/Models/Characters</c> and raced without anybody hand-typing a bone map.
    ///
    /// It reads geometry rather than names. Every generator that has turned up in this project names the
    /// same joint differently -- Mixamo/Avaturn call the shin <c>LeftLeg</c>, AccuRig calls it
    /// <c>CC_Base_L_Calf</c>, the Trump export calls it <c>bone_27</c> -- and AccuRig additionally hangs
    /// twist, share and breast bones off the real ones, so a name table is a list of the generators
    /// somebody has already met. A skeleton's shape, on the other hand, is the same everywhere: the leg is
    /// the chain that drops furthest, the arm is the chain that reaches furthest sideways, and the toes
    /// point the way the athlete faces. That holds for all seven models in the roster and, unlike a name
    /// table, for the eighth nobody has seen yet.
    ///
    /// The inferred map is written into the <see cref="AthleteDefinition"/> as plain text, so it stays
    /// visible in the inspector and can be corrected by hand if a rig ever defeats the geometry.
    /// </summary>
    public static class SkeletonMapper
    {
        public class Result
        {
            public List<SkinBinder.BoneMap> map = new List<SkinBinder.BoneMap>();
            public Vector3 rootEuler = new Vector3(0f, 90f, 0f);
            /// <summary>Hip height above the lowest bone, in metres. Sanity: a human is 0.85-0.95.</summary>
            public float hipHeight;
            public readonly List<string> notes = new List<string>();
            public bool Complete => map.Count == 12;
        }

        /// <summary>
        /// Reads one imported model asset and returns the map. The asset is instantiated into the open
        /// scene for the duration -- bone world positions are the whole input and a prefab on disk has
        /// none -- and destroyed again before returning, so the scene is left as it was found.
        /// </summary>
        public static Result Infer(GameObject modelAsset)
        {
            var r = new Result();
            if (modelAsset == null) { r.notes.Add("no model asset"); return r; }

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset);
            if (inst == null) { r.notes.Add("could not instantiate"); return r; }
            inst.transform.position = Vector3.zero;
            inst.transform.rotation = Quaternion.identity;
            try { Infer(inst, r); }
            finally { Object.DestroyImmediate(inst); }
            return r;
        }

        static void Infer(GameObject inst, Result r)
        {
            var bones = new HashSet<Transform>();
            foreach (var smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                foreach (Transform b in smr.bones) if (b != null) bones.Add(b);
                if (smr.rootBone != null) bones.Add(smr.rootBone);
            }
            if (bones.Count < 8) { r.notes.Add($"only {bones.Count} skinned bones; not a character rig"); return; }

            Transform pelvis = Shallowest(bones);
            // A few exporters hang the skeleton off a zero-length "root"/"reference" node that is skinned
            // but is not the hips. It has exactly one skinned child and sits on the floor; step over it.
            while (IsPassThroughRoot(pelvis, bones, out Transform only)) pelvis = only;

            float ground = float.MaxValue;
            foreach (Transform b in bones) ground = Mathf.Min(ground, b.position.y);
            r.hipHeight = pelvis.position.y - ground;

            // The two lowest bones that are on opposite sides of the body are the toes, and the paths down
            // to them are the legs. "Opposite sides" is measured before the facing is known, which is fine:
            // the sagittal plane is the one the hips sit on whichever way the athlete is pointing.
            if (!LowestPair(bones, pelvis, out Transform toeA, out Transform toeB))
            { r.notes.Add("could not find two leg chains"); return; }

            List<Transform> legA = PathFrom(pelvis, toeA), legB = PathFrom(pelvis, toeB);
            if (!Limb(legA, out Transform thighA, out Transform shinA, out Transform footA) ||
                !Limb(legB, out Transform thighB, out Transform shinB, out Transform footB))
            { r.notes.Add("leg chains too short to split into thigh/shin/foot"); return; }

            // Facing. The toes say roughly which way the athlete points -- the one direction every rest
            // pose agrees on, where the hips and the mesh bounds do not -- but only roughly: these models
            // are hand-posed and their feet splay, which on its own put Grandpa 13 degrees off square. So
            // the toes are used for the *sign* only, and the angle comes off the body's lateral axis,
            // squared up against it. Running square to the body is what makes a stride look like a stride.
            //
            // Provisional here, on the hips alone, because that is all that has been identified yet and it
            // is plenty to tell left from right. It is settled properly once the shoulders are known, a
            // few lines below.
            Vector3 rough = Flat(Toe(footA) - footA.position) + Flat(Toe(footB) - footB.position);
            if (rough.sqrMagnitude < 1e-6f)
            {
                rough = Vector3.forward;
                r.notes.Add("no toe bones; assumed the model faces +Z");
            }
            Vector3 lateral = Across(thighA, thighB);
            Vector3 forward = Square(lateral, rough);
            Vector3 right = Vector3.Cross(Vector3.up, forward);

            // Torso: the pelvis's own child that heads upstairs rather than down a leg. Structural, because
            // the name is Spine on one rig, Spine02 on the next and Waist on the third.
            Transform torso = Spine(pelvis, bones, legA, legB);
            if (torso == null) { r.notes.Add("no spine child under the pelvis"); return; }

            // The arms, found from the chest down rather than from the hands up. Searching within the torso
            // keeps the legs out of it; the furthest bone each way is then somewhere on an arm, and where
            // the two sides meet is the chest.
            if (!ArmTips(torso, right, out Transform tipL, out Transform tipR))
            { r.notes.Add("could not find two arm chains"); return; }
            Transform chest = CommonAncestor(tipL, tipR);
            if (chest == null) { r.notes.Add("the two arms do not meet at a chest"); return; }

            if (!Arm(PathFrom(chest, tipL), r, out Transform upperL, out Transform foreL) ||
                !Arm(PathFrom(chest, tipR), r, out Transform upperR, out Transform foreR))
            { r.notes.Add("arm chains too short to split into upper arm/forearm"); return; }

            // Now the shoulders are known, settle the facing on the hips and the shoulders together.
            //
            // A symmetrical rig does not care -- the zombie, Trump and Matt Avaturn all read 90.0 degrees
            // off either -- but a hand-posed one does, and no single pair is trustworthy on its own: the
            // doggy model's hips sit 9 degrees round from the rest of it, while Grandpa's and Nick's hips
            // are square and it is their *feet* that are 14 degrees out, because a figure standing with
            // one foot advanced has an ankle line that says nothing about which way it faces. Measured
            // across all eight, hips and shoulders agree within 3 degrees of each other on every model,
            // the ankles disagree with both by up to 15, and averaging the two good pairs brings the worst
            // model in from 9 degrees off square to 3. So: the two structural pairs, and not the feet.
            //
            // Signed against `lateral` rather than against `right`, which is not the same thing: the legs
            // are found lowest-first, so on six of the eight models leg A is the right one and `lateral`
            // runs right to left. Signing against `right` subtracted the shoulders on exactly those six.
            Vector3 shoulders = Across(upperL, upperR);
            lateral += shoulders * Mathf.Sign(Vector3.Dot(shoulders, lateral));
            forward = Square(lateral, rough);
            r.rootEuler = new Vector3(0f, Vector3.SignedAngle(forward, Vector3.right, Vector3.up), 0f);

            // Sides from the provisional facing, which is more than accurate enough to tell left from
            // right -- the refinement above moves the angle by degrees, never by the 90 it would take to
            // swap a leg over.
            bool aIsRight = Vector3.Dot(thighA.position - pelvis.position, right) > 0f;
            Add(r, "pelvis", pelvis);
            Add(r, "torso", torso);
            Add(r, aIsRight ? "thigh_r" : "thigh_l", thighA);
            Add(r, aIsRight ? "shin_r" : "shin_l", shinA);
            Add(r, aIsRight ? "foot_r" : "foot_l", footA);
            Add(r, aIsRight ? "thigh_l" : "thigh_r", thighB);
            Add(r, aIsRight ? "shin_l" : "shin_r", shinB);
            Add(r, aIsRight ? "foot_l" : "foot_r", footB);
            Add(r, "upper_arm_l", upperL);
            Add(r, "forearm_l", foreL);
            Add(r, "upper_arm_r", upperR);
            Add(r, "forearm_r", foreR);

            // Two bones sharing a name bind to whichever the dictionary saw first, so say so here rather
            // than let it show up as a limb that does not move.
            var seen = new HashSet<string>();
            foreach (SkinBinder.BoneMap m in r.map)
                if (!seen.Add(m.bone)) r.notes.Add($"bone name '{m.bone}' is used more than once");
        }

        static void Add(Result r, string body, Transform bone)
            => r.map.Add(new SkinBinder.BoneMap { body = body, bone = bone.name });

        // ------------------------------------------------------------------ limb splitting

        /// <summary>
        /// Splits a path from the pelvis to a fingertip or toe into the three bones that matter.
        ///
        /// The limb proper is where the chain actually travels: the two consecutive steps that between them
        /// cover more distance than any other pair. For a leg that is hip-to-knee plus knee-to-ankle, which
        /// lands on the ankle and skips the toes; for an arm it is shoulder-to-elbow plus elbow-to-wrist,
        /// which skips the clavicle at one end and the fingers at the other. Twist and share bones sit at
        /// zero length on top of their parents, so they never win a step and never get picked.
        ///
        /// Plain distance, deliberately, and not distance measured sideways: these athletes stand in an
        /// A-pose with their arms hanging, so an arm covers most of its length downwards and a sideways
        /// measure scored the clavicle above the humerus on three of the seven models.
        /// </summary>
        static bool Limb(List<Transform> chain, out Transform a, out Transform b, out Transform c)
        {
            a = b = c = null;
            if (chain.Count < 4) return false;   // pelvis + three bones is the shortest limb worth binding

            int best = -1; float bestSpan = 0f;
            for (int k = 1; k + 2 < chain.Count; k++)
            {
                float span = Vector3.Distance(chain[k + 1].position, chain[k].position)
                           + Vector3.Distance(chain[k + 2].position, chain[k + 1].position);
                if (span > bestSpan) { bestSpan = span; best = k; }
            }
            if (best < 0) return false;
            a = chain[best]; b = chain[best + 1]; c = chain[best + 2];
            return true;
        }

        /// <summary>
        /// The upper arm and forearm: counted out from the chest, which is the one place on an arm where
        /// every humanoid rig agrees. Chest, collarbone, humerus, forearm, hand, in that order, always.
        ///
        /// The arm is not split the way the leg is, and two earlier attempts to do it by measurement are
        /// worth recording because both looked right on most of the roster. Taking the two longest steps
        /// works on a leg, where the thigh and shin dwarf their neighbours, and fails on an arm: these
        /// models are hand-posed and asymmetric, and on RIGGED_Matt's right side the collarbone is 19 cm
        /// against a 17 cm forearm, so the longest pair started one bone too early -- an arm one joint out,
        /// on one side only, which on the deck reads as a shoulder bending the wrong way. Walking in from
        /// the fingertips to the first bone that branches fails differently: AccuRig hangs twist and share
        /// bones off the forearm, so the forearm branches too, and the Trump model's fingers are not
        /// skinned at all so there is nothing to walk in from. Position in the chain has neither problem.
        /// </summary>
        static bool Arm(List<Transform> chain, Result r, out Transform upper, out Transform fore)
        {
            upper = fore = null;
            if (chain.Count < 4) return false;   // chest, collarbone, humerus, forearm

            upper = chain[2];
            fore = chain[3];
            // A rig that hangs the arm straight off the chest with no collarbone would put everything one
            // bone late. None of the seven does, and the giveaway if an eighth did is a collarbone longer
            // than the humerus, so say so rather than bind a wrong arm quietly.
            float clavicle = Vector3.Distance(chain[1].position, chain[2].position);
            float humerus = Vector3.Distance(chain[2].position, chain[3].position);
            if (clavicle > humerus)
                r.notes.Add($"'{chain[1].name}' is longer than '{chain[2].name}'; check this arm has a collarbone");
            return true;
        }

        /// <summary>The bone furthest from the ankle inside the foot: the toe, whichever generator made it.</summary>
        static Vector3 Toe(Transform foot)
        {
            Transform best = null; float far = 0f;
            foreach (Transform t in foot.GetComponentsInChildren<Transform>(true))
            {
                float d = Flat(t.position - foot.position).magnitude;
                if (d > far) { far = d; best = t; }
            }
            return best != null ? best.position : foot.position;
        }

        /// <summary>
        /// The bone reaching furthest each way inside the torso. It need not be the hand -- on the Trump
        /// model, whose arms hang and whose fingers carry no skin weights, the furthest bone out is the
        /// elbow -- because all it has to do is be somewhere past the shoulder on each side. Searching the
        /// torso rather than the whole rig is what keeps a wide stance from offering up a foot.
        /// </summary>
        static bool ArmTips(Transform torso, Vector3 right, out Transform left, out Transform rightTip)
        {
            left = rightTip = null;
            float farL = 0f, farR = 0f;
            foreach (Transform b in torso.GetComponentsInChildren<Transform>(true))
            {
                float side = Vector3.Dot(b.position - torso.position, right);
                if (side > farR) { farR = side; rightTip = b; }
                if (-side > farL) { farL = -side; left = b; }
            }
            return left != null && rightTip != null && farL > 0.05f && farR > 0.05f;
        }

        /// <summary>The pelvis child that climbs instead of descending into a leg.</summary>
        static Transform Spine(Transform pelvis, HashSet<Transform> bones, List<Transform> legA, List<Transform> legB)
        {
            Transform best = null;
            foreach (Transform child in pelvis)
            {
                if (!bones.Contains(child)) continue;
                // Skip a child that a leg path goes through; the AccuRig rig hangs both legs off a
                // zero-length CC_Base_Pelvis that would otherwise look exactly like a spine.
                if (legA.Contains(child) || legB.Contains(child)) continue;
                if (best == null || child.position.y > best.position.y) best = child;
            }
            return best;
        }

        // ------------------------------------------------------------------ hierarchy helpers

        static Transform Shallowest(HashSet<Transform> bones)
        {
            Transform best = null; int shallow = int.MaxValue;
            foreach (Transform b in bones)
            {
                int d = 0;
                for (Transform t = b; t.parent != null; t = t.parent) d++;
                if (d < shallow) { shallow = d; best = b; }
            }
            return best;
        }

        static bool IsPassThroughRoot(Transform t, HashSet<Transform> bones, out Transform only)
        {
            only = null;
            int kids = 0;
            foreach (Transform c in t)
                if (bones.Contains(c)) { kids++; only = c; }
            // One skinned child, no distance between them: a holder, not a joint.
            return kids == 1 && Vector3.Distance(t.position, only.position) < 1e-4f && !HasTwoLegs(t, bones);
        }

        /// <summary>True if this bone already branches into both legs, in which case it IS the pelvis.</summary>
        static bool HasTwoLegs(Transform t, HashSet<Transform> bones)
        {
            int below = 0;
            foreach (Transform c in t)
                if (bones.Contains(c) && c.position.y < t.position.y - 1e-4f) below++;
            return below >= 2;
        }

        /// <summary>
        /// The lowest bone in the rig, and the lowest bone in the *other* leg: one toe apiece.
        ///
        /// "Other leg" is the part that needs care. The sagittal plane is not known yet -- the facing comes
        /// later and depends on this answer -- and a toe sticks out forwards further than it sticks out
        /// sideways, so a left/right test picks the wrong foot on an ordinary human rig. What separates the
        /// far toe from a share bone on the near foot is where the two chains part company: up at the hips
        /// for the far one, down at the ankle for the near one. So: the lowest bone whose parting point is
        /// up at hip level.
        ///
        /// Measured in metres rather than in hops up the hierarchy, because the number of hops is a fact
        /// about the exporter and not about the body. The AccuRig zombie hangs both legs off an extra
        /// zero-length CC_Base_Pelvis under the hips, which puts its far toe one hop deeper than every
        /// other model's -- enough, when this counted hops, to lose to the waist and leave the athlete with
        /// a leg two bones long.
        /// </summary>
        static bool LowestPair(HashSet<Transform> bones, Transform pelvis, out Transform a, out Transform b)
        {
            b = null;
            var sorted = new List<Transform>(bones);
            sorted.Sort((x, y) => x.position.y.CompareTo(y.position.y));
            a = sorted[0];

            float ground = sorted[0].position.y;
            float hipLevel = ground + 0.5f * (pelvis.position.y - ground);
            foreach (Transform t in sorted)   // ascending height: the first that qualifies is the lowest
            {
                if (t == a || IsRelated(t, a)) continue;
                Transform lca = CommonAncestor(a, t);
                if (lca == null || lca.position.y < hipLevel) continue;
                if (PathFrom(pelvis, t).Count < 4) continue;   // too short to be a leg
                b = t;
                return true;
            }
            return false;
        }

        static Transform CommonAncestor(Transform x, Transform y)
        {
            var up = new HashSet<Transform>();
            for (Transform t = x; t != null; t = t.parent) up.Add(t);
            for (Transform t = y; t != null; t = t.parent) if (up.Contains(t)) return t;
            return null;
        }

        static bool IsRelated(Transform x, Transform y)
        {
            for (Transform t = x; t != null; t = t.parent) if (t == y) return true;
            for (Transform t = y; t != null; t = t.parent) if (t == x) return true;
            return false;
        }

        static List<Transform> PathFrom(Transform root, Transform leaf)
        {
            var path = new List<Transform>();
            for (Transform t = leaf; t != null; t = t.parent)
            {
                path.Add(t);
                if (t == root) break;
            }
            path.Reverse();
            return path;
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        /// <summary>
        /// The unit vector across the body from one side's bone to the other's. Normalised so that a wide
        /// pair (the shoulders) and a narrow one (the ankles) each get one vote rather than a vote weighted
        /// by how far apart they happen to be.
        /// </summary>
        static Vector3 Across(Transform left, Transform right)
        {
            Vector3 v = Flat(right.position - left.position);
            return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.zero;
        }

        /// <summary>Forward: square to the body's lateral axis, pointing the way the toes do.</summary>
        static Vector3 Square(Vector3 lateral, Vector3 rough)
        {
            Vector3 f = lateral.sqrMagnitude > 1e-8f
                ? Vector3.Cross(lateral.normalized, Vector3.up)
                : rough;
            if (Vector3.Dot(f, rough) < 0f) f = -f;
            return f.normalized;
        }
    }
}
