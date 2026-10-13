using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using LanMic;

// Real loopback TLS, no physical microphone or audio device required.
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
    private static string Pin(X509Certificate certificate)
    {
        using (SHA256 hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(certificate.GetRawCertData())).Replace("-", "").Substring(0, 20);
    }
    private sealed class Client : IDisposable
    {
        public readonly TcpClient Tcp;
        public readonly SslStream Stream;
        public Client(int port, string expectedPin)
        {
            Tcp = new TcpClient();
            Tcp.NoDelay = true;
            Tcp.Connect(IPAddress.Loopback, port);
            Tcp.ReceiveTimeout = 3000;
            Tcp.SendTimeout = 3000;
            Stream = new SslStream(Tcp.GetStream(), false, delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
            {
                return cert != null && Pin(cert) == expectedPin;
            });
            Stream.ReadTimeout = 3000;
            Stream.WriteTimeout = 3000;
            try { Stream.AuthenticateAsClient("LanMic", null, SslProtocols.Tls12, false); }
            catch { Stream.Dispose(); Tcp.Close(); throw; }
        }
        public string Auth(string auth, bool fragmented)
        {
            byte[] name = Encoding.UTF8.GetBytes("Integration 手机");
            byte[] header = Encoding.ASCII.GetBytes("LMIC0001" + auth);
            byte[] bytes = new byte[header.Length + 2 + name.Length];
            Buffer.BlockCopy(header, 0, bytes, 0, header.Length);
            bytes[14] = (byte)(name.Length >> 8);
            bytes[15] = (byte)name.Length;
            Buffer.BlockCopy(name, 0, bytes, 16, name.Length);
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
                server.Start(port, -2);
                string pin = server.CertificatePin;
                string auth = server.AuthCode;
                Check(pin.Length == 20 && auth.Length == 6, "pairing components use expected lengths");
                bool wrongPinRejected = false;
                try { using (Client badPin = new Client(port, new string('0', 20))) { } }
                catch (AuthenticationException) { wrongPinRejected = true; }
                Check(wrongPinRejected, "client rejects wrong pinned certificate");
                using (Client wrong = new Client(port, pin))
                    Check(wrong.Auth(auth == "000000" ? "999999" : "000000", false) == "NO", "wrong authentication is rejected");
                using (TcpClient idle = new TcpClient())
                {
                    idle.Connect(IPAddress.Loopback, port);
                    using (Client good = new Client(port, pin))
                    {
                        Check(good.Auth(auth, true) == "OK", "fragmented handshake succeeds while another socket is idle");
                        byte[] pcm = Tone();
                        long before = server.Snapshot.ReceivedFrames;
                        good.Frame(pcm, true);
                        good.Frame(pcm, false);
                        Until(delegate { return server.Snapshot.ReceivedFrames >= before + 2; }, 2000, "fragmented PCM is reassembled and received");
                        Check(server.Snapshot.Peak > 0.45 && server.Snapshot.Rms > 0.3, "meter reflects actual 1 kHz PCM samples");
                        Check(server.Snapshot.OutputFrames == 0, "meter-only mode does not claim physical audio output");
                        using (Client busy = new Client(port, pin))
                            Check(busy.Auth(auth, false) == "BZ", "second authenticated sender receives busy");
                        before = server.Snapshot.ReceivedFrames;
                        good.Frame(new byte[1920], false);
                        Until(delegate { return server.Snapshot.ReceivedFrames > before; }, 2000, "busy rejection does not interrupt active sender");
                        Check(server.Snapshot.Peak == 0, "silent PCM produces zero meter");
                    }
                }
                Until(delegate { return server.Snapshot.ActiveClients == 0; }, 2000, "disconnect releases active source");
                using (Client malformed = new Client(port, pin))
                {
                    Check(malformed.Auth(auth, false) == "OK", "new sender can reconnect");
                    malformed.Stream.Write(new byte[] { 255, 255 }, 0, 2);
                    malformed.Stream.Flush();
                    Until(delegate { return server.Snapshot.ActiveClients == 0; }, 2000, "invalid PCM length terminates source before allocation");
                }
                using (Client malformedName = new Client(port, pin))
                {
                    byte[] prefix = Encoding.ASCII.GetBytes("LMIC0001" + auth);
                    malformedName.Stream.Write(prefix, 0, prefix.Length);
                    malformedName.Stream.Write(new byte[] { 255, 255 }, 0, 2);
                    malformedName.Stream.Flush();
                    byte[] reply = new byte[2];
                    ReadFully(malformedName.Stream, reply);
                    Check(Encoding.ASCII.GetString(reply) == "NO", "oversized device name is rejected");
                }
                using (TcpClient idle = new TcpClient())
                {
                    idle.Connect(IPAddress.Loopback, port);
                    Thread.Sleep(100);
                    Until(delegate { return server.Snapshot.PendingConnections == 0; }, 6500, "unauthenticated idle socket times out");
                }
                string oldToken = server.PairingToken;
                using (Client active = new Client(port, pin))
                {
                    Check(active.Auth(auth, false) == "OK", "source accepted before stop");
                    active.Frame(Tone(), false);
                    server.Stop();
                    Check(server.Snapshot.ActiveClients == 0, "stop closes active source");
                }
                bool stopped = false;
                try { using (TcpClient probe = new TcpClient()) probe.Connect(IPAddress.Loopback, port); }
                catch (SocketException) { stopped = true; }
                Check(stopped, "stopped listener refuses connections");
                server.Start(port, -2);
                Check(server.PairingToken != oldToken, "new listener session rotates pairing token");
                using (Client restarted = new Client(port, server.CertificatePin))
                    Check(restarted.Auth(server.AuthCode, false) == "OK", "same server object restarts on same port");
                for (int attempt = 0; attempt < 5; attempt++)
                    using (Client wrong = new Client(port, server.CertificatePin))
                        if (wrong.Auth(server.AuthCode == "000000" ? "999999" : "000000", false) != "NO")
                            throw new Exception("Invalid authentication was accepted during rate-limit test");
                using (Client coolingDown = new Client(port, server.CertificatePin))
                    Check(coolingDown.Auth(server.AuthCode, false) == "NO", "five wrong codes trigger authentication cooldown");
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
