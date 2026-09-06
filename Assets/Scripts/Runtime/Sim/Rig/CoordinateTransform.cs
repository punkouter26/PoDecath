using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Strict transform layer between the external right-handed Z-up frame
    /// (Isaac Lab / MuJoCo) and the Unity left-handed Y-up frame.
    ///
    ///   Position / vector : (x, y, z)        -> (x, z, y)
    ///   Quaternion        : (qx, qy, qz, qw) -> (-qx, -qz, -qy, qw)
    ///
    /// Both mappings are involutions, so the same function converts in either direction.
    /// External +X (forward) maps to Unity +X, external +Y (left) maps to Unity +Z,
    /// external +Z (up) maps to Unity +Y.
    ///
    /// Because the mapping is a reflection, a positive rotation of angle t about an
    /// external axis becomes a rotation of -t about the mapped axis in Unity. Joint
    /// angle signs are therefore handled explicitly through JointSpec.sign.
    /// </summary>
    public static class CoordinateTransform
    {
        public static Vector3 ExternalToUnity(Vector3 v) => new Vector3(v.x, v.z, v.y);
        public static Vector3 UnityToExternal(Vector3 v) => new Vector3(v.x, v.z, v.y);

        public static Quaternion ExternalToUnity(Quaternion q) => new Quaternion(-q.x, -q.z, -q.y, q.w);
        public static Quaternion UnityToExternal(Quaternion q) => new Quaternion(-q.x, -q.z, -q.y, q.w);

        /// <summary>Writes a Unity-frame vector into a flat buffer using external (x, y, z) ordering.</summary>
        public static void WriteExternal(float[] dst, int offset, Vector3 unityVector)
        {
            dst[offset + 0] = unityVector.x;
            dst[offset + 1] = unityVector.z;
            dst[offset + 2] = unityVector.y;
        }
    }
}
