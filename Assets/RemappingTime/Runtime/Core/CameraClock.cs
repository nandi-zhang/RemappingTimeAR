using System;
using UnityEngine;

namespace RemappingTime
{
    /// <summary>
    /// A continuous clock that runs on the passthrough camera's timebase.
    ///
    /// Camera frames carry capture timestamps but arrive at ≤ 60 Hz, while the headset renders at
    /// 72–120 Hz. If time only advanced when a frame arrived, the remapping would step in 60 Hz jumps
    /// and frame blending would have nothing to blend. Instead, between frames we extrapolate:
    ///
    ///     now = (capture time of newest frame) + (real time elapsed since it arrived)
    ///
    /// The extrapolation is capped so a stalled camera cannot make the clock run away. All times are
    /// in seconds relative to the first frame received (<see cref="Origin"/>).
    /// </summary>
    public sealed class CameraClock
    {
        /// <summary>Maximum extrapolation past the newest frame (s). Beyond this we assume the stream stalled.</summary>
        public double MaxExtrapolation = 0.1;

        public bool HasOrigin { get; private set; }
        public DateTime Origin { get; private set; }

        /// <summary>Capture time of the newest frame, in clock seconds.</summary>
        public double NewestFrameTime { get; private set; }

        private double _newestArrivalRealtime;
        private double _lastNow;

        public void Reset()
        {
            HasOrigin = false;
            NewestFrameTime = 0;
            _lastNow = 0;
        }

        /// <summary>Converts a camera timestamp to clock seconds (sets the origin on first use).</summary>
        public double ToSeconds(DateTime timestamp)
        {
            if (!HasOrigin)
            {
                Origin = timestamp;
                HasOrigin = true;
            }
            return (timestamp - Origin).TotalSeconds;
        }

        /// <summary>Call when a new frame has been captured.</summary>
        public void NotifyFrame(double captureTimeSeconds)
        {
            if (captureTimeSeconds <= NewestFrameTime && NewestFrameTime > 0) return;
            NewestFrameTime = captureTimeSeconds;
            _newestArrivalRealtime = Time.realtimeSinceStartupAsDouble;
        }

        /// <summary>The current physical time t on the camera timebase. Never decreases.</summary>
        public double Now
        {
            get
            {
                if (!HasOrigin) return 0;
                double elapsed = Time.realtimeSinceStartupAsDouble - _newestArrivalRealtime;
                double now = NewestFrameTime + Math.Min(Math.Max(0.0, elapsed), MaxExtrapolation);
                if (now < _lastNow) now = _lastNow;
                _lastNow = now;
                return now;
            }
        }
    }
}
