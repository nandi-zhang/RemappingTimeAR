using System;
using Meta.XR;
using UnityEngine;

namespace RemappingTime
{
    /// <summary>
    /// Streams both Quest 3 passthrough RGB cameras (via Meta's <see cref="PassthroughCameraAccess"/>)
    /// into a <see cref="FrameRingBuffer"/>, tagging every frame with its capture time and the world
    /// pose of each camera at that instant.
    ///
    /// <para>Left and right images arrive independently and may land on different Unity frames. We
    /// push a stereo pair once both eyes have a fresh image, so the pair is never torn. The pair's
    /// time is the later of the two timestamps.</para>
    ///
    /// <para>If no <see cref="PassthroughCameraAccess"/> components are assigned, two are created at
    /// runtime. The app must declare <c>horizonos.permission.HEADSET_CAMERA</c> in its Android manifest;
    /// the runtime permission is requested on start.</para>
    /// </summary>
    [DefaultExecutionOrder(-50)] // after PassthroughCameraAccess (-100), before the engine (0)
    public sealed class StereoPassthroughCapture : MonoBehaviour
    {
        [Header("Cameras (created automatically if empty)")]
        [SerializeField] private PassthroughCameraAccess leftCamera;
        [SerializeField] private PassthroughCameraAccess rightCamera;
        [SerializeField] private Vector2Int requestedResolution = new Vector2Int(1280, 960);

        [Header("Buffer")]
        [Tooltip("Buffer budget B in seconds: the largest delay that can ever be shown. " +
                 "Memory ≈ B × 60 fps × 2 eyes × width × height × 4 bytes (≈1.2 GB for 2 s at 1280×960).")]
        [SerializeField, Range(0.5f, 6f)] private float bufferSeconds = 2f;
        [Tooltip("Expected maximum camera frame rate, used to size the buffer.")]
        [SerializeField] private int expectedFps = 60;
        [Tooltip("Store frames at a fraction of camera resolution to save memory and heat (1 = full).")]
        [SerializeField, Range(0.25f, 1f)] private float storageScale = 1f;

        [Header("Coordinate frame")]
        [Tooltip("The rig's TrackingSpace. Camera poses from the API are relative to the tracking origin; " +
                 "they are transformed by this so they stay correct if the rig is moved. May be left empty " +
                 "if the rig sits at the world origin.")]
        [SerializeField] private Transform trackingSpace;

        public FrameRingBuffer Buffer { get; } = new FrameRingBuffer();
        public CameraClock Clock { get; } = new CameraClock();

        /// <summary>True once both cameras stream and the buffer holds at least one frame.</summary>
        public bool IsReady => Buffer.IsInitialized && Buffer.Count > 0;
        public float BufferSeconds => bufferSeconds;
        /// <summary>Measured rate at which stereo pairs are buffered (Hz).</summary>
        public float CaptureFps { get; private set; }
        public int PairsCaptured { get; private set; }
        /// <summary>Frames that were overwritten before their partner eye arrived (should stay near 0).</summary>
        public int UnpairedFrames { get; private set; }
        public string Status { get; private set; } = "starting";

        /// <summary>Raised after a stereo pair has been pushed into the buffer.</summary>
        public event Action FrameCaptured;

        private bool _leftFresh, _rightFresh;
        private Pose _leftPose, _rightPose;
        private DateTime _leftTs, _rightTs;
        private double _lastPushRealtime;
        private readonly Vector3[][] _localCorners = new Vector3[2][];

        public PassthroughCameraAccess Access(Eye eye) => eye == Eye.Left ? leftCamera : rightCamera;

        /// <summary>
        /// Corners of the camera image at unit depth, in camera-local space, ordered
        /// bottom-left, bottom-right, top-left, top-right. They include the real intrinsics (principal
        /// point, focal length, sensor crop), so a quad built from them matches the camera frustum
        /// exactly. Null until the cameras are streaming.
        /// </summary>
        public Vector3[] LocalCorners(Eye eye) => _localCorners[(int)eye];

        /// <summary>Image aspect ratio (width / height) of the stored frames.</summary>
        public float Aspect => Buffer.IsInitialized ? (float)Buffer.Width / Buffer.Height
                                                    : (float)requestedResolution.x / requestedResolution.y;

        private void Awake()
        {
            if (leftCamera == null) leftCamera = CreateCamera(PassthroughCameraAccess.CameraPositionType.Left);
            if (rightCamera == null) rightCamera = CreateCamera(PassthroughCameraAccess.CameraPositionType.Right);
        }

        private void Start()
        {
            if (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.PassthroughCameraAccess))
            {
                Status = "waiting for camera permission";
                OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.PassthroughCameraAccess });
            }
        }

        private PassthroughCameraAccess CreateCamera(PassthroughCameraAccess.CameraPositionType position)
        {
            // Configure while inactive: PassthroughCameraAccess reads its settings in OnEnable.
            var go = new GameObject($"PassthroughCamera {position}");
            go.SetActive(false);
            go.transform.SetParent(transform, false);
            var access = go.AddComponent<PassthroughCameraAccess>();
            access.CameraPosition = position;
            access.RequestedResolution = requestedResolution;
            go.SetActive(true);
            return access;
        }

        private void Update()
        {
            if (leftCamera == null || rightCamera == null) return;
            if (!leftCamera.IsPlaying || !rightCamera.IsPlaying)
            {
                if (Buffer.IsInitialized) Status = "camera stream paused";
                return;
            }

            if (!Buffer.IsInitialized) InitBuffer();

            if (leftCamera.IsUpdatedThisFrame)
            {
                if (_leftFresh) UnpairedFrames++;
                _leftFresh = true;
                _leftTs = leftCamera.Timestamp;
                _leftPose = ToWorld(leftCamera.GetCameraPose());
            }
            if (rightCamera.IsUpdatedThisFrame)
            {
                if (_rightFresh) UnpairedFrames++;
                _rightFresh = true;
                _rightTs = rightCamera.Timestamp;
                _rightPose = ToWorld(rightCamera.GetCameraPose());
            }

            if (_leftFresh && _rightFresh)
            {
                double t = Clock.ToSeconds(_leftTs >= _rightTs ? _leftTs : _rightTs);
                Buffer.Push(leftCamera.GetTexture(), rightCamera.GetTexture(), _leftPose, _rightPose, t);
                Clock.NotifyFrame(t);
                _leftFresh = _rightFresh = false;
                PairsCaptured++;

                double now = Time.realtimeSinceStartupAsDouble;
                if (_lastPushRealtime > 0)
                {
                    float inst = (float)(1.0 / Math.Max(1e-3, now - _lastPushRealtime));
                    CaptureFps = CaptureFps <= 0 ? inst : Mathf.Lerp(CaptureFps, inst, 0.05f);
                }
                _lastPushRealtime = now;
                Status = "streaming";
                FrameCaptured?.Invoke();
            }
        }

        private void InitBuffer()
        {
            Texture lt = leftCamera.GetTexture(), rt = rightCamera.GetTexture();
            int capacity = Mathf.CeilToInt(bufferSeconds * Mathf.Max(1, expectedFps)) + 8;
            Buffer.Init(lt, rt, capacity, storageScale);
            Clock.Reset();

            for (int e = 0; e < 2; e++)
            {
                PassthroughCameraAccess access = e == 0 ? leftCamera : rightCamera;
                var identity = new Pose(Vector3.zero, Quaternion.identity);
                Vector2[] viewport = { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
                var corners = new Vector3[4];
                for (int i = 0; i < 4; i++)
                {
                    Vector3 d = access.ViewportPointToRay(viewport[i], identity).direction;
                    corners[i] = d / Mathf.Max(1e-4f, d.z); // normalise to depth z = 1
                }
                _localCorners[e] = corners;
            }

            Debug.Log($"[RemappingTime] Buffer ready: {capacity} stereo frames, {Buffer.Width}×{Buffer.Height}, " +
                      $"{Buffer.BytesAllocated / (1024f * 1024f):F0} MB, budget {bufferSeconds:F1} s.");
        }

        /// <summary>Current (newest-frame) world pose of a camera, used for the non-reprojected "head-locked" mode.</summary>
        public Pose CurrentCameraPose(Eye eye)
        {
            PassthroughCameraAccess access = Access(eye);
            return access != null && access.IsPlaying ? ToWorld(access.GetCameraPose()) : default;
        }

        /// <summary>Projects a world point into a camera image (viewport 0..1) as seen from <paramref name="cameraPose"/>.</summary>
        public Vector2 WorldToViewport(Eye eye, Vector3 worldPoint, Pose cameraPose, out bool inFront)
        {
            Vector3 local = Quaternion.Inverse(cameraPose.rotation) * (worldPoint - cameraPose.position);
            inFront = local.z > 0.02f;
            PassthroughCameraAccess access = Access(eye);
            if (!inFront || access == null || !access.IsPlaying) return new Vector2(0.5f, 0.5f);
            return access.WorldToViewportPoint(worldPoint, cameraPose);
        }

        /// <summary>
        /// True if <paramref name="worldPoint"/> currently falls inside either camera's image, at least
        /// <paramref name="margin"/> (fraction of the image) away from its edges. This is the region where the
        /// remapped canvas can show it.
        /// </summary>
        public bool IsInView(Vector3 worldPoint, float margin)
        {
            for (int e = 0; e < 2; e++)
            {
                var eye = (Eye)e;
                PassthroughCameraAccess access = Access(eye);
                if (access == null || !access.IsPlaying) continue;
                Vector2 v = WorldToViewport(eye, worldPoint, CurrentCameraPose(eye), out bool inFront);
                if (inFront && v.x >= margin && v.x <= 1f - margin && v.y >= margin && v.y <= 1f - margin) return true;
            }
            return false;
        }

        private Pose ToWorld(Pose trackingPose)
        {
            if (trackingSpace == null) return trackingPose;
            return new Pose(trackingSpace.TransformPoint(trackingPose.position), trackingSpace.rotation * trackingPose.rotation);
        }

        private void OnDestroy() => Buffer.Dispose();
    }
}
