using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEngine;

namespace RemappingTime.Remote
{
    /// <summary>Finds the headset's LAN IPv4 address so the HUD can show where to point a browser.</summary>
    public static class NetworkInfo
    {
        public static string LocalIPv4()
        {
            // 1) Ask the OS which local address it would use to reach the outside world. No packets are
            //    sent; connecting a UDP socket only selects a route. Works on most Wi-Fi networks,
            //    including routers without internet (they still provide a default route).
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect("8.8.8.8", 65530);
                    if (s.LocalEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address) && !ep.Address.Equals(IPAddress.Any))
                        return ep.Address.ToString();
                }
            }
            catch (Exception) { /* fall through */ }

            // 2) Enumerate interfaces.
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                            return a.Address.ToString();
                }
            }
            catch (Exception) { /* fall through */ }

#if UNITY_ANDROID && !UNITY_EDITOR
            // 3) Android WifiManager (needs ACCESS_WIFI_STATE).
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (AndroidJavaObject wifi = activity.Call<AndroidJavaObject>("getSystemService", "wifi"))
                using (AndroidJavaObject info = wifi.Call<AndroidJavaObject>("getConnectionInfo"))
                {
                    int ip = info.Call<int>("getIpAddress");
                    if (ip != 0) return $"{ip & 0xFF}.{(ip >> 8) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 24) & 0xFF}";
                }
            }
            catch (Exception) { /* fall through */ }
#endif
            return "127.0.0.1";
        }
    }
}
