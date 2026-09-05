using UnityEngine;
using Unity.Cinemachine;

namespace PoDecath.Cam
{
    /// <summary>
    /// Two Cinemachine cameras framed for portrait: a chase view behind the creature and a side view.
    /// Toggle() swaps priorities; the CinemachineBrain on the main camera blends between them.
    /// </summary>
    public class CameraRig : MonoBehaviour
    {
        public CinemachineCamera chaseCamera;
        public CinemachineCamera sideCamera;
        public int activeIndex = 0;

        public string ActiveName => activeIndex == 0 ? "Chase" : "Side";

        void Start() => Apply();

        public void SetTarget(Transform target)
        {
            if (chaseCamera != null) { chaseCamera.Follow = target; chaseCamera.LookAt = target; }
            if (sideCamera != null) { sideCamera.Follow = target; sideCamera.LookAt = target; }
        }

        public void Toggle()
        {
            activeIndex = (activeIndex + 1) % 2;
            Apply();
        }

        void Apply()
        {
            if (chaseCamera != null) chaseCamera.Priority = activeIndex == 0 ? 20 : 10;
            if (sideCamera != null) sideCamera.Priority = activeIndex == 1 ? 20 : 10;
        }
    }
}
