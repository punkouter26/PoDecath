// Unity half of "rebuild a Unity scene in Blender". Run in the open editor, no recompile needed:
//
//     unity command eval_file --file training/tools/unity_scene_export.cs --timeout 600000
//
// Opens SCENE additively (the active scene is left alone, nothing is saved), and writes with glTFast:
//   Blender/source/<Scene>_generated.glb  - everything the scene builders generate (track, skyline, treeline),
//                                           plus the sun and the main camera
//   Blender/source/<Scene>_scene.json     - what the glb cannot carry: which model files are placed where
//                                           (the White House is re-imported from its own glb, full quality,
//                                           and its LOD1/LOD2 copies are skipped), sky, ambient and fog.
// training/tools/unity_scene_to_blend.py then assembles the .blend from those two files.
const string SCENE = "Assets/Scenes/RooftopRace.unity";

string root = System.IO.Path.GetFullPath("Blender/source");
System.IO.Directory.CreateDirectory(root);
var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(SCENE);
bool opened = false;
if (!scene.isLoaded)
{
    scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(SCENE, UnityEditor.SceneManagement.OpenSceneMode.Additive);
    opened = true;
}
var log = new System.Text.StringBuilder();

// Every instance of a model file (the White House, the Blender-made city) is placed by reference at its own
// world transform, wherever it sits in the hierarchy; its LOD1/LOD2 copies go with it. Those instances are
// switched off for the glTFast export, so the geometry export carries only what the builders generate (the
// track, the treeline) and nothing twice. The scene is closed without saving, so nothing is left switched off.
var generated = new System.Collections.Generic.List<UnityEngine.GameObject>();
var placed = new System.Collections.Generic.List<string>();
var hidden = new System.Collections.Generic.List<UnityEngine.GameObject>();
foreach (var go in scene.GetRootGameObjects())
{
    if (!go.activeInHierarchy) continue;
    foreach (var t in go.GetComponentsInChildren<UnityEngine.Transform>(false))
    {
        if (!UnityEditor.PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) continue;
        string ap = UnityEditor.AssetDatabase.GetAssetPath(UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject));
        if (!(ap.EndsWith(".glb") || ap.EndsWith(".fbx")) || ap.Contains("_LOD")) continue;
        placed.Add($"{{\"name\":\"{t.name}\",\"model\":\"{ap}\",\"position\":[{t.position.x},{t.position.y},{t.position.z}],"
                   + $"\"rotation\":[{t.rotation.x},{t.rotation.y},{t.rotation.z},{t.rotation.w}],\"scale\":[{t.lossyScale.x},{t.lossyScale.y},{t.lossyScale.z}]}}");
        log.AppendLine($"placed by reference: {t.name} -> {ap}");
        hidden.Add(t.gameObject);
    }
}
foreach (var h in hidden) h.SetActive(false);
foreach (var go in scene.GetRootGameObjects())
{
    if (!go.activeInHierarchy) continue;
    int rends = go.GetComponentsInChildren<UnityEngine.Renderer>(false).Length;
    bool camOrSun = (go.GetComponent<UnityEngine.Camera>() || go.GetComponent<UnityEngine.Light>()) && (go.name == "Sun" || go.name == "Main Camera");
    if (rends > 0 || camOrSun)
    {
        generated.Add(go);
        log.AppendLine($"exported as geometry: {go.name} ({rends} renderers)");
    }
}

// Sky, ambient and fog are per-scene RenderSettings, read with this scene active.
var prev = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);
var sky = UnityEngine.RenderSettings.skybox;
string skyTex = "", skyShader = "";
float skyExposure = 1, skyRotation = 0;
if (sky)
{
    skyShader = sky.shader.name;
    var tex = sky.HasProperty("_MainTex") ? sky.GetTexture("_MainTex") : null;
    if (tex) skyTex = UnityEditor.AssetDatabase.GetAssetPath(tex);
    if (sky.HasProperty("_Exposure")) skyExposure = sky.GetFloat("_Exposure");
    if (sky.HasProperty("_Rotation")) skyRotation = sky.GetFloat("_Rotation");
}
var fc = UnityEngine.RenderSettings.fogColor;
string env = $"{{\"skyShader\":\"{skyShader}\",\"skyTexture\":\"{skyTex}\",\"skyExposure\":{skyExposure},\"skyRotation\":{skyRotation},"
           + $"\"ambientIntensity\":{UnityEngine.RenderSettings.ambientIntensity},\"fog\":{(UnityEngine.RenderSettings.fog ? "true" : "false")},"
           + $"\"fogMode\":\"{UnityEngine.RenderSettings.fogMode}\",\"fogColor\":[{fc.r},{fc.g},{fc.b}],\"fogStart\":{UnityEngine.RenderSettings.fogStartDistance},"
           + $"\"fogEnd\":{UnityEngine.RenderSettings.fogEndDistance},\"fogDensity\":{UnityEngine.RenderSettings.fogDensity}}}";
UnityEngine.SceneManagement.SceneManager.SetActiveScene(prev);

// Colour grading from the scene's global Volume (the PC profile it is saved with).
string grading = "null";
foreach (var go in scene.GetRootGameObjects())
{
    var vol = go.GetComponent<UnityEngine.Rendering.Volume>();
    if (vol == null || !vol.isGlobal || vol.sharedProfile == null) continue;
    var p = vol.sharedProfile;
    string tm = "None"; float contrast = 0, saturation = 0, exposure = 0, bloom = 0, vignette = 0;
    if (p.TryGet<UnityEngine.Rendering.Universal.Tonemapping>(out var t) && t.active) tm = t.mode.value.ToString();
    if (p.TryGet<UnityEngine.Rendering.Universal.ColorAdjustments>(out var ca) && ca.active)
    { contrast = ca.contrast.value; saturation = ca.saturation.value; exposure = ca.postExposure.value; }
    if (p.TryGet<UnityEngine.Rendering.Universal.Bloom>(out var bl) && bl.active) bloom = bl.intensity.value;
    if (p.TryGet<UnityEngine.Rendering.Universal.Vignette>(out var vg) && vg.active) vignette = vg.intensity.value;
    grading = $"{{\"profile\":\"{p.name}\",\"tonemapping\":\"{tm}\",\"postExposure\":{exposure},\"contrast\":{contrast},\"saturation\":{saturation},\"bloom\":{bloom},\"vignette\":{vignette}}}";
    log.AppendLine("grading: " + grading);
}

var sun = System.Linq.Enumerable.FirstOrDefault(generated, g => g.GetComponent<UnityEngine.Light>());
string sunJson = sun ? $"{{\"intensity\":{sun.GetComponent<UnityEngine.Light>().intensity},\"color\":[{sun.GetComponent<UnityEngine.Light>().color.r},{sun.GetComponent<UnityEngine.Light>().color.g},{sun.GetComponent<UnityEngine.Light>().color.b}]}}" : "null";

string name = scene.name;
string glbPath = System.IO.Path.Combine(root, name + "_generated.glb");
var exportSettings = new GLTFast.Export.ExportSettings
{
    Format = GLTFast.Export.GltfFormat.Binary,
    FileConflictResolution = GLTFast.Export.FileConflictResolution.Overwrite,
    ComponentMask = GLTFast.ComponentType.All,
};
var goSettings = new GLTFast.Export.GameObjectExportSettings { OnlyActiveInHierarchy = true, DisabledComponents = false };
var export = new GLTFast.Export.GameObjectExport(exportSettings, goSettings, deferAgent: new GLTFast.UninterruptedDeferAgent());
export.AddScene(generated.ToArray(), name);
// eval cannot await. With the uninterrupted defer agent the export normally finishes inside this call; if it
// has not, the scene is left open so the export can finish, and a second run of this file picks up the result.
var task = export.SaveToFileAndDispose(glbPath);
log.AppendLine(task.IsCompleted ? $"glTFast export ok={task.Result} -> {glbPath}" : $"glTFast export still running -> {glbPath}");
bool openedHere = opened;
if (!task.IsCompleted) opened = false;

System.IO.File.WriteAllText(System.IO.Path.Combine(root, name + "_scene.json"),
    $"{{\"scene\":\"{SCENE}\",\"placed\":[{string.Join(",", placed)}],\"environment\":{env},\"sun\":{sunJson},\"grading\":{grading}}}");

if (task.IsCompleted && !openedHere) foreach (var h in hidden) h.SetActive(true);    // a scene that was already open is put back
if (!task.IsCompleted && !openedHere && hidden.Count > 0) log.AppendLine("NOTE: the scene was already open; re-enable the placed models after the export finishes, or reopen the scene without saving");
if (opened) UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
else if (openedHere) log.AppendLine("the scene stays open until the export finishes; close it without saving (nothing in it was saved)");
return log.ToString();
