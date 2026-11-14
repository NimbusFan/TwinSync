using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    // Advertisements are hints, never pairing credentials. Trust is established by the existing TLS pairing flow.
    internal sealed class LanDiscovery : IDisposable
    {
        private readonly object gate = new object();
        private readonly Func<JObject> identity;
        private readonly string deviceId;
        private readonly Dictionary<string, JObject> nearby = new Dictionary<string, JObject>();
        private readonly Dictionary<string, DateTime> seen = new Dictionary<string, DateTime>();
        private UdpClient socket;
        private Timer timer;
        private readonly int port;
        private readonly bool advertise;
        private const string Group = "239.255.55.22";
        internal string Error { get; private set; }
        internal LanDiscovery(Func<JObject> identity, int port = 55221, bool advertise = true) { this.identity = identity; this.port = port; this.advertise = advertise; deviceId = (string)identity()["id"]; }
        internal void SetEnabled(bool enabled)
        {
            lock (gate)
            {
                if (!enabled) { Error = null; Close(); return; }
                if (socket != null) return;
                UdpClient udp = null;
                try
                {
                    udp = new UdpClient(AddressFamily.InterNetwork);
                    udp.ExclusiveAddressUse = false;
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                    udp.EnableBroadcast = true;
                    udp.MulticastLoopback = false;
                    udp.Ttl = 1;
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                    foreach (var ip in Addresses()) try { udp.JoinMulticastGroup(IPAddress.Parse(Group), ip); } catch (SocketException) { }
                    socket = udp; Error = null;
                    var receiver = new Thread(delegate() { Receive(udp); }) { IsBackground = true, Name = "TwinSync discovery" };
                    receiver.Start();
                    if (advertise) timer = new Timer(delegate { Announce(); }, null, 0, 10000);
                }
                catch (Exception error) { Error = error.Message; if (udp != null) udp.Close(); Close(); }
            }
        }
        private static IEnumerable<IPAddress> Addresses()
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && Private(a)).Distinct().ToArray();
        }
        private static bool Private(IPAddress address)
        {
            var b = address.GetAddressBytes();
            return b.Length == 4 && (b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254 || b[0] == 127);
        }
        private void Announce()
        {
            try
            {
            var data = identity();
            data["service"] = "TwinSync"; data["protocol"] = 2;
            var bytes = Encoding.UTF8.GetBytes(data.ToString(Newtonsoft.Json.Formatting.None));
            lock (gate)
            {
                if (socket == null) return;
                foreach (var ip in Addresses())
                {
                    try
                    {
                        try { socket.JoinMulticastGroup(IPAddress.Parse(Group), ip); } catch (SocketException) { }
                        socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ip.GetAddressBytes());
                        socket.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse(Group), port));
                    }
                    catch (SocketException) { }
                }
                foreach (var address in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && Private(a.Address) && !IPAddress.IsLoopback(a.Address)))
                {
                    try
                    {
                        var ip = address.Address.GetAddressBytes(); var mask = address.IPv4Mask.GetAddressBytes();
                        for (var i = 0; i < ip.Length; i++) ip[i] = (byte)(ip[i] | ~mask[i]);
                        socket.Send(bytes, bytes.Length, new IPEndPoint(new IPAddress(ip), port));
                    }
                    catch (SocketException) { }
                }
            }
            }
            catch (Exception error) { Error = error.Message; }
        }
        private void Receive(UdpClient udp)
        {
            while (true)
            {
                try { var endpoint = new IPEndPoint(IPAddress.Any, 0); var bytes = udp.Receive(ref endpoint); lock (gate) { if (socket != udp) return; Observe(bytes, endpoint.Address, DateTime.UtcNow); } }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { lock (gate) if (socket != udp) return; Thread.Sleep(100); }
                catch { }
            }
        }
        internal void Observe(byte[] bytes, IPAddress source, DateTime now)
        {
            if (bytes.Length > 1024 || !Private(source)) return;
            try
            {
                var item = JObject.Parse(Encoding.UTF8.GetString(bytes)); Guid id;
                var key = JsonUtil.GetString(item["id"], ""); var name = JsonUtil.GetString(item["name"], ""); var endpointPort = JsonUtil.GetInt(item["port"], 0);
                if ((string)item["service"] != "TwinSync" || JsonUtil.GetInt(item["protocol"], 0) != 2 || !Guid.TryParse(key, out id) || key == deviceId || name.Length > 64 || name.Any(char.IsControl) || endpointPort < 1 || endpointPort > 65535) return;
                key = id.ToString(); if (key == deviceId) return;
                lock (gate)
                {
                    Expire(now);
                    if (nearby.Count >= 128 && !nearby.ContainsKey(key)) return;
                    nearby[key] = JsonUtil.Obj("id", key, "name", string.IsNullOrWhiteSpace(name) ? "未命名设备" : name, "address", source.ToString(), "port", endpointPort);
                    seen[key] = now;
                }
            }
            catch { }
        }
        private void Expire(DateTime now) { foreach (var key in seen.Where(p => now - p.Value > TimeSpan.FromSeconds(35)).Select(p => p.Key).ToArray()) { seen.Remove(key); nearby.Remove(key); } }
        internal JArray Snapshot() { lock (gate) { Expire(DateTime.UtcNow); return new JArray(nearby.Values.Select(v => v.DeepClone())); } }
        private void Close() { if (timer != null) timer.Dispose(); timer = null; if (socket != null) socket.Close(); socket = null; nearby.Clear(); seen.Clear(); }
        public void Dispose() { lock (gate) Close(); }
    }
}
