// Writes Build/lod_pose.json: for every athlete whose full model is an FBX and whose phone skin is a glb,
// where each bone of the phone skin has to be, and what its bind matrix has to be, for the phone skeleton
// to be the full model's skeleton and skin exactly as it does. training/tools/glb_set_pose.py then writes
// that into the glb. Run this in the editor (not in play mode) after training/tools/athlete_lods.py has
// made or remade an FBX athlete's phone skin, then the python, then let Unity re-import:
//
//   unity command eval_file --path training/tools/lod_pose_from_unity.cs      (or the bridge's execute-code)
//   python training/tools/glb_set_pose.py Build/lod_pose.json
//
// Why: an AccuRig FBX is bound in the pose its scan was taken in and carries its T-pose as a one-frame
// take. Unity stands the model in the take; Blender exports the phone skin in the scan pose. SkinBinder
// binds a skin in whatever pose its prefab stands in, so the two tiers bound the same athlete in two
// poses, and on the phone Nick Doggy ran with his arms out sideways (2026-10-01). A glb skin already in
// the full model's pose comes through unchanged, so running this twice is harmless.
//
// The returned text says, per athlete, the full model's skinned box, the phone skin's as it is, and the
// phone skin's as it will be; the first and the last must agree.
var sb = new System.Text.StringBuilder();
var outJson = new System.Text.StringBuilder("{");
var defs = new System.Collections.Generic.List<string>();
foreach (string g in UnityEditor.AssetDatabase.FindAssets("t:AthleteDefinition", new[] { "Assets/Athletes" }))
{
  string dp = UnityEditor.AssetDatabase.GUIDToAssetPath(g);
  var d = UnityEditor.AssetDatabase.LoadAssetAtPath<PoDecath.Sim.AthleteDefinition>(dp);
  if (d == null || d.skinOverride == null || d.skinOverrideMobile == null) continue;
  if (UnityEditor.AssetDatabase.GetAssetPath(d.skinOverride).ToLowerInvariant().EndsWith(".fbx")
      && UnityEditor.AssetDatabase.GetAssetPath(d.skinOverrideMobile).ToLowerInvariant().EndsWith(".glb")) defs.Add(dp);
}
var inv = System.Globalization.CultureInfo.InvariantCulture;
bool firstDef = true;
System.Func<UnityEngine.Transform, int> depth = (tr) => { int d = 0; while (tr.parent != null) { d++; tr = tr.parent; } return d; };
// World box of a skinned mesh from the skinning sum itself. BakeMesh is not to be trusted on a renderer that carries a scale.
System.Func<UnityEngine.SkinnedMeshRenderer, UnityEngine.Bounds> box = (s) =>
{
  var m = s.sharedMesh; var v = m.vertices; var w = m.boneWeights; var bp = m.bindposes; var bn = s.bones;
  var mats = new UnityEngine.Matrix4x4[bn.Length];
  for (int k = 0; k < bn.Length; k++) mats[k] = bn[k].localToWorldMatrix * bp[k];
  var bb = new UnityEngine.Bounds(); bool f = true;
  for (int k = 0; k < v.Length; k += 5)
  {
    var bw = w[k];
    UnityEngine.Vector3 p = mats[bw.boneIndex0].MultiplyPoint3x4(v[k]) * bw.weight0 + mats[bw.boneIndex1].MultiplyPoint3x4(v[k]) * bw.weight1
                          + mats[bw.boneIndex2].MultiplyPoint3x4(v[k]) * bw.weight2 + mats[bw.boneIndex3].MultiplyPoint3x4(v[k]) * bw.weight3;
    if (f) { bb = new UnityEngine.Bounds(p, UnityEngine.Vector3.zero); f = false; } else bb.Encapsulate(p);
  }
  return bb;
};
foreach (string path in defs)
{
  var def = UnityEditor.AssetDatabase.LoadAssetAtPath<PoDecath.Sim.AthleteDefinition>(path);
  var F = (UnityEngine.GameObject)UnityEngine.Object.Instantiate(def.skinOverride);
  var L = (UnityEngine.GameObject)UnityEngine.Object.Instantiate(def.skinOverrideMobile);
  var smrF = F.GetComponentInChildren<UnityEngine.SkinnedMeshRenderer>(); var smrL = L.GetComponentInChildren<UnityEngine.SkinnedMeshRenderer>();
  var bF = new System.Collections.Generic.Dictionary<string, int>();
  for (int i = 0; i < smrF.bones.Length; i++) if (smrF.bones[i] != null) bF[smrF.bones[i].name] = i;
  var bpF = smrF.sharedMesh.bindposes; var bpL = smrL.sharedMesh.bindposes;
  var common = new System.Collections.Generic.List<int>();
  for (int i = 0; i < smrL.bones.Length; i++) if (smrL.bones[i] != null && bF.ContainsKey(smrL.bones[i].name)) common.Add(i);

  var boxF = box(smrF); var boxL0 = box(smrL);
  sb.Append("\n" + def.name + ": " + common.Count + " shared bones; raw mesh size full " + smrF.sharedMesh.bounds.size.ToString("F2") + " phone " + smrL.sharedMesh.bounds.size.ToString("F2"));
  sb.Append("\n   full model as Unity stands it: centre " + boxF.center.ToString("F2") + " size " + boxF.size.ToString("F2"));
  sb.Append("\n   phone skin as it is now:       centre " + boxL0.center.ToString("F2") + " size " + boxL0.size.ToString("F2"));

  // The phone mesh in its own bind pose sits somewhere in the world (C_L, the same for every bone); the full
  // model's mesh in *its* bind pose is its own mesh coordinates. X carries phone-mesh coordinates onto
  // full-mesh coordinates. Both meshes are the same surface, so X is fitted from the bones' bind origins.
  // X carries phone-mesh vertex coordinates onto full-mesh vertex coordinates. The two meshes are the same
  // surface (one is the other, decimated), so their boxes must coincide through X; of the 48 signed axis
  // swaps, take the one that makes them.
  var fb = smrF.sharedMesh.bounds; var lb = smrL.sharedMesh.bounds;
  float best = float.MaxValue; UnityEngine.Matrix4x4 bestR = UnityEngine.Matrix4x4.identity;
  int[][] perms = { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };
  foreach (var pm in perms) for (int sg = 0; sg < 8; sg++)
  {
    var R = UnityEngine.Matrix4x4.zero; R[3, 3] = 1f;
    for (int r = 0; r < 3; r++) R[r, pm[r]] = ((sg >> r) & 1) == 0 ? 1f : -1f;
    if (R.determinant < 0f) continue;   // a rotation, not a mirror image
    UnityEngine.Vector3 c = R.MultiplyPoint3x4(lb.center); UnityEngine.Vector3 e = R.MultiplyVector(lb.size); e = new UnityEngine.Vector3(UnityEngine.Mathf.Abs(e.x), UnityEngine.Mathf.Abs(e.y), UnityEngine.Mathf.Abs(e.z));
    float err = (c - fb.center).sqrMagnitude + (e - fb.size).sqrMagnitude;
    if (err < best) { best = err; bestR = R; }
  }
  var X = bestR;
  sb.Append("\n   mesh-to-mesh: box mismatch " + System.Math.Sqrt(best).ToString("G3") + " m, rotation rows " + bestR.GetRow(0).ToString("F0") + bestR.GetRow(1).ToString("F0") + bestR.GetRow(2).ToString("F0"));

  // Put every shared bone exactly where the full model's bone is (parents first), then give it the bind
  // matrix that makes its skinning matrix the full model's: the phone skeleton becomes the full model's
  // skeleton, joint for joint, and the fit and the binding at runtime see the same thing on both tiers.
  common.Sort((x1, x2) => depth(smrL.bones[x1]).CompareTo(depth(smrL.bones[x2])));
  var newBind = new System.Collections.Generic.Dictionary<int, UnityEngine.Matrix4x4>();
  foreach (int i in common)
  {
    var t = smrL.bones[i]; var f = smrF.bones[bF[t.name]];
    t.SetPositionAndRotation(f.position, f.rotation);
  }
  foreach (int i in common)
  {
    var t = smrL.bones[i]; int j = bF[t.name];
    newBind[i] = t.worldToLocalMatrix * smrF.bones[j].localToWorldMatrix * bpF[j] * X;
  }
  // check: skin the phone mesh with the new pose and the new bind matrices
  {
    var m = smrL.sharedMesh; var v = m.vertices; var w = m.boneWeights; var bn = smrL.bones;
    var mats = new UnityEngine.Matrix4x4[bn.Length];
    for (int k = 0; k < bn.Length; k++) mats[k] = bn[k].localToWorldMatrix * (newBind.ContainsKey(k) ? newBind[k] : bpL[k]);
    var bb = new UnityEngine.Bounds(); bool first = true;
    for (int k = 0; k < v.Length; k += 5)
    {
      var bw = w[k];
      UnityEngine.Vector3 q3 = mats[bw.boneIndex0].MultiplyPoint3x4(v[k]) * bw.weight0 + mats[bw.boneIndex1].MultiplyPoint3x4(v[k]) * bw.weight1
                             + mats[bw.boneIndex2].MultiplyPoint3x4(v[k]) * bw.weight2 + mats[bw.boneIndex3].MultiplyPoint3x4(v[k]) * bw.weight3;
      if (first) { bb = new UnityEngine.Bounds(q3, UnityEngine.Vector3.zero); first = false; } else bb.Encapsulate(q3);
    }
    sb.Append("\n   phone skin, re-posed and re-bound: centre " + bb.center.ToString("F2") + " size " + bb.size.ToString("F2"));
    int unshared = 0; float unsharedWeight = 0f;
    for (int k = 0; k < bn.Length; k++) if (!newBind.ContainsKey(k)) unshared++;
    foreach (var bw in w) { if (!newBind.ContainsKey(bw.boneIndex0)) unsharedWeight += bw.weight0; if (!newBind.ContainsKey(bw.boneIndex1)) unsharedWeight += bw.weight1; }
    sb.Append("; bones only the phone skin has: " + unshared + " (carrying weight " + unsharedWeight.ToString("F3") + ")");
  }

  if (!firstDef) outJson.Append(","); firstDef = false;
  outJson.Append("\"" + System.IO.Path.GetFileName(UnityEditor.AssetDatabase.GetAssetPath(def.skinOverrideMobile)) + "\":{");
  bool firstBone = true;
  foreach (int i in common)
  {
    var t = smrL.bones[i]; var p = t.localPosition; var q = t.localRotation; var bm = newBind[i];
    if (!firstBone) outJson.Append(","); firstBone = false;
    outJson.Append("\"" + t.name + "\":{\"p\":[" + p.x.ToString("R", inv) + "," + p.y.ToString("R", inv) + "," + p.z.ToString("R", inv) + "],\"q\":["
      + q.x.ToString("R", inv) + "," + q.y.ToString("R", inv) + "," + q.z.ToString("R", inv) + "," + q.w.ToString("R", inv) + "],\"bind\":[");
    for (int e = 0; e < 16; e++) outJson.Append((e > 0 ? "," : "") + bm[e].ToString("R", inv));   // column-major, as Matrix4x4 indexes
    outJson.Append("]}");
  }
  outJson.Append("}");
  UnityEngine.Object.DestroyImmediate(F); UnityEngine.Object.DestroyImmediate(L);
}
outJson.Append("}");
System.IO.File.WriteAllText(System.IO.Path.Combine(UnityEngine.Application.dataPath, "../Build/lod_pose.json"), outJson.ToString());
return sb.ToString();
