using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace RemappingTime.Remote
{
    /// <summary>
    /// A tiny, dependency-free HTTP + WebSocket (RFC 6455) server that runs inside the headset app.
    ///
    /// It serves static pages over plain HTTP (so any phone or laptop browser on the same network can
    /// open <c>http://&lt;headset-ip&gt;:&lt;port&gt;/</c>) and upgrades connections to WebSockets on the same
    /// port for live, two-way messaging. It handles a handful of clients, not the open internet: there is
    /// no TLS, and messages are capped in size.
    ///
    /// <para><b>Threading.</b> Accepting, reading and writing happen on background threads. Received
    /// messages are queued and must be drained on Unity's main thread with <see cref="TryDequeue"/>.
    /// <see cref="Send"/>/<see cref="Broadcast"/> are safe to call from any thread; each client has its own
    /// bounded outgoing queue, so a slow client cannot stall the app (excess messages are dropped).</para>
    /// </summary>
    public sealed class MiniWebServer : IDisposable
    {
        public enum EventKind { Connected, Message, Disconnected }

        public struct Event
        {
            public EventKind Kind;
            public int ClientId;
            public string Text;
            public string RemoteAddress;
        }

        /// <summary>Resolves an HTTP GET path to a response. Called on worker threads; must be thread-safe.</summary>
        public delegate bool HttpHandler(string path, out string contentType, out byte[] body);

        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int MaxHeaderBytes = 16 * 1024;
        private const int MaxMessageBytes = 256 * 1024;
        private const int SendQueueCapacity = 128;

        private sealed class Client
        {
            public int Id;
            public TcpClient Tcp;
            public NetworkStream Stream;
            public string RemoteAddress;
            public readonly BlockingCollection<byte[]> Outgoing = new BlockingCollection<byte[]>(SendQueueCapacity);
            public int Closed;
        }

        private readonly ConcurrentQueue<Event> _events = new ConcurrentQueue<Event>();
        private readonly ConcurrentDictionary<int, Client> _clients = new ConcurrentDictionary<int, Client>();
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private int _nextId;

        public HttpHandler Http { get; set; }
        public int Port { get; private set; }
        public bool IsRunning => _running;
        public int ClientCount => _clients.Count;

        public bool Start(int port)
        {
            Stop();
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
            }
            catch (Exception e)
            {
                Debug.LogError($"[RemappingTime] Web server could not listen on port {port}: {e.Message}");
                return false;
            }

            Port = port;
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "RemapWebAccept" };
            _acceptThread.Start();
            return true;
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { /* ignored */ }
            _listener = null;
            foreach (Client c in _clients.Values) Close(c);
            _clients.Clear();
        }

        public void Dispose() => Stop();

        /// <summary>Takes the next received event, if any. Call from the main thread.</summary>
        public bool TryDequeue(out Event e) => _events.TryDequeue(out e);

        public void Send(int clientId, string text)
        {
            if (_clients.TryGetValue(clientId, out Client c)) Enqueue(c, EncodeFrame(0x1, Encoding.UTF8.GetBytes(text)));
        }

        /// <summary>Sends to every connected client, optionally filtered.</summary>
        public void Broadcast(string text, Func<int, bool> filter = null)
        {
            if (_clients.IsEmpty) return;
            byte[] frame = EncodeFrame(0x1, Encoding.UTF8.GetBytes(text));
            foreach (Client c in _clients.Values)
                if (filter == null || filter(c.Id)) Enqueue(c, frame);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Connection handling
        // ─────────────────────────────────────────────────────────────────────

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient tcp;
                try { tcp = _listener.AcceptTcpClient(); }
                catch { if (_running) Thread.Sleep(50); continue; }

                var t = new Thread(() => HandleConnection(tcp)) { IsBackground = true, Name = "RemapWebClient" };
                t.Start();
            }
        }

        private void HandleConnection(TcpClient tcp)
        {
            Client client = null;
            try
            {
                tcp.NoDelay = true;
                NetworkStream stream = tcp.GetStream();
                stream.ReadTimeout = 10000;

                string header = ReadHttpHeader(stream);
                if (header == null) { tcp.Close(); return; }

                string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] request = lines[0].Split(' ');
                string path = request.Length > 1 ? request[1] : "/";
                int q = path.IndexOf('?');
                if (q >= 0) path = path.Substring(0, q);

                string key = null;
                bool upgrade = false;
                foreach (string line in lines)
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string name = line.Substring(0, colon).Trim();
                    string value = line.Substring(colon + 1).Trim();
                    if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)) key = value;
                    if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) &&
                        value.Equals("websocket", StringComparison.OrdinalIgnoreCase)) upgrade = true;
                }

                if (!upgrade || key == null)
                {
                    ServeHttp(stream, path);
                    tcp.Close();
                    return;
                }

                // WebSocket handshake.
                // SHA1Managed is constructed directly (not via SHA1.Create()) so IL2CPP code stripping keeps it.
                string accept;
#pragma warning disable SYSLIB0021, CS0618
                using (var sha1 = new SHA1Managed())
#pragma warning restore SYSLIB0021, CS0618
                    accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
                byte[] response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                    $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
                stream.Write(response, 0, response.Length);
                stream.ReadTimeout = Timeout.Infinite;

                client = new Client
                {
                    Id = Interlocked.Increment(ref _nextId),
                    Tcp = tcp,
                    Stream = stream,
                    RemoteAddress = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?",
                };
                _clients[client.Id] = client;
                Client sender = client;
                new Thread(() => SendLoop(sender)) { IsBackground = true, Name = "RemapWebSend" }.Start();
                _events.Enqueue(new Event { Kind = EventKind.Connected, ClientId = client.Id, RemoteAddress = client.RemoteAddress });

                ReadLoop(client);
            }
            catch (Exception)
            {
                // Connection dropped; fall through to cleanup.
            }
            finally
            {
                if (client != null) Close(client);
                else try { tcp.Close(); } catch { /* ignored */ }
            }
        }

        private void ServeHttp(NetworkStream stream, string path)
        {
            string status = "200 OK";
            string contentType = "text/plain; charset=utf-8";
            byte[] body;
            if (Http == null || !Http(path, out contentType, out body))
            {
                status = "404 Not Found";
                contentType = "text/plain; charset=utf-8";
                body = Encoding.UTF8.GetBytes("Not found");
            }
            byte[] head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n" +
                "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static string ReadHttpHeader(NetworkStream stream)
        {
            var buffer = new MemoryStream();
            while (buffer.Length < MaxHeaderBytes)
            {
                int b = stream.ReadByte();
                if (b < 0) return null;
                buffer.WriteByte((byte)b);
                long n = buffer.Length;
                byte[] a = buffer.GetBuffer();
                if (n >= 4 && a[n - 4] == 13 && a[n - 3] == 10 && a[n - 2] == 13 && a[n - 1] == 10) // CR LF CR LF
                    return Encoding.ASCII.GetString(a, 0, (int)n);
            }
            return null;
        }

        private void ReadLoop(Client c)
        {
            NetworkStream s = c.Stream;
            var header = new byte[14];
            var message = new MemoryStream();
            int messageOpcode = 0;

            while (_running && c.Closed == 0)
            {
                if (!ReadExactly(s, header, 0, 2)) return;
                bool fin = (header[0] & 0x80) != 0;
                int opcode = header[0] & 0x0F;
                bool masked = (header[1] & 0x80) != 0;
                long length = header[1] & 0x7F;

                if (length == 126)
                {
                    if (!ReadExactly(s, header, 0, 2)) return;
                    length = (header[0] << 8) | header[1];
                }
                else if (length == 127)
                {
                    if (!ReadExactly(s, header, 0, 8)) return;
                    length = 0;
                    for (int i = 0; i < 8; i++) length = (length << 8) | header[i];
                }
                if (length < 0 || length > MaxMessageBytes) return;

                var mask = new byte[4];
                if (masked && !ReadExactly(s, mask, 0, 4)) return;

                var payload = new byte[length];
                if (length > 0 && !ReadExactly(s, payload, 0, (int)length)) return;
                if (masked) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

                switch (opcode)
                {
                    case 0x0: // continuation
                    case 0x1: // text
                    case 0x2: // binary
                        if (opcode != 0x0) { message.SetLength(0); messageOpcode = opcode; }
                        message.Write(payload, 0, payload.Length);
                        if (message.Length > MaxMessageBytes) return;
                        if (fin)
                        {
                            if (messageOpcode == 0x1)
                                _events.Enqueue(new Event
                                {
                                    Kind = EventKind.Message,
                                    ClientId = c.Id,
                                    Text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length),
                                });
                            message.SetLength(0);
                        }
                        break;
                    case 0x8: // close
                        Enqueue(c, EncodeFrame(0x8, Array.Empty<byte>()));
                        return;
                    case 0x9: // ping
                        Enqueue(c, EncodeFrame(0xA, payload));
                        break;
                    // 0xA pong: ignore
                }
            }
        }

        private void SendLoop(Client c)
        {
            try
            {
                foreach (byte[] frame in c.Outgoing.GetConsumingEnumerable())
                {
                    c.Stream.Write(frame, 0, frame.Length);
                    if (frame.Length > 0 && (frame[0] & 0x0F) == 0x8) break; // sent a close frame
                }
            }
            catch (Exception)
            {
                // Socket closed underneath us.
            }
            Close(c);
        }

        private static void Enqueue(Client c, byte[] frame)
        {
            if (c.Closed != 0) return;
            try { c.Outgoing.TryAdd(frame); } catch (InvalidOperationException) { /* completed */ }
        }

        private void Close(Client c)
        {
            if (Interlocked.Exchange(ref c.Closed, 1) != 0) return;
            try { c.Outgoing.CompleteAdding(); } catch { /* ignored */ }
            try { c.Tcp.Close(); } catch { /* ignored */ }
            _clients.TryRemove(c.Id, out _);
            _events.Enqueue(new Event { Kind = EventKind.Disconnected, ClientId = c.Id, RemoteAddress = c.RemoteAddress });
        }

        private static bool ReadExactly(Stream s, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int n = s.Read(buffer, offset, count);
                if (n <= 0) return false;
                offset += n;
                count -= n;
            }
            return true;
        }

        /// <summary>Builds an unmasked server-to-client frame.</summary>
        private static byte[] EncodeFrame(int opcode, byte[] payload)
        {
            int len = payload.Length;
            int headerLen = len < 126 ? 2 : len <= 0xFFFF ? 4 : 10;
            var frame = new byte[headerLen + len];
            frame[0] = (byte)(0x80 | opcode);
            if (len < 126)
            {
                frame[1] = (byte)len;
            }
            else if (len <= 0xFFFF)
            {
                frame[1] = 126;
                frame[2] = (byte)(len >> 8);
                frame[3] = (byte)len;
            }
            else
            {
                frame[1] = 127;
                for (int i = 0; i < 8; i++) frame[2 + i] = (byte)((long)len >> (8 * (7 - i)));
            }
            Buffer.BlockCopy(payload, 0, frame, headerLen, len);
            return frame;
        }
    }
}
