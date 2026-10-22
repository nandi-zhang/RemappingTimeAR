using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace RemappingTime
{
    /// <summary>One buffered stereo capture: both camera images plus the pose each camera had when it captured.</summary>
    public sealed class StereoFrame
    {
        public RenderTexture Left;
        public RenderTexture Right;

        /// <summary>World pose of the left/right camera at capture time. Storing these is what enables
        /// head reprojection: a past frame can be drawn where it was seen, not where the head is now.</summary>
        public Pose LeftPose;
        public Pose RightPose;

        /// <summary>Capture time in <see cref="CameraClock"/> seconds.</summary>
        public double Time;

        public RenderTexture Texture(Eye eye) => eye == Eye.Left ? Left : Right;
        public Pose CameraPose(Eye eye) => eye == Eye.Left ? LeftPose : RightPose;
    }

    public enum Eye { Left = 0, Right = 1 }

    /// <summary>
    /// The result of looking up a source time s in the buffer: the two frames that bracket it and how far
    /// s lies between them (0 = <see cref="Older"/>, 1 = <see cref="Newer"/>). Renderers can cross-fade
    /// the two for smooth slow motion, or use <see cref="Nearest"/> for the paper's nearest-frame rule (Eq. 1).
    /// </summary>
    public struct FrameSample
    {
        public StereoFrame Older;
        public StereoFrame Newer;
        public float Blend;

        public StereoFrame Nearest => Blend < 0.5f ? Older : Newer;

        /// <summary>Capture time actually represented by this sample.</summary>
        public double Time => Older.Time + (Newer.Time - Older.Time) * Blend;

        /// <summary>Camera pose interpolated between the two frames.</summary>
        public Pose InterpolatedPose(Eye eye)
        {
            Pose a = Older.CameraPose(eye), b = Newer.CameraPose(eye);
            return new Pose(Vector3.Lerp(a.position, b.position, Blend), Quaternion.Slerp(a.rotation, b.rotation, Blend));
        }
    }

    /// <summary>
    /// Fixed-capacity circular buffer of the most recent stereo camera frames (the {(I_k, τ_k)} of paper Sec. 3.1),
    /// extended with per-frame camera poses.
    ///
    /// All GPU memory is allocated once in <see cref="Init"/>; pushing a frame is a GPU-side texture copy.
    /// Memory cost is roughly capacity × 2 eyes × width × height × 4 bytes, about 1.2 GB for 2 s at
    /// 1280×960 @ 60 Hz. Use <c>resolutionScale</c> &lt; 1 to trade sharpness for memory and heat.
    /// </summary>
    public sealed class FrameRingBuffer : IDisposable
    {
        private StereoFrame[] _frames;
        private int _head;   // index of the next slot to write
        private int _count;
        private bool _useCopyTexture;

        public int Capacity => _frames?.Length ?? 0;
        public int Count => _count;
        public bool IsInitialized => _frames != null;
        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>Approximate GPU memory held by the buffer, in bytes.</summary>
        public long BytesAllocated => (long)Capacity * 2 * Width * Height * 4;

        public double OldestTime => _count == 0 ? 0 : At(0).Time;
        public double NewestTime => _count == 0 ? 0 : At(_count - 1).Time;

        /// <summary>Allocates <paramref name="capacity"/> stereo frames matching the source textures.</summary>
        public void Init(Texture leftSource, Texture rightSource, int capacity, float resolutionScale = 1f)
        {
            Dispose();

            resolutionScale = Mathf.Clamp(resolutionScale, 0.25f, 1f);
            Width = Mathf.Max(16, Mathf.RoundToInt(leftSource.width * resolutionScale));
            Height = Mathf.Max(16, Mathf.RoundToInt(leftSource.height * resolutionScale));

            // Match the source format exactly so we can use a raw GPU copy (fast, no shader pass).
            // Fall back to a Blit when sizes differ (downscaled storage) or copies are unsupported.
            GraphicsFormat format = leftSource.graphicsFormat;
            if (format == GraphicsFormat.None) format = GraphicsFormat.R8G8B8A8_SRGB;

            _useCopyTexture = Mathf.Approximately(resolutionScale, 1f)
                              && leftSource.width == rightSource.width && leftSource.height == rightSource.height
                              && leftSource.graphicsFormat == rightSource.graphicsFormat
                              && SystemInfo.copyTextureSupport != UnityEngine.Rendering.CopyTextureSupport.None;

            _frames = new StereoFrame[Mathf.Max(4, capacity)];
            for (int i = 0; i < _frames.Length; i++)
            {
                _frames[i] = new StereoFrame
                {
                    Left = CreateTexture(Width, Height, format, $"RemapBuffer L{i}"),
                    Right = CreateTexture(Width, Height, format, $"RemapBuffer R{i}"),
                };
            }
            _head = 0;
            _count = 0;
        }

        /// <summary>
        /// Copies the current camera images into the buffer. Frames must arrive in increasing time order;
        /// a frame that is not newer than the newest buffered frame is ignored.
        /// </summary>
        public void Push(Texture left, Texture right, Pose leftPose, Pose rightPose, double time)
        {
            if (_frames == null) return;
            if (_count > 0 && time <= NewestTime) return;

            StereoFrame slot = _frames[_head];
            if (_useCopyTexture)
            {
                Graphics.CopyTexture(left, slot.Left);
                Graphics.CopyTexture(right, slot.Right);
            }
            else
            {
                Graphics.Blit(left, slot.Left);
                Graphics.Blit(right, slot.Right);
            }
            slot.LeftPose = leftPose;
            slot.RightPose = rightPose;
            slot.Time = time;

            _head = (_head + 1) % _frames.Length;
            _count = Mathf.Min(_count + 1, _frames.Length);
        }

        /// <summary>
        /// Finds the frames bracketing source time <paramref name="time"/>. Times newer than the newest
        /// frame return the newest frame; times older than the oldest return the oldest.
        /// </summary>
        public bool TrySample(double time, out FrameSample sample)
        {
            sample = default;
            if (_count == 0) return false;

            StereoFrame oldest = At(0), newest = At(_count - 1);
            if (time >= newest.Time) { sample = new FrameSample { Older = newest, Newer = newest, Blend = 0 }; return true; }
            if (time <= oldest.Time) { sample = new FrameSample { Older = oldest, Newer = oldest, Blend = 0 }; return true; }

            // Binary search: frames are stored in increasing time order (logical index 0 = oldest).
            int lo = 0, hi = _count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (At(mid).Time <= time) lo = mid; else hi = mid;
            }
            StereoFrame a = At(lo), b = At(hi);
            double span = b.Time - a.Time;
            float blend = span > 1e-9 ? (float)((time - a.Time) / span) : 0f;
            sample = new FrameSample { Older = a, Newer = b, Blend = Mathf.Clamp01(blend) };
            return true;
        }

        public bool TryGetNewest(out StereoFrame frame)
        {
            frame = _count > 0 ? At(_count - 1) : null;
            return frame != null;
        }

        /// <summary>Frame by logical index, 0 = oldest.</summary>
        private StereoFrame At(int logicalIndex)
        {
            int oldestPhysical = (_head - _count + _frames.Length) % _frames.Length;
            return _frames[(oldestPhysical + logicalIndex) % _frames.Length];
        }

        public void Dispose()
        {
            if (_frames == null) return;
            foreach (StereoFrame f in _frames)
            {
                DestroyTexture(f.Left);
                DestroyTexture(f.Right);
            }
            _frames = null;
            _count = 0;
            _head = 0;
        }

        private static RenderTexture CreateTexture(int w, int h, GraphicsFormat format, string name)
        {
            var rt = new RenderTexture(w, h, 0, format)
            {
                name = name,
                useMipMap = false,
                autoGenerateMips = false,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            rt.Create();
            return rt;
        }

        private static void DestroyTexture(RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            UnityEngine.Object.Destroy(rt);
        }
    }
}
