using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// Generates the project's look: the graded post-processing profiles the broadcast scenes hang on a
    /// global Volume, the procedural sky material behind them, and the two render pipeline assets tuned
    /// into an honest pair of tiers rather than two near-identical copies.
    ///
    /// Like <see cref="AudioBakery"/>, nothing here is authored by hand — the profiles are built from code
    /// so the grade can be reasoned about, diffed and re-derived, and so the scene builders can call one
    /// method instead of depending on assets somebody remembered to set up. Re-running is idempotent.
    ///
    /// Two profiles are produced because the same grade cannot serve both tiers: the mobile asset renders
    /// at 0.8 scale with no soft shadows, so it gets the colour work (which is nearly free) and none of
    /// the full-screen blur passes (which are not).
    /// </summary>
    public static class LookBakery
    {
        const string SettingsDir = "Assets/Settings";
        const string MaterialsDir = "Assets/Materials";
        public const string PcProfilePath = SettingsDir + "/Broadcast_Volume_PC.asset";
        public const string MobileProfilePath = SettingsDir + "/Broadcast_Volume_Mobile.asset";
        public const string SkyMaterialPath = MaterialsDir + "/Broadcast_Sky.mat";
        const string PcRendererPath = SettingsDir + "/PC_Renderer.asset";
        const string MobileRpPath = SettingsDir + "/Mobile_RPAsset.asset";
        const string PcRpPath = SettingsDir + "/PC_RPAsset.asset";

        [MenuItem("PoDecath/Bake Look (post, sky, tiers)", priority = 7)]
        public static void Bake()
        {
            VolumeProfile pc = BakeProfiles();
            BakeSky();
            TuneRenderPipelineAssets();
            AssetDatabase.SaveAssets();
            Selection.activeObject = pc;
            Debug.Log("[PoDecath] Look baked: PC + mobile volume profiles, procedural sky, tiered RP assets.");
        }

        // ---------------------------------------------------------------- volume profiles

        /// <summary>Bakes both profiles and returns the PC one.</summary>
        public static VolumeProfile BakeProfiles()
        {
            PolicyLibraryTools.EnsureFolder(SettingsDir);
            VolumeProfile pc = Profile(PcProfilePath, full: true);
            Profile(MobileProfilePath, full: false);
            return pc;
        }

        public static VolumeProfile LoadProfile(bool mobile) =>
            AssetDatabase.LoadAssetAtPath<VolumeProfile>(mobile ? MobileProfilePath : PcProfilePath);

        /// <summary>
        /// The grade. It is the same colour treatment a sports broadcast puts on a daylight feed: a filmic
        /// roll-off so the sky over the roof does not clip to a flat white, a touch of warmth to place it in
        /// the afternoon, contrast lifted a little because the deck and the building are both mid-grey, and
        /// just enough vignette that the eye goes to the middle of a portrait frame.
        ///
        /// <paramref name="full"/> adds the passes that cost a full-screen read: depth of field (driven
        /// per-shot by CinematicFocus), camera motion blur, and film grain.
        /// </summary>
        static VolumeProfile Profile(string path, bool full)
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, path);
            }
            // Rebuilt from scratch every bake: an override left behind by an earlier recipe would keep
            // applying, and a profile that is partly generated and partly historical is not reproducible.
            foreach (VolumeComponent c in new List<VolumeComponent>(profile.components))
            {
                profile.components.Remove(c);
                UnityEngine.Object.DestroyImmediate(c, true);
            }
            profile.components.Clear();

            // Neutral, not ACES. ACES has a filmic shoulder but it also skews bright saturated hues, and
            // the brightest thing in every shot here is the sky at the horizon — under ACES it rolled off
            // through green into yellow and the rooftop looked like it was under a sodium lamp. Neutral
            // keeps the same highlight roll-off without moving the hue.
            var tone = profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.Neutral);

            var colour = profile.Add<ColorAdjustments>(true);
            colour.postExposure.Override(0f);
            colour.contrast.Override(10f);
            colour.saturation.Override(4f);

            var wb = profile.Add<WhiteBalance>(true);
            wb.temperature.Override(6f);    // afternoon, not midday
            wb.tint.Override(-2f);

            // Shadows cool, midtones neutral, highlights warm: the cheapest way to make a scene lit by one
            // directional light read as sun plus sky rather than as one lamp.
            var smh = profile.Add<ShadowsMidtonesHighlights>(true);
            smh.shadows.Override(new Vector4(0.94f, 0.97f, 1.06f, 0f));
            smh.midtones.Override(new Vector4(1f, 1f, 1f, 0f));
            smh.highlights.Override(new Vector4(1.04f, 1.01f, 0.95f, 0f));

            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(1.05f);
            bloom.intensity.Override(full ? 0.55f : 0.32f);
            bloom.scatter.Override(0.62f);
            bloom.tint.Override(new Color(1f, 0.97f, 0.92f));
            bloom.highQualityFiltering.Override(full);

            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0.22f);
            vignette.smoothness.Override(0.45f);

            var ca = profile.Add<ChromaticAberration>(true);
            ca.intensity.Override(full ? 0.08f : 0f);   // lens, not artefact: only at the extreme corners

            if (full)
            {
                // Bokeh, not gaussian: the aperture blades are what make a long lens read as a long lens.
                // Focus distance is animated at runtime, so what is authored here is only the lens.
                var dof = profile.Add<DepthOfField>(true);
                dof.mode.Override(DepthOfFieldMode.Bokeh);
                dof.focusDistance.Override(12f);
                dof.focalLength.Override(75f);
                dof.aperture.Override(4.4f);
                dof.bladeCount.Override(6);

                var mb = profile.Add<MotionBlur>(true);
                mb.mode.Override(MotionBlurMode.CameraOnly);
                mb.quality.Override(MotionBlurQuality.Medium);
                mb.intensity.Override(0.18f);   // enough to smear a whip pan, not enough to smear a runner

                var grain = profile.Add<FilmGrain>(true);
                grain.type.Override(FilmGrainLookup.Thin1);
                grain.intensity.Override(0.14f);
                grain.response.Override(0.7f);
            }

            EditorUtility.SetDirty(profile);
            return profile;
        }

        // ---------------------------------------------------------------- sky

        /// <summary>
        /// A procedural sky, so time of day is four numbers rather than six cubemaps. The values here are
        /// the afternoon default; SceneLook drives them per preset at runtime.
        /// </summary>
        public static Material BakeSky()
        {
            PolicyLibraryTools.EnsureFolder(MaterialsDir);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(SkyMaterialPath);
            if (mat == null)
            {
                Shader s = Shader.Find("Skybox/Procedural");
                if (s == null) { Debug.LogWarning("[PoDecath] Skybox/Procedural not found; sky left as-is."); return null; }
                mat = new Material(s);
                AssetDatabase.CreateAsset(mat, SkyMaterialPath);
            }
            mat.SetFloat("_SunSize", 0.03f);
            mat.SetFloat("_SunSizeConvergence", 8f);
            mat.SetFloat("_AtmosphereThickness", 0.85f);
            mat.SetColor("_SkyTint", new Color(0.44f, 0.56f, 0.78f));
            mat.SetColor("_GroundColor", new Color(0.32f, 0.31f, 0.29f));
            mat.SetFloat("_Exposure", 0.95f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // ---------------------------------------------------------------- pipeline tiers

        /// <summary>
        /// Makes the two RP assets actually differ. They shipped as near-copies — both MSAA off, both one
        /// shadow cascade's worth of quality, PC only distinguished by supporting additional-light shadows —
        /// which meant a desktop got a phone's picture for no gain.
        ///
        /// Mobile keeps its 0.8 render scale and single cascade and gains nothing expensive. PC gets 4x
        /// MSAA, soft shadows, four cascades over a longer distance, a bigger shadow map, and the SSAO
        /// renderer feature that plants feet and hurdle legs on the deck.
        /// </summary>
        static void TuneRenderPipelineAssets()
        {
            Tune(MobileRpPath, mobile: true);
            Tune(PcRpPath, mobile: false);
            EnsureSsao(PcRendererPath);
        }

        static void Tune(string path, bool mobile)
        {
            var asset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(path);
            if (asset == null) { Debug.LogWarning($"[PoDecath] {path} missing; tier not tuned."); return; }
            var so = new SerializedObject(asset);

            SetInt(so, "m_MSAA", mobile ? 1 : 4);
            SetFloat(so, "m_RenderScale", mobile ? 0.8f : 1f);
            SetBool(so, "m_SupportsHDR", true);
            SetInt(so, "m_MainLightShadowmapResolution", mobile ? 1024 : 2048);
            SetInt(so, "m_ShadowCascadeCount", mobile ? 1 : 4);
            SetFloat(so, "m_ShadowDistance", mobile ? 55f : 110f);
            SetBool(so, "m_SoftShadowsSupported", !mobile);
            SetBool(so, "m_AdditionalLightShadowsSupported", !mobile);
            SetBool(so, "m_RequireDepthTexture", true);        // depth of field and SSAO both read it
            SetBool(so, "m_RequireOpaqueTexture", !mobile);    // and the wet-deck surface work

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        static void SetInt(SerializedObject so, string name, int value)
        {
            SerializedProperty p = so.FindProperty(name);
            if (p != null) p.intValue = value;
        }

        static void SetFloat(SerializedObject so, string name, float value)
        {
            SerializedProperty p = so.FindProperty(name);
            if (p != null) p.floatValue = value;
        }

        static void SetBool(SerializedObject so, string name, bool value)
        {
            SerializedProperty p = so.FindProperty(name);
            if (p != null) p.boolValue = value;
        }

        /// <summary>
        /// Adds screen-space ambient occlusion to the PC renderer if it is not on it already. The feature's
        /// settings are private, so they go in through the serialised object — which is also why this is
        /// wrapped: a URP version that renames them should cost a warning, not a broken bake.
        /// </summary>
        static void EnsureSsao(string rendererPath)
        {
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (data == null) { Debug.LogWarning($"[PoDecath] {rendererPath} is not a UniversalRendererData; SSAO skipped."); return; }

            ScriptableRendererFeature existing = data.rendererFeatures.Find(f => f is ScreenSpaceAmbientOcclusion);
            if (existing == null)
            {
                var ssao = ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusion>();
                ssao.name = "SSAO";
                data.rendererFeatures.Add(ssao);
                AssetDatabase.AddObjectToAsset(ssao, data);
                EditorUtility.SetDirty(data);
                existing = ssao;
            }

            try
            {
                var so = new SerializedObject(existing);
                SerializedProperty s = so.FindProperty("m_Settings");
                if (s != null)
                {
                    RelFloat(s, "Intensity", 1.1f);
                    RelFloat(s, "Radius", 0.28f);
                    RelFloat(s, "DirectLightingStrength", 0.28f);
                    RelInt(s, "Downsample", 1);
                    RelInt(s, "NormalSamples", 1);   // medium
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PoDecath] SSAO settings not applied ({e.Message}); the feature is still enabled with its defaults.");
            }
            EditorUtility.SetDirty(data);
        }

        static void RelFloat(SerializedProperty parent, string name, float v)
        {
            SerializedProperty p = parent.FindPropertyRelative(name);
            if (p != null && p.propertyType == SerializedPropertyType.Float) p.floatValue = v;
        }

        static void RelInt(SerializedProperty parent, string name, int v)
        {
            SerializedProperty p = parent.FindPropertyRelative(name);
            if (p == null) return;
            if (p.propertyType == SerializedPropertyType.Enum) p.enumValueIndex = v;
            else if (p.propertyType == SerializedPropertyType.Boolean) p.boolValue = v != 0;
            else if (p.propertyType == SerializedPropertyType.Integer) p.intValue = v;
        }
    }
}
