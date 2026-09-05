using System.IO;
using UnityEditor;
using UnityEngine;
using PoDecath.Fx;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// Generates the sprites the effects layer draws with and the materials that use them, then points a
    /// <see cref="VfxBank"/> at the lot.
    ///
    /// Four sprites cover everything: a soft radial dot (dust, sand grains, the blob shadow), a lumpier
    /// version of it with noise eaten out of the edge (smoke), a tapered streak (sparks and trails) and a
    /// ring-and-grain stamp for the mark a jumper leaves in the pit. They are tiny — 128 px, alpha only,
    /// white — because a particle sprite's job is a shape, and the colour is the particle system's.
    /// </summary>
    public static class VfxBakery
    {
        const string TexDir = "Assets/Textures";
        const string MaterialsDir = "Assets/Materials";
        const string BankPath = "Assets/Materials/VfxBank.asset";
        const int Size = 128;

        [MenuItem("PoDecath/Bake Effects (sprites + materials)", priority = 9)]
        public static void Bake()
        {
            VfxBank bank = BakeBank();
            AssetDatabase.SaveAssets();
            Selection.activeObject = bank;
            Debug.Log("[PoDecath] Effects baked: soft, smoke, streak and mark sprites, eight materials, VfxBank.");
        }

        /// <summary>
        /// The bank, freshly resolved. Loaded through here for the same reason <see cref="AudioBakery"/>
        /// does: opening a scene unloads unused assets, and a wrapper held across that serialises correctly
        /// while comparing equal to null.
        /// </summary>
        public static VfxBank LoadBank() => AssetDatabase.LoadAssetAtPath<VfxBank>(BankPath);

        /// <summary>Bakes only if the bank is missing; the scene builders call this on every build.</summary>
        public static VfxBank EnsureBaked()
        {
            VfxBank bank = LoadBank();
            return bank != null ? bank : BakeBank();
        }

        public static VfxBank BakeBank()
        {
            PolicyLibraryTools.EnsureFolder(TexDir);
            PolicyLibraryTools.EnsureFolder(MaterialsDir);

            Texture2D soft = Sprite("Fx_soft", Soft());
            Texture2D smoke = Sprite("Fx_smoke", Smoke());
            Texture2D streak = Sprite("Fx_streak", Streak());
            Texture2D mark = Sprite("Fx_mark", Mark());

            var bank = LoadBank();
            if (bank == null)
            {
                bank = ScriptableObject.CreateInstance<VfxBank>();
                AssetDatabase.CreateAsset(bank, BankPath);
            }

            bank.dust = Particle("Fx_Dust", soft, additive: false, softParticles: true);
            bank.sand = Particle("Fx_Sand", soft, additive: false, softParticles: false);
            bank.smoke = Particle("Fx_Smoke", smoke, additive: false, softParticles: true);
            bank.spark = Particle("Fx_Spark", streak, additive: true, softParticles: false);
            bank.confetti = Particle("Fx_Confetti", null, additive: false, softParticles: false);
            // Alpha-blended and soft-edged, not additive: an additive streak over a bright sky reads as a
            // solid bar of colour, which is what the first pass looked like.
            bank.trail = Particle("Fx_Trail", soft, additive: false, softParticles: false);
            bank.blobShadow = Multiply("Fx_BlobShadow", soft);
            bank.sandMark = Multiply("Fx_SandMark", mark);

            EditorUtility.SetDirty(bank);
            AssetDatabase.SaveAssets();
            return LoadBank();
        }

        // ---------------------------------------------------------------- sprites

        /// <summary>A radial falloff. The workhorse: dust, sand, and the disc under an athlete.</summary>
        static float[] Soft()
        {
            var a = new float[Size * Size];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float d = Radius(x, y);
                // Squared falloff, not linear: a linear disc has a visible circular edge when a hundred of
                // them overlap, and overlapping is the normal case for dust.
                a[y * Size + x] = Mathf.Clamp01(1f - d);
                a[y * Size + x] *= a[y * Size + x];
            }
            return a;
        }

        /// <summary>The same disc with fractal noise eaten out of it, so a puff has an edge with shape.</summary>
        static float[] Smoke()
        {
            var a = new float[Size * Size];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float d = Radius(x, y);
                float n = Noise(x * 0.06f, y * 0.06f, 2001) * 0.55f + Noise(x * 0.14f, y * 0.14f, 2002) * 0.45f;
                float edge = Mathf.Clamp01(1f - d * Mathf.Lerp(0.75f, 1.5f, n));
                a[y * Size + x] = edge * edge;
            }
            return a;
        }

        /// <summary>A streak: full width at one end, tapered to nothing at the other.</summary>
        static float[] Streak()
        {
            var a = new float[Size * Size];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float u = (float)x / (Size - 1);
                float v = Mathf.Abs((float)y / (Size - 1) - 0.5f) * 2f;
                float width = Mathf.Lerp(1f, 0.05f, u);
                float across = Mathf.Clamp01(1f - v / Mathf.Max(0.02f, width));
                a[y * Size + x] = across * across * Mathf.Lerp(1f, 0.15f, u);
            }
            return a;
        }

        /// <summary>
        /// The mark a landing leaves: a disturbed patch with a rim of thrown sand round it. Drawn as a
        /// multiply decal, so the darker it is the deeper the sand reads.
        /// </summary>
        static float[] Mark()
        {
            var a = new float[Size * Size];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float d = Radius(x, y);
                float n = Noise(x * 0.09f, y * 0.09f, 3003);
                float pit = Mathf.Clamp01(1f - d * Mathf.Lerp(1.1f, 1.6f, n));    // the hollow
                float rim = Mathf.Clamp01(1f - Mathf.Abs(d * Mathf.Lerp(1f, 1.3f, n) - 0.72f) * 7f) * 0.45f;
                a[y * Size + x] = Mathf.Clamp01(pit * 0.8f + rim);
            }
            return a;
        }

        static float Radius(int x, int y)
        {
            float dx = (x + 0.5f) / Size * 2f - 1f;
            float dy = (y + 0.5f) / Size * 2f - 1f;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Small smooth value noise; these sprites are 128 px and do not need the surface machinery.</summary>
        static float Noise(float x, float y, int seed)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = H(x0, y0, seed), b = H(x0 + 1, y0, seed), c = H(x0, y0 + 1, seed), d = H(x0 + 1, y0 + 1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        static float H(int x, int y, int seed)
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2246822519);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xffffff) / (float)0x1000000;
        }

        static Texture2D Sprite(string name, float[] alpha)
        {
            string path = $"{TexDir}/{name}.png";
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false, true);
            var px = new Color[alpha.Length];
            for (int i = 0; i < px.Length; i++) px[i] = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha[i]));
            tex.SetPixels(px);
            tex.Apply();
            File.WriteAllBytes(Path.GetFullPath(path), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = true;
                importer.sRGBTexture = true;
                importer.wrapMode = TextureWrapMode.Clamp;   // a clamped sprite has no seam to tile
                importer.mipmapEnabled = true;
                importer.maxTextureSize = 128;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // ---------------------------------------------------------------- materials

        /// <summary>
        /// A particle material on URP's unlit particle shader. Alpha blended by default; additive for the
        /// things that are light rather than matter, which is sparks and the athlete trails.
        /// </summary>
        static Material Particle(string name, Texture2D tex, bool additive, bool softParticles)
        {
            string path = $"{MaterialsDir}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (m == null)
            {
                if (shader == null) { Debug.LogWarning($"[PoDecath] no particle shader found for {name}."); return null; }
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            if (shader != null && m.shader != shader) m.shader = shader;

            // Surface 1 is transparent; blend 0 is alpha, 1 is premultiply, 2 is additive.
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", additive ? 2f : 0f);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", 0f);           // a particle quad is seen from both sides
            m.SetFloat("_AlphaClip", 0f);
            m.SetFloat("_SoftParticlesEnabled", softParticles ? 1f : 0f);
            // _SoftParticleFadeParams is a Vector4 on this shader, not a float; the keyword below is what
            // actually turns the fade on, so it is left for the shader to fill in.
            m.SetColor("_BaseColor", Color.white);
            if (tex != null) m.SetTexture("_BaseMap", tex);

            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            if (additive) m.EnableKeyword("_ALPHAMODULATE_ON"); else m.DisableKeyword("_ALPHAMODULATE_ON");
            if (softParticles) m.EnableKeyword("_SOFTPARTICLES_ON"); else m.DisableKeyword("_SOFTPARTICLES_ON");
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)(additive ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>
        /// A multiplying decal: it darkens what is behind it rather than adding to it, which is what a
        /// shadow and a footprint in sand both do. Unlit, so it is not itself lit by the sun it implies.
        /// </summary>
        static Material Multiply(string name, Texture2D tex)
        {
            string path = $"{MaterialsDir}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (m == null)
            {
                if (shader == null) { Debug.LogWarning($"[PoDecath] no unlit shader found for {name}."); return null; }
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            if (shader != null && m.shader != shader) m.shader = shader;

            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", 0f);
            m.SetColor("_BaseColor", new Color(0f, 0f, 0f, 0.5f));
            if (tex != null) m.SetTexture("_BaseMap", tex);
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent - 50;   // under the particles
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
