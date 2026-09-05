using System.Collections.Generic;
using UnityEngine;
using UnityEngine.ProBuilder;

namespace PoDecath.Sim
{
    public enum ArenaType { FlatTrack, SteppedPlatforms, ObstacleLane }

    /// <summary>
    /// Procedural ProBuilder arenas. The lane runs along Unity +X (the creature's forward axis)
    /// so a chase camera behind the creature frames the full course inside a 9:16 portrait frustum.
    /// Works at runtime in players and in the editor.
    /// </summary>
    public class ArenaGenerator : MonoBehaviour
    {
        public ArenaType type = ArenaType.FlatTrack;

        [Header("Dimensions (m)")]
        public float laneLength = 16f;
        public float laneWidth = 4f;
        public float wallHeight = 0.6f;
        public float wallThickness = 0.15f;
        public float startMargin = 3f;

        [Header("Materials")]
        public Material floorMaterial;
        public Material wallMaterial;
        public Material obstacleMaterial;
        public Material targetMaterial;
        public float groundFriction = 1f;

        public Bounds Bounds { get; private set; }
        public Vector3 SpawnPosition { get; private set; }
        public Transform TargetMarker { get; private set; }

        readonly List<Bounds> _platformTops = new List<Bounds>();
        PhysicsMaterial _groundPhysMat;

        public void Generate(ArenaType arenaType)
        {
            type = arenaType;
            Clear();
            EnsureMaterials();

            float xMin = -startMargin;
            float xMax = laneLength - startMargin;
            float zHalf = laneWidth * 0.5f;

            Bounds = new Bounds(new Vector3((xMin + xMax) * 0.5f, 1f, 0f), new Vector3(xMax - xMin, 4f, laneWidth));
            SpawnPosition = Vector3.zero;

            // Floor
            CreateBox("Floor", new Vector3((xMin + xMax) * 0.5f, -0.1f, 0f), new Vector3(xMax - xMin + 2f, 0.2f, laneWidth + 2f), floorMaterial);

            // Boundary walls
            float wy = wallHeight * 0.5f;
            CreateBox("Wall_L", new Vector3((xMin + xMax) * 0.5f, wy, zHalf + wallThickness * 0.5f), new Vector3(xMax - xMin, wallHeight, wallThickness), wallMaterial);
            CreateBox("Wall_R", new Vector3((xMin + xMax) * 0.5f, wy, -zHalf - wallThickness * 0.5f), new Vector3(xMax - xMin, wallHeight, wallThickness), wallMaterial);
            CreateBox("Wall_Front", new Vector3(xMax + wallThickness * 0.5f, wy, 0f), new Vector3(wallThickness, wallHeight, laneWidth + wallThickness * 2f), wallMaterial);
            CreateBox("Wall_Back", new Vector3(xMin - wallThickness * 0.5f, wy, 0f), new Vector3(wallThickness, wallHeight, laneWidth + wallThickness * 2f), wallMaterial);

            switch (type)
            {
                case ArenaType.SteppedPlatforms:
                    BuildSteps();
                    break;
                case ArenaType.ObstacleLane:
                    BuildObstacles();
                    break;
            }

            var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            marker.name = "TargetMarker";
            marker.transform.SetParent(transform, false);
            marker.transform.localScale = new Vector3(0.6f, 0.02f, 0.6f);
            var col = marker.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var mr = marker.GetComponent<MeshRenderer>();
            if (mr != null && targetMaterial != null) mr.sharedMaterial = targetMaterial;
            TargetMarker = marker.transform;
            TargetMarker.position = new Vector3(xMax - 2f, 0.01f, 0f);
        }

        void BuildSteps()
        {
            float x = 2.5f;
            float rise = 0.08f;
            float len = 1.4f;
            int n = 7;
            for (int i = 0; i < n; i++)
            {
                float h = rise * (i + 1);
                Vector3 c = new Vector3(x + len * 0.5f, h * 0.5f, 0f);
                Vector3 s = new Vector3(len, h, laneWidth);
                CreateBox($"Step_{i}", c, s, obstacleMaterial);
                _platformTops.Add(new Bounds(new Vector3(c.x, h, 0f), new Vector3(len, 0.01f, laneWidth)));
                x += len;
            }
            // Plateau to the end of the lane
            float top = rise * n;
            float xEnd = laneLength - startMargin;
            Vector3 pc = new Vector3((x + xEnd) * 0.5f, top * 0.5f, 0f);
            CreateBox("Plateau", pc, new Vector3(xEnd - x, top, laneWidth), obstacleMaterial);
            _platformTops.Add(new Bounds(new Vector3(pc.x, top, 0f), new Vector3(xEnd - x, 0.01f, laneWidth)));
        }

        void BuildObstacles()
        {
            float[] xs = { 2.5f, 4.5f, 6.5f, 8.5f, 10.5f };
            float[] hs = { 0.06f, 0.10f, 0.14f, 0.10f, 0.08f };
            for (int i = 0; i < xs.Length; i++)
            {
                CreateBox($"Bar_{i}", new Vector3(xs[i], hs[i] * 0.5f, 0f), new Vector3(0.35f, hs[i], laneWidth), obstacleMaterial);
            }
            // Staggered pillars to force lateral steering
            float zHalf = laneWidth * 0.5f;
            for (int i = 0; i < 4; i++)
            {
                float x = 3.5f + i * 2f;
                float z = (i % 2 == 0 ? 1f : -1f) * (zHalf * 0.45f);
                CreateBox($"Pillar_{i}", new Vector3(x, 0.3f, z), new Vector3(0.3f, 0.6f, 0.3f), wallMaterial);
            }
        }

        /// <summary>Approximate floor height under a point (accounts for stepped platforms).</summary>
        public float FloorHeightAt(Vector3 p)
        {
            float best = 0f;
            for (int i = 0; i < _platformTops.Count; i++)
            {
                Bounds b = _platformTops[i];
                if (p.x >= b.min.x && p.x <= b.max.x && p.z >= b.min.z && p.z <= b.max.z)
                    best = Mathf.Max(best, b.center.y);
            }
            return best;
        }

        public Vector3 SampleTarget(System.Random rng)
        {
            float xMax = laneLength - startMargin;
            float x = Mathf.Lerp(xMax * 0.55f, xMax - 1.2f, (float)rng.NextDouble());
            float z = ((float)rng.NextDouble() * 2f - 1f) * (laneWidth * 0.5f - 0.8f);
            float y = FloorHeightAt(new Vector3(x, 0f, z));
            Vector3 t = new Vector3(x, y, z);
            if (TargetMarker != null) TargetMarker.position = t + Vector3.up * 0.01f;
            return t;
        }

        public void Clear()
        {
            _platformTops.Clear();
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                GameObject c = transform.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(c); else DestroyImmediate(c);
            }
            TargetMarker = null;
        }

        void EnsureMaterials()
        {
            if (_groundPhysMat == null)
            {
                _groundPhysMat = new PhysicsMaterial("ArenaGround")
                {
                    staticFriction = groundFriction,
                    dynamicFriction = groundFriction,
                    frictionCombine = PhysicsMaterialCombine.Average,
                    bounceCombine = PhysicsMaterialCombine.Minimum,
                    bounciness = 0f,
                };
            }
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (floorMaterial == null) floorMaterial = MakeMat(lit, new Color(0.22f, 0.24f, 0.28f));
            if (wallMaterial == null) wallMaterial = MakeMat(lit, new Color(0.55f, 0.35f, 0.2f));
            if (obstacleMaterial == null) obstacleMaterial = MakeMat(lit, new Color(0.35f, 0.5f, 0.6f));
            if (targetMaterial == null) targetMaterial = MakeMat(lit, new Color(0.2f, 0.9f, 0.4f));
        }

        static Material MakeMat(Shader s, Color c)
        {
            var m = new Material(s != null ? s : Shader.Find("Standard"));
            m.color = c;
            return m;
        }

        GameObject CreateBox(string name, Vector3 center, Vector3 size, Material mat)
        {
            ProBuilderMesh pb = ShapeGenerator.GenerateCube(PivotLocation.Center, size);
            GameObject go = pb.gameObject;
            go.name = name;
            go.transform.SetParent(transform, false);
            go.transform.localPosition = center;
            go.transform.localRotation = Quaternion.identity;
            pb.ToMesh();
            pb.Refresh();

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null && mat != null) mr.sharedMaterial = mat;

            var mf = go.GetComponent<MeshFilter>();
            var mc = go.GetComponent<MeshCollider>();
            if (mc == null) mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mf != null ? mf.sharedMesh : null;
            mc.sharedMaterial = _groundPhysMat;
            go.isStatic = true;
            return go;
        }
    }
}
