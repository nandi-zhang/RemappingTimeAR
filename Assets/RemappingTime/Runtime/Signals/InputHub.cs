using System.Collections.Generic;
using UnityEngine;

namespace RemappingTime
{
    /// <summary>
    /// Merges control inputs from the headset controllers and from web clients into one
    /// <see cref="RemapInputs"/> per frame.
    ///
    /// Default headset mapping (right controller = "time controls"; the left controller drives the HUD):
    /// <list type="bullet">
    /// <item>Right index trigger: hold (analog)</item>
    /// <item>Right thumbstick X: scrub</item>
    /// <item>A: action (e.g. fire a pulse or a freeze)</item>
    /// <item>B: snap to live</item>
    /// <item>Left grip (hold): bypass, i.e. show live for a quick A/B comparison</item>
    /// </list>
    /// In the Editor, the keyboard also works: Space = hold, ←/→ = scrub, Enter = action, Backspace = live.
    ///
    /// <para>Remote inputs time out: if a web client stops sending (closed tab, dropped Wi-Fi) while
    /// holding, its hold is released after <see cref="remoteTimeout"/> seconds, so the wearer is never left
    /// stuck in slow motion.</para>
    /// </summary>
    [DefaultExecutionOrder(-30)]
    public sealed class InputHub : MonoBehaviour
    {
        [SerializeField] private bool localControllers = true;
        [SerializeField] private float remoteTimeout = 0.6f;

        private struct RemoteState { public float Hold, Scrub; public double LastSeen; }
        private readonly Dictionary<int, RemoteState> _remote = new Dictionary<int, RemoteState>();
        private bool _queuedAction, _queuedSnap;

        public RemapInputs Current { get; private set; }
        /// <summary>True while the bypass (A/B compare) control is held.</summary>
        public bool Bypass { get; private set; }
        public int ActiveRemoteClients => _remote.Count;

        /// <summary>Updates the held/scrub state reported by a web client.</summary>
        public void SetRemoteInput(int clientId, float hold, float scrub)
        {
            _remote[clientId] = new RemoteState
            {
                Hold = Mathf.Clamp01(hold),
                Scrub = Mathf.Clamp(scrub, -1f, 1f),
                LastSeen = Time.realtimeSinceStartupAsDouble,
            };
        }

        public void RemoveRemoteClient(int clientId) => _remote.Remove(clientId);

        /// <summary>Requests an action on the next frame (from the web, a game script, …).</summary>
        public void QueueAction() => _queuedAction = true;
        public void QueueSnapToLive() => _queuedSnap = true;

        private readonly List<int> _expired = new List<int>();

        private void Update()
        {
            float localHold = 0f, localScrub = 0f;
            bool action = _queuedAction, snap = _queuedSnap, bypass = false;
            _queuedAction = _queuedSnap = false;

            if (localControllers)
            {
                localHold = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
                localScrub = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).x;
                action |= OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch);
                snap |= OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch);
                bypass = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.LTouch) > 0.6f;
            }

#if UNITY_EDITOR
            if (Input.GetKey(KeyCode.Space)) localHold = 1f;
            if (Input.GetKey(KeyCode.LeftArrow)) localScrub = -1f;
            if (Input.GetKey(KeyCode.RightArrow)) localScrub = 1f;
            action |= Input.GetKeyDown(KeyCode.Return);
            snap |= Input.GetKeyDown(KeyCode.Backspace);
#endif

            // Combine remote clients: strongest hold wins; scrub with the largest magnitude wins.
            float remoteHold = 0f, remoteScrub = 0f;
            double now = Time.realtimeSinceStartupAsDouble;
            _expired.Clear();
            foreach (KeyValuePair<int, RemoteState> kv in _remote)
            {
                if (now - kv.Value.LastSeen > remoteTimeout) { _expired.Add(kv.Key); continue; }
                remoteHold = Mathf.Max(remoteHold, kv.Value.Hold);
                if (Mathf.Abs(kv.Value.Scrub) > Mathf.Abs(remoteScrub)) remoteScrub = kv.Value.Scrub;
            }
            foreach (int id in _expired) _remote.Remove(id);

            Bypass = bypass;
            Current = new RemapInputs
            {
                LocalHold = localHold,
                RemoteHold = remoteHold,
                Hold = Mathf.Max(localHold, remoteHold),
                Scrub = Mathf.Abs(localScrub) >= Mathf.Abs(remoteScrub) ? localScrub : remoteScrub,
                Action = action,
                SnapToLive = snap,
            };
        }
    }
}
