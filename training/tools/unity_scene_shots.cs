// Stills of a scene from fixed camera poses, rendered by the game's own pipeline (post-processing included),
// for before/after comparisons. Opens the scene alongside the current one, poses its Main Camera, renders,
// and closes it without saving; the camera pose is never written back.
//
//     unity command eval_file --file training/tools/unity_scene_shots.cs --timeout 300000
//
// Writes Blender/source/shots/<TAG>_<shot>.png. Change TAG to "before" / "after".
const string SCENE = "Assets/Scenes/RooftopRace.unity";
const string TAG = "after";

var poses = new (string name, UnityEngine.Vector3 pos, UnityEngine.Vector3 look, float fov)[]
{
    ("main_camera", new UnityEngine.Vector3(float.NaN, 0, 0), UnityEngine.Vector3.zero, 0),     // as saved
    ("roof_to_city", new UnityEngine.Vector3(10f, 29f, -8f), new UnityEngine.Vector3(-400f, 18f, -620f), 55f),
    ("drone", new UnityEngine.Vector3(260f, 170f, 330f), new UnityEngine.Vector3(-60f, 10f, -150f), 60f),
};
string dir = System.IO.Path.GetFullPath("Blender/source/shots");
System.IO.Directory.CreateDirectory(dir);
var prev = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
var s = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(SCENE, UnityEditor.SceneManagement.OpenSceneMode.Additive);
UnityEngine.SceneManagement.SceneManager.SetActiveScene(s);
UnityEngine.Camera cam = null;
foreach (var go in s.GetRootGameObjects()) if (go.name == "Main Camera") cam = go.GetComponent<UnityEngine.Camera>();
var savedPos = cam.transform.position; var savedRot = cam.transform.rotation; float savedFov = cam.fieldOfView; float savedFar = cam.farClipPlane;
var rt = new UnityEngine.RenderTexture(1280, 720, 24, UnityEngine.RenderTextureFormat.ARGB32);
var tex = new UnityEngine.Texture2D(1280, 720, UnityEngine.TextureFormat.RGB24, false);
var written = new System.Collections.Generic.List<string>();
foreach (var p in poses)
{
    if (!float.IsNaN(p.pos.x))
    {
        cam.transform.position = p.pos;
        cam.transform.rotation = UnityEngine.Quaternion.LookRotation(p.look - p.pos);
        cam.fieldOfView = p.fov;
        cam.farClipPlane = 3000f;
    }
    cam.targetTexture = rt;
    cam.Render();
    UnityEngine.RenderTexture.active = rt;
    tex.ReadPixels(new UnityEngine.Rect(0, 0, 1280, 720), 0, 0);
    tex.Apply();
    UnityEngine.RenderTexture.active = null;
    cam.targetTexture = null;
    string f = System.IO.Path.Combine(dir, $"{TAG}_{p.name}.png");
    System.IO.File.WriteAllBytes(f, tex.EncodeToPNG());
    written.Add(f);
    cam.transform.SetPositionAndRotation(savedPos, savedRot);
    cam.fieldOfView = savedFov; cam.farClipPlane = savedFar;
}
UnityEngine.Object.DestroyImmediate(tex);
rt.Release();
UnityEngine.SceneManagement.SceneManager.SetActiveScene(prev);
UnityEditor.SceneManagement.EditorSceneManager.CloseScene(s, true);
return string.Join("\n", written);
