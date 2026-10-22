using Meta.XR;
using Meta.XR.EnvironmentDepth;
using UnityEngine;
using UnityEngine.Android;

namespace RemappingTime
{
    /// <summary>
    /// Checks, live on the device, whether Meta's Depth API delivers depth to this app. The result appears in the
    /// web operator page's Diagnostics card ("depth: …"):
    /// <list type="bullet">
    /// <item><b>supported</b>: the runtime and headset support environment depth;</item>
    /// <item><b>permission</b>: the "spatial data" permission (com.oculus.permission.USE_SCENE) is granted;</item>
    /// <item><b>available</b>: the <see cref="EnvironmentDepthManager"/> is producing depth frames;</item>
    /// <item><b>texture</b>: the depth map published to shaders as <c>_EnvironmentDepthTexture</c> (this global
    /// texture, plus <c>_EnvironmentDepthReprojectionMatrices</c>, is what depth-based reprojection would use);</item>
    /// <item><b>ahead</b>: the measured distance to whatever is straight in front of the head (MRUK raycast).</item>
    /// </list>
    /// It is a prerequisite check for depth-based reprojection; it does not change rendering.
    /// </summary>
    public sealed class DepthProbe : MonoBehaviour
    {
        [SerializeField] private TimeRemapEngine engine;
        [SerializeField] private EnvironmentDepthManager depthManager;
        [SerializeField] private EnvironmentRaycastManager raycaster;
        [SerializeField] private Transform head;

        private static readonly int DepthTextureId = Shader.PropertyToID("_EnvironmentDepthTexture");
        private float _next;

        private void Start()
        {
            if (engine == null) engine = FindFirstObjectByType<TimeRemapEngine>();
            if (head == null && engine != null) head = engine.Head;
            // We only read depth; we don't use Meta's occlusion shaders.
            if (depthManager != null) depthManager.OcclusionShadersMode = OcclusionShadersMode.None;
            if (!Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission))
                OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.Scene });
        }

        /// <summary>Share of captured camera frames that carried a depth map (what depth reprojection uses).</summary>
        private string FramesWithDepth()
        {
            StereoPassthroughCapture c = engine != null ? engine.Capture : null;
            return c == null || c.PairsCaptured == 0 ? "–" : $"{100f * c.FramesWithDepth / c.PairsCaptured:F0}%";
        }

        private void Update()
        {
            if (engine == null || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.3f;

            bool permission = Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission);
            bool available = depthManager != null && depthManager.IsDepthAvailable;
            Texture tex = Shader.GetGlobalTexture(DepthTextureId);
            string texture = tex != null ? $"{tex.width}x{tex.height} {tex.dimension}" : "none";

            string ahead = "–";
            if (available && raycaster != null && head != null)
            {
                if (raycaster.Raycast(new Ray(head.position, head.forward), out EnvironmentRaycastHit hit))
                    ahead = $"{Vector3.Distance(head.position, hit.point):F2} m";
                else
                    ahead = hit.status.ToString();
            }

            engine.SetDiagnostic("depth",
                $"supported {EnvironmentDepthManager.IsSupported} | permission {permission} | available {available} | " +
                $"texture {texture} | ahead {ahead} | frames with depth {FramesWithDepth()}");
        }
    }
}
