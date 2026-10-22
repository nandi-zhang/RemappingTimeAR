using UnityEngine;

namespace RemappingTime
{
    /// <summary>
    /// Makes each eye see only its own camera image. The left passthrough camera's frames go on the
    /// "LeftEye" layer and the right one's on "RightEye"; this component configures the rig's per-eye
    /// cameras to cull the other eye's layer, and to clear to transparent black so the system passthrough
    /// shows through wherever nothing is drawn.
    ///
    /// Applied for the first few frames because <see cref="OVRCameraRig"/> may (re)create its cameras on startup.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class EyeLayerSetup : MonoBehaviour
    {
        [SerializeField] private OVRCameraRig rig;
        [SerializeField] private string leftEyeLayer = "LeftEye";
        [SerializeField] private string rightEyeLayer = "RightEye";

        private int _framesApplied;

        private void Awake()
        {
            if (rig == null) rig = GetComponent<OVRCameraRig>();
            if (rig != null && !rig.usePerEyeCameras)
            {
                Debug.LogWarning("[RemappingTime] OVRCameraRig.usePerEyeCameras was off; enabling it so each eye " +
                                 "can show its own passthrough camera.");
                rig.usePerEyeCameras = true;
            }
        }

        private void LateUpdate()
        {
            if (_framesApplied > 10 || rig == null) return;
            _framesApplied++;

            int left = LayerMask.NameToLayer(leftEyeLayer), right = LayerMask.NameToLayer(rightEyeLayer);
            if (left < 0 || right < 0)
            {
                Debug.LogError($"[RemappingTime] Layers '{leftEyeLayer}' and '{rightEyeLayer}' must exist (Project Settings → Tags and Layers).");
                _framesApplied = int.MaxValue;
                return;
            }

            Configure(rig.leftEyeCamera, ~(1 << right));
            Configure(rig.rightEyeCamera, ~(1 << left));
            if (rig.centerEyeAnchor != null)
                Configure(rig.centerEyeAnchor.GetComponent<Camera>(), ~((1 << left) | (1 << right)));
        }

        private static void Configure(Camera cam, int mask)
        {
            if (cam == null) return;
            cam.cullingMask &= mask;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
        }
    }
}
