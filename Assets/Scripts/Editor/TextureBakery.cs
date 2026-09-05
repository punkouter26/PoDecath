using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// Synthesises the project's surfaces — albedo, normal and a packed metallic/AO/smoothness mask for
    /// every material the rooftop is built from — and writes them into <c>Assets/Textures/</c>.
    ///
    /// Every material in the project shipped as URP Lit with a flat colour and no maps at all, which is
    /// why the deck, the runway and the infield all read as the same plastic under the same light. The set
    /// here is generated the same way <see cref="AudioBakery"/> generates the sound set: fractal value
    /// noise for the broad variation, cellular noise for anything with aggregate or grain in it, a Sobel
    /// pass over the height field for the normal, and cavity from that same height for the ambient
    /// occlusion. All of it is tileable — the lattices wrap — and all of it is deterministic, so a re-bake
    /// produces byte-identical files and does not churn the repository.
    ///
    /// Tiling is in metres and is baked into the mesh UVs by <see cref="WorldUvProjector"/>, not into the
    /// material, because the track is ProBuilder geometry whose curve wedges would otherwise stretch the
    /// asphalt round the bends.
    /// </summary>
    public static class TextureBakery
    {
        const string TexDir = "Assets/Textures";
        const string MaterialsDir = "Assets/Materials";

        /// <summary>One surface: what it looks like, and how big its detail is on the ground.</summary>
        public struct Surface
        {
            public string name;
            /// <summary>Metres across one repeat of the texture. Drives the UV projection, not the material.</summary>
            public float metresPerTile;
            public Color tint;
            public float metallic;
            public float smoothness;
        }

        public static readonly Surface[] Surfaces =
        {
            new Surface { name = "Asphalt",  metresPerTile = 2.4f, tint = new Color(0.20f, 0.20f, 0.22f), metallic = 0f,    smoothness = 0.22f },
            new Surface { name = "Concrete", metresPerTile = 3.0f, tint = new Color(0.68f, 0.68f, 0.66f), metallic = 0f,    smoothness = 0.16f },
            new Surface { name = "Rubber",   metresPerTile = 1.6f, tint = new Color(0.66f, 0.24f, 0.16f), metallic = 0f,    smoothness = 0.30f },
            new Surface { name = "Sand",     metresPerTile = 1.1f, tint = new Color(0.86f, 0.77f, 0.58f), metallic = 0f,    smoothness = 0.08f },
            new Surface { name = "Turf",     metresPerTile = 1.4f, tint = new Color(0.26f, 0.36f, 0.22f), metallic = 0f,    smoothness = 0.12f },
            new Surface { name = "Metal",    metresPerTile = 1.0f, tint = new Color(0.78f, 0.79f, 0.82f), metallic = 0.9f,  smoothness = 0.62f },
            new Surface { name = "Paint",    metresPerTile = 2.0f, tint = new Color(0.94f, 0.94f, 0.93f), metallic = 0f,    smoothness = 0.34f },
        };

        [MenuItem("PoDecath/Bake Surfaces (textures + materials)", priority = 8)]
        public static void Bake()
        {
            BakeAll();
            AssetDatabase.SaveAssets();
            Debug.Log($"[PoDecath] Baked {Surfaces.Length} surfaces into {TexDir} and re-dressed the rooftop materials.");
        }

        /// <summary>
        /// Bakes only if the set is not on disk. The scene builders call this: a full bake is twenty-one
        /// PNG imports and is not worth paying for on every scene rebuild, but a project that has never
        /// baked must not build a scene full of untextured materials either.
        /// </summary>
        public static void EnsureBaked()
        {
            if (AssetDatabase.LoadAssetAtPath<Texture>($"{TexDir}/Asphalt_albedo.png") != null)
            {
                DressMaterials();   // cheap, and it re-asserts the wiring if a material was edited by hand
                return;
            }
            BakeAll();
        }

        public static void BakeAll()
        {
            PolicyLibraryTools.EnsureFolder(TexDir);
            PolicyLibraryTools.EnsureFolder(MaterialsDir);
            foreach (Surface s in Surfaces) BakeSurface(s);
            DressMaterials();
        }

        /// <summary>Metres per texture repeat for a named surface; the UV projector needs the same number.</summary>
        public static float TileSize(string surface)
        {
            foreach (Surface s in Surfaces) if (s.name == surface) return s.metresPerTile;
            return 2f;
        }

        /// <summary>
        /// Metres per texture repeat for whatever material a piece of generated geometry was given. The
        /// builders know the material, not the surface, so this is the bridge: <see cref="WorldUvProjector"/>
        /// has to project at exactly the scale the material was dressed at or the tiling reads wrong.
        /// </summary>
        public static float TileForMaterial(Material m) => m == null ? 2f : m.name switch
        {
            "Track_Asphalt" => TileSize("Asphalt"),
            "Track_Barrier" => TileSize("Concrete"),
            "Track_Line" => TileSize("Paint"),
            "Track_Column" => TileSize("Concrete"),
            "Infield_Deck" => TileSize("Turf"),
            "LongJump_Runway" => TileSize("Rubber"),
            "LongJump_Sand" => TileSize("Sand"),
            "LongJump_Foul" => TileSize("Rubber"),
            "Hurdle_Frame" => TileSize("Metal"),
            "Hurdle_Bar" => TileSize("Metal"),
            _ => 2f,
        };

        // ---------------------------------------------------------------- one surface

        static void BakeSurface(Surface s)
        {
            const int size = 512;
            Fields f = Build(s.name, size);
            float[] height = f.height;
            Color[] albedo = Albedo(s, size, f);
            Color[] mask = Mask(s, size, height);

            WritePng($"{TexDir}/{s.name}_albedo.png", size, albedo, srgb: true, normal: false);
            WritePng($"{TexDir}/{s.name}_normal.png", size, NormalFrom(height, size, NormalStrength(s.name)), srgb: false, normal: true);
            WritePng($"{TexDir}/{s.name}_mask.png", size, mask, srgb: false, normal: false);
        }

        static float NormalStrength(string name) => name switch
        {
            "Asphalt" => 2.6f,
            "Concrete" => 1.4f,
            "Sand" => 2.2f,
            "Turf" => 3.0f,
            "Rubber" => 1.1f,
            "Metal" => 0.5f,
            _ => 0.7f,
        };

        /// <summary>
        /// The three fields every map is derived from: relief, which stone or grain each texel belongs to,
        /// and the slow variation that happens over metres rather than millimetres.
        ///
        /// <see cref="grain"/> is what makes an aggregate surface read as aggregate. Height alone gives
        /// bumps that are all the same colour; a real chipping is a different stone from the one beside it,
        /// so each cell gets its own random value and the albedo tints per cell, not per texel.
        /// </summary>
        struct Fields
        {
            public float[] height;
            public float[] grain;   // 0..1 per cell, constant within a stone
            public float[] patch;   // metres-scale variation: wear, damp, staining
        }

        /// <summary>
        /// Builds the fields for one surface. Each is a different mix of the same two primitives — fractal
        /// value noise for the shape of the material and cellular noise for its grain — because that is
        /// genuinely what tells asphalt from sand: the size of the stones and how sharply they sit above
        /// the binder. The result is histogram-stretched, without which averaging octaves piles every
        /// value up around the middle and the surface comes out looking like vinyl.
        /// </summary>
        static Fields Build(string name, int size)
        {
            var h = new float[size * size];
            var g = new float[size * size];
            var p = new float[size * size];

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                float u = (float)x / size, v = (float)y / size;
                float value, cellId = 0.5f;
                switch (name)
                {
                    case "Asphalt":
                        // Chippings in binder: cells give the stones, fbm the ruts the field wears into it.
                        value = 0.62f * Ridge(Cellular(u, v, 30, 11, out cellId), 1.7f)
                              + 0.24f * Fbm(u, v, 8, 5, 12) + 0.14f * Fbm(u, v, 2, 3, 13);
                        break;
                    case "Concrete":
                        value = 0.62f * Fbm(u, v, 6, 5, 21) + 0.24f * Cellular(u, v, 11, 22, out cellId)
                              + 0.14f * Fbm(u, v, 40, 2, 23);   // the fine pitting of a poured slab
                        break;
                    case "Rubber":
                        // A poured track is nearly flat with a fine granule on top.
                        value = 0.55f * Ridge(Cellular(u, v, 52, 31, out cellId), 1.3f) + 0.45f * Fbm(u, v, 12, 3, 32);
                        break;
                    case "Sand":
                        // Grains, plus the long ripple a rake leaves down the pit.
                        value = 0.44f * Ridge(Cellular(u, v, 72, 41, out cellId), 1.5f) + 0.26f * Fbm(u, v, 16, 4, 42)
                              + 0.30f * (0.5f + 0.5f * Mathf.Sin((v * 16f + Fbm(u, v, 4, 3, 43) * 3.2f) * Mathf.PI * 2f));
                        break;
                    case "Turf":
                        // Blades: cells for the clumps, a high-frequency stretched noise for the nap.
                        value = 0.5f * Ridge(Cellular(u, v, 84, 51, out cellId), 2.2f)
                              + 0.3f * Fbm(u * 6f, v, 48, 3, 52) + 0.2f * Fbm(u, v, 7, 4, 53);
                        break;
                    case "Metal":
                        // Brushed: stretched noise, so the anisotropy runs one way.
                        value = 0.7f * Fbm(u * 30f, v, 32, 3, 61) + 0.3f * Fbm(u, v, 5, 3, 62);
                        cellId = 0.5f;
                        break;
                    default:   // Paint
                        value = 0.55f * Fbm(u, v, 5, 4, 71) + 0.45f * Cellular(u, v, 16, 72, out cellId);
                        break;
                }
                h[i] = value;
                g[i] = cellId;
                p[i] = Fbm(u, v, 3, 4, 101 + name.Length * 37);
            }

            Stretch(h);
            Stretch(p);
            return new Fields { height = h, grain = g, patch = p };
        }

        /// <summary>
        /// Sharpens a cellular field into something with edges. Cell noise straight out of the generator is
        /// a field of soft blobs; a chipping has a lip where it meets the binder, and this is that lip.
        /// </summary>
        static float Ridge(float v, float power) => Mathf.Pow(Mathf.Clamp01(v), power);

        /// <summary>
        /// Rescales a field so its darkest texel is 0 and its brightest is 1. Fractal noise made by summing
        /// octaves is strongly centre-weighted, so without this every surface comes out as a mid-grey with
        /// a faint pattern in it — which is exactly what the first bake looked like.
        /// </summary>
        static void Stretch(float[] f)
        {
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < f.Length; i++) { if (f[i] < min) min = f[i]; if (f[i] > max) max = f[i]; }
            float span = max - min;
            if (span < 1e-5f) return;
            for (int i = 0; i < f.Length; i++) f[i] = (f[i] - min) / span;
        }

        static Color[] Albedo(Surface s, int size, Fields f)
        {
            var c = new Color[size * size];
            float[] h = f.height;
            for (int i = 0; i < c.Length; i++)
            {
                float grain = f.grain[i], patch = f.patch[i];
                Color col;
                switch (s.name)
                {
                    case "Asphalt":
                        // Binder is near-black; the chippings sitting proud of it are grey stones, each a
                        // slightly different grey. Patch darkens the ruts, where the deck stays damp.
                        col = Color.Lerp(new Color(0.10f, 0.10f, 0.115f), new Color(0.46f, 0.45f, 0.47f),
                                         Mathf.SmoothStep(0.35f, 0.9f, h[i]) * Mathf.Lerp(0.55f, 1f, grain));
                        col *= Mathf.Lerp(0.78f, 1.12f, patch);
                        break;
                    case "Turf":
                        col = Color.Lerp(new Color(0.13f, 0.22f, 0.10f), new Color(0.39f, 0.52f, 0.24f),
                                         Mathf.Clamp01(h[i] * 0.75f + grain * 0.35f));
                        col *= Mathf.Lerp(0.82f, 1.14f, patch);   // mown bands and worn lines
                        break;
                    case "Sand":
                        col = Color.Lerp(new Color(0.68f, 0.58f, 0.40f), new Color(0.97f, 0.91f, 0.74f),
                                         Mathf.Clamp01(h[i] * 0.8f + grain * 0.3f));
                        col *= Mathf.Lerp(0.9f, 1.06f, patch);
                        break;
                    case "Concrete":
                        col = Color.Lerp(new Color(0.44f, 0.44f, 0.43f), new Color(0.84f, 0.84f, 0.81f),
                                         Mathf.SmoothStep(0.15f, 0.85f, h[i]));
                        col *= Mathf.Lerp(0.8f, 1.1f, patch);     // staining and rain streaks
                        break;
                    case "Metal":
                        col = Color.Lerp(new Color(0.55f, 0.56f, 0.59f), new Color(0.93f, 0.94f, 0.97f), h[i]);
                        break;
                    case "Rubber":
                        col = Color.Lerp(new Color(0.42f, 0.16f, 0.11f), new Color(0.86f, 0.36f, 0.24f),
                                         Mathf.Clamp01(h[i] * 0.6f + grain * 0.5f));
                        col *= Mathf.Lerp(0.88f, 1.08f, patch);
                        break;
                    default:   // Paint: worn, and the low ground is where the surface underneath shows through
                        col = Color.Lerp(new Color(0.46f, 0.46f, 0.45f), new Color(0.99f, 0.99f, 0.98f),
                                         Mathf.SmoothStep(0.2f, 0.62f, h[i]));
                        col *= Mathf.Lerp(0.86f, 1.05f, patch);
                        break;
                }
                // Each recipe above already ends on the surface's own colour, so the tint on the Surface
                // record is only what the materials multiply by afterwards — it is not applied twice here.
                c[i] = new Color(Mathf.Clamp01(col.r), Mathf.Clamp01(col.g), Mathf.Clamp01(col.b), 1f);
            }
            return c;
        }

        /// <summary>
        /// URP Lit reads metallic from R and smoothness from A of the metallic map, and occlusion from G of
        /// the occlusion map — so one texture serves as both, which is one sampler saved on every surface.
        /// Occlusion is cavity: how far below its neighbourhood a point sits.
        /// </summary>
        static Color[] Mask(Surface s, int size, float[] h)
        {
            var c = new Color[size * size];
            var blur = Blur(h, size, 6);
            for (int i = 0; i < c.Length; i++)
            {
                float cavity = Mathf.Clamp01(0.5f + (h[i] - blur[i]) * 2.4f);
                float ao = Mathf.Lerp(0.55f, 1f, cavity);
                float smooth = s.name switch
                {
                    // Wet-looking where the surface is low (water sits in the ruts) and duller on the tops.
                    "Asphalt" => Mathf.Lerp(s.smoothness * 1.6f, s.smoothness * 0.6f, h[i]),
                    "Metal" => Mathf.Lerp(s.smoothness * 0.7f, s.smoothness * 1.15f, h[i]),
                    "Sand" => s.smoothness * Mathf.Lerp(1.4f, 0.7f, h[i]),
                    _ => Mathf.Lerp(s.smoothness * 1.25f, s.smoothness * 0.75f, h[i]),
                };
                float metal = s.metallic * Mathf.Lerp(0.85f, 1f, h[i]);
                c[i] = new Color(metal, ao, 0f, Mathf.Clamp01(smooth));
            }
            return c;
        }

        // ---------------------------------------------------------------- maps from height

        /// <summary>Sobel over the (wrapping) height field, packed as a tangent-space normal.</summary>
        static Color[] NormalFrom(float[] h, int size, float strength)
        {
            var c = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float l = h[Idx(x - 1, y, size)], r = h[Idx(x + 1, y, size)];
                float d = h[Idx(x, y - 1, size)], u = h[Idx(x, y + 1, size)];
                Vector3 n = new Vector3((l - r) * strength, (d - u) * strength, 1f).normalized;
                c[y * size + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
            }
            return c;
        }

        /// <summary>Separable box blur with wrapping edges; the reference a cavity map is measured against.</summary>
        static float[] Blur(float[] src, int size, int radius)
        {
            var tmp = new float[src.Length];
            var outp = new float[src.Length];
            float inv = 1f / (radius * 2 + 1);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float sum = 0f;
                for (int k = -radius; k <= radius; k++) sum += src[Idx(x + k, y, size)];
                tmp[y * size + x] = sum * inv;
            }
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float sum = 0f;
                for (int k = -radius; k <= radius; k++) sum += tmp[Idx(x, y + k, size)];
                outp[y * size + x] = sum * inv;
            }
            return outp;
        }

        static int Idx(int x, int y, int size)
        {
            x = ((x % size) + size) % size;
            y = ((y % size) + size) % size;
            return y * size + x;
        }

        // ---------------------------------------------------------------- noise

        /// <summary>
        /// Tileable value noise. The lattice wraps at <paramref name="period"/>, so a texture sampled over
        /// the unit square meets itself at the seam — which is the whole reason these are usable as
        /// repeating surfaces at all.
        /// </summary>
        static float Value(float x, float y, int period, int seed)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Hash(x0, y0, period, seed);
            float b = Hash(x0 + 1, y0, period, seed);
            float c = Hash(x0, y0 + 1, period, seed);
            float d = Hash(x0 + 1, y0 + 1, period, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        static float Fbm(float u, float v, int period, int octaves, int seed)
        {
            float sum = 0f, amp = 0.5f, norm = 0f;
            int p = Mathf.Max(1, period);
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * Value(u * p, v * p, p, seed + o * 977);
                norm += amp;
                amp *= 0.5f;
                p *= 2;
            }
            return norm > 0f ? sum / norm : 0f;
        }

        /// <summary>
        /// Tileable cellular (Worley) noise, returned as 1 - distance so the cell centres are the high
        /// ground. This is what gives asphalt its chippings and sand its grains. <paramref name="cellId"/>
        /// comes back as a value that is constant across one cell, which is how a stone gets a colour of
        /// its own rather than a gradient shared with its neighbours.
        /// </summary>
        static float Cellular(float u, float v, int cells, int seed, out float cellId)
        {
            float x = u * cells, y = v * cells;
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float best = 10f;
            cellId = 0.5f;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int cx = xi + dx, cy = yi + dy;
                float px = cx + Hash(cx, cy, cells, seed);
                float py = cy + Hash(cx, cy, cells, seed + 5701);
                float d = (px - x) * (px - x) + (py - y) * (py - y);
                if (d < best) { best = d; cellId = Hash(cx, cy, cells, seed + 9173); }
            }
            return Mathf.Clamp01(1f - Mathf.Sqrt(best));
        }

        /// <summary>Integer hash on a wrapping lattice. Wrapping is what makes every map above tileable.</summary>
        static float Hash(int x, int y, int period, int seed)
        {
            int p = Mathf.Max(1, period);
            x = ((x % p) + p) % p;
            y = ((y % p) + p) % p;
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2246822519);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xffffff) / (float)0x1000000;
        }

        // ---------------------------------------------------------------- writing

        static void WritePng(string path, int size, Color[] pixels, bool srgb, bool normal)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, !srgb);
            tex.SetPixels(pixels);
            tex.Apply();
            File.WriteAllBytes(Path.GetFullPath(path), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;
            importer.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture = srgb;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 4;          // the deck is nearly always seen at a glancing angle
            importer.mipmapEnabled = true;
            importer.streamingMipmaps = true;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.maxTextureSize = 512;
            importer.SaveAndReimport();
        }

        // ---------------------------------------------------------------- materials

        /// <summary>
        /// Points every rooftop material at the surface it should have been wearing all along. Colours are
        /// kept close to the flat ones the scene builders picked so nothing moves in the layout — the
        /// change is that they now have relief, cavity and a believable specular response.
        /// </summary>
        public static void DressMaterials()
        {
            Dress("Track_Asphalt", "Asphalt", new Color(0.9f, 0.9f, 0.95f));
            Dress("Track_Barrier", "Concrete", new Color(0.86f, 0.16f, 0.14f));
            Dress("Track_Line", "Paint", Color.white);
            Dress("Track_Column", "Concrete", new Color(0.86f, 0.86f, 0.84f));
            Dress("Infield_Deck", "Turf", new Color(0.95f, 1f, 0.9f));
            Dress("LongJump_Runway", "Rubber", new Color(1f, 0.86f, 0.8f));
            Dress("LongJump_Sand", "Sand", Color.white);
            Dress("LongJump_Foul", "Rubber", new Color(0.2f, 0.2f, 0.21f));
            Dress("Hurdle_Frame", "Metal", new Color(0.95f, 0.95f, 0.98f));
            Dress("Hurdle_Bar", "Metal", new Color(1f, 0.66f, 0.12f));
            Dress("Arena_Floor", "Concrete", new Color(0.6f, 0.64f, 0.7f));
            Dress("Arena_Wall", "Concrete", new Color(0.8f, 0.55f, 0.35f));
            Dress("Arena_Obstacle", "Concrete", new Color(0.5f, 0.68f, 0.85f));
        }

        static void Dress(string materialName, string surface, Color tint)
        {
            string path = $"{MaterialsDir}/{materialName}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) return;

            Texture albedo = Load(surface, "albedo");
            Texture normal = Load(surface, "normal");
            Texture mask = Load(surface, "mask");
            if (albedo == null) return;

            Surface s = Find(surface);
            m.SetTexture("_BaseMap", albedo);
            m.SetColor("_BaseColor", tint);
            if (normal != null)
            {
                m.SetTexture("_BumpMap", normal);
                m.SetFloat("_BumpScale", 1f);
                m.EnableKeyword("_NORMALMAP");
            }
            if (mask != null)
            {
                // Same texture in both slots: R and A carry metallic and smoothness, G carries occlusion.
                m.SetTexture("_MetallicGlossMap", mask);
                m.SetTexture("_OcclusionMap", mask);
                m.SetFloat("_OcclusionStrength", 1f);
                m.EnableKeyword("_METALLICSPECGLOSSMAP");
                m.EnableKeyword("_OCCLUSIONMAP");
            }
            m.SetFloat("_Metallic", s.metallic);
            m.SetFloat("_Smoothness", s.smoothness);
            m.SetFloat("_EnvironmentReflections", 1f);
            m.SetFloat("_SpecularHighlights", 1f);
            // UVs are projected in world metres by the builders, so the material must not tile on top.
            m.SetTextureScale("_BaseMap", Vector2.one);
            m.SetTextureOffset("_BaseMap", Vector2.zero);
            EditorUtility.SetDirty(m);
        }

        static Surface Find(string name)
        {
            foreach (Surface s in Surfaces) if (s.name == name) return s;
            return Surfaces[0];
        }

        static Texture Load(string surface, string map) =>
            AssetDatabase.LoadAssetAtPath<Texture>($"{TexDir}/{surface}_{map}.png");
    }
}
