using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LanMic;

// Real loopback TCP v2; no password, TLS, microphone, or playback device.
internal static class ReceiverIntegrationTests
{
    private static int passed;
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        passed++;
        Console.WriteLine("PASS " + message);
    }
    private static void Until(Func<bool> predicate, int milliseconds, string message)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end)
        {
            if (predicate()) { Check(true, message); return; }
            Thread.Sleep(20);
        }
        Check(false, message);
    }
    private sealed class Client : IDisposable
    {
        public readonly TcpClient Tcp;
        public readonly NetworkStream Stream;
        public Client(int port)
        {
            Tcp = new TcpClient();
            Tcp.NoDelay = true;
            Tcp.Connect(IPAddress.Loopback, port);
            Tcp.ReceiveTimeout = 3000;
            Tcp.SendTimeout = 3000;
            Stream = Tcp.GetStream();
            Stream.ReadTimeout = 3000;
            Stream.WriteTimeout = 3000;
        }
        public string Hello(bool fragmented)
        {
            byte[] name = Encoding.UTF8.GetBytes("Integration 手机");
            return Request("LMIC0002", name, name.Length, fragmented);
        }
        public string Request(string magic, byte[] name, int declaredLength, bool fragmented)
        {
            byte[] header = Encoding.ASCII.GetBytes(magic);
            if (header.Length != 8) throw new ArgumentException("Magic must be 8 ASCII bytes");
            byte[] bytes = new byte[10 + name.Length];
            Buffer.BlockCopy(header, 0, bytes, 0, 8);
            bytes[8] = (byte)(declaredLength >> 8);
            bytes[9] = (byte)declaredLength;
            Buffer.BlockCopy(name, 0, bytes, 10, name.Length);
            if (fragmented)
            {
                foreach (byte value in bytes) { Stream.WriteByte(value); Stream.Flush(); }
            }
            else Stream.Write(bytes, 0, bytes.Length);
            byte[] reply = new byte[2];
            ReadFully(Stream, reply);
            return Encoding.ASCII.GetString(reply);
        }
        public void Frame(byte[] pcm, bool fragmented)
        {
            Stream.WriteByte((byte)(pcm.Length >> 8));
            Stream.WriteByte((byte)pcm.Length);
            if (fragmented)
            {
                for (int offset = 0; offset < pcm.Length; offset += 37)
                    Stream.Write(pcm, offset, Math.Min(37, pcm.Length - offset));
            }
            else Stream.Write(pcm, 0, pcm.Length);
            Stream.Flush();
        }
        public void Dispose() { Stream.Dispose(); Tcp.Close(); }
    }
    private static void ReadFully(Stream stream, byte[] bytes)
    {
        int offset = 0;
        while (offset < bytes.Length)
        {
            int count = stream.Read(bytes, offset, bytes.Length - offset);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
    }
    private static byte[] Tone()
    {
        byte[] result = new byte[1920];
        for (int n = 0; n < 960; n++)
        {
            short value = (short)(Math.Sin(2 * Math.PI * 1000 * n / 48000) * 16384);
            result[n * 2] = (byte)value;
            result[n * 2 + 1] = (byte)(value >> 8);
        }
        return result;
    }
    public static int Main()
    {
        try
        {
            TcpListener reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            using (ReceiverServer server = new ReceiverServer())
            {
                server.Start(port, ReceiverServer.LevelOnlyDevice);
                byte[] name = Encoding.UTF8.GetBytes("Raw TCP client");
                using (Client raw = new Client(port))
                    Check(raw.Request("LMIC0002", name, name.Length, false) == "OK", "raw TCP socket connects with only magic and device name; no password is required");
                Until(delegate { return server.Snapshot.ActiveClients == 0; }, 2000, "raw TCP source disconnects cleanly");
                using (Client wrongMagic = new Client(port))
                    Check(wrongMagic.Request("BAD00000", name, name.Length, false) == "NO", "wrong protocol magic is rejected");
                using (Client legacy = new Client(port))
                    Check(legacy.Request("LMIC0001", name, name.Length, false) == "NO", "legacy LMIC0001 magic is rejected");
                using (Client emptyName = new Client(port))
                    Check(emptyName.Request("LMIC0002", new byte[0], 0, false) == "NO", "empty device name length is rejected");
                using (Client oversizedName = new Client(port))
                    Check(oversizedName.Request("LMIC0002", new byte[0], 129, false) == "NO", "oversized device name length is rejected before reading payload");
                using (Client invalidUtf8 = new Client(port))
                    Check(invalidUtf8.Request("LMIC0002", new byte[] { 0xC3, 0x28 }, 2, false) == "NO", "malformed UTF-8 device name is rejected");
                using (Client whitespaceName = new Client(port))
                    Check(whitespaceName.Request("LMIC0002", new byte[] { 32, 32 }, 2, false) == "NO", "blank device name is rejected");
                using (TcpClient idle = new TcpClient())
                {
                    idle.Connect(IPAddress.Loopback, port);
                    using (Client good = new Client(port))
                    {
                        Check(good.Hello(true) == "OK", "fragmented TCP handshake succeeds while another socket is idle");
                        byte[] pcm = Tone();
                        long before = server.Snapshot.ReceivedFrames;
                        good.Frame(pcm, true);
                        good.Frame(pcm, false);
                        Until(delegate { return server.Snapshot.ReceivedFrames >= before + 2; }, 2000, "fragmented PCM is reassembled and received");
                        Check(Math.Abs(server.Snapshot.Peak - 0.5) < 0.002 && Math.Abs(server.Snapshot.Rms - Math.Sqrt(0.125)) < 0.002, "meter matches actual 1 kHz PCM peak and RMS");
                        Check(server.Snapshot.OutputFrames == 0, "meter-only mode does not claim physical audio output");
                        using (Client busy = new Client(port))
                            Check(busy.Hello(false) == "BZ", "second sender receives busy");
                        before = server.Snapshot.ReceivedFrames;
                        good.Frame(new byte[1920], false);
                        Until(delegate { return server.Snapshot.ReceivedFrames > before; }, 2000, "busy rejection does not interrupt active sender");
                        Check(server.Snapshot.Peak == 0, "silent PCM produces zero meter");
                    }
                }
                Until(delegate { return server.Snapshot.ActiveClients == 0; }, 2000, "disconnect releases active source");
                using (Client malformed = new Client(port))
                {
                    Check(malformed.Hello(false) == "OK", "new sender can reconnect");
                    malformed.Stream.Write(new byte[] { 255, 255 }, 0, 2);
                    malformed.Stream.Flush();
                    Until(delegate { return server.Snapshot.ActiveClients == 0; }, 2000, "invalid PCM length terminates source before allocation");
                }
                Until(delegate { return server.Snapshot.PendingConnections == 0; }, 2000, "completed invalid requests release all pending slots");
                using (TcpClient idle = new TcpClient())
                {
                    idle.Connect(IPAddress.Loopback, port);
                    Until(delegate { return server.Snapshot.PendingConnections == 1; }, 2000, "idle TCP socket is tracked while waiting for protocol header");
                    Until(delegate { return server.Snapshot.PendingConnections == 0; }, 6500, "idle TCP socket times out");
                }
                using (Client idleSource = new Client(port))
                {
                    Check(idleSource.Hello(false) == "OK", "source accepted before audio timeout test");
                    Until(delegate { return server.Snapshot.ActiveClients == 0; }, 6500, "connected sender that sends no PCM times out");
                }
                using (Client active = new Client(port))
                {
                    Check(active.Hello(false) == "OK", "source accepted before stop");
                    active.Frame(Tone(), false);
                    server.Stop();
                    Check(server.Snapshot.ActiveClients == 0, "stop closes active source");
                }
                bool stopped = false;
                try { using (TcpClient probe = new TcpClient()) probe.Connect(IPAddress.Loopback, port); }
                catch (SocketException) { stopped = true; }
                Check(stopped, "stopped listener refuses connections");
                server.Start(port, ReceiverServer.LevelOnlyDevice);
                using (Client restarted = new Client(port))
                    Check(restarted.Hello(false) == "OK", "same server object restarts on same port without a password");
                server.Stop();
            }
            Console.WriteLine("RESULT " + passed + " integration assertions passed");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
