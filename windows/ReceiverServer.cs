using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace LanMic
{
    public sealed class ReceiverSnapshot
    {
        public bool Listening;
        public string Status, DeviceName, Error;
        public long ReceivedFrames, OutputFrames, DroppedFrames, RejectedConnections;
        public int ActiveClients, PendingConnections, QueuedFrames;
        public double Rms, Peak;
        public DateTime LastFrameUtc;
    }

    public sealed class ReceiverServer : IDisposable
    {
        public const int LevelOnlyDevice = -2;
        const int MaxPending = 8;
        readonly object gate = new object();
        Session current;
        int volume = 100;
        bool muted;
        string lastError = "";

        sealed class Session
        {
            public TcpListener Listener;
            public X509Certificate2 Certificate;
            public RSA Key;
            public WaveOutPlayer Player;
            public readonly HashSet<TcpClient> Clients = new HashSet<TcpClient>();
            public TcpClient Active;
            public bool Running, CertificateDisposed;
            public string Token, Pin, Code, DeviceName = "", Error = "";
            public long Received, Rejected;
            public double Rms, Peak;
            public DateTime LastFrameUtc;
        }

        public int Volume { get { lock (gate) return volume; } set { lock (gate) volume = Math.Max(0, Math.Min(200, value)); } }
        public bool Muted { get { lock (gate) return muted; } set { lock (gate) muted = value; } }
        public string PairingToken { get { lock (gate) return current == null ? "" : current.Token; } }
        public string CertificatePin { get { lock (gate) return current == null ? "" : current.Pin; } }
        public string AuthCode { get { lock (gate) return current == null ? "" : current.Code; } }

        public ReceiverSnapshot Snapshot
        {
            get
            {
                lock (gate)
                {
                    Session s = current;
                    if (s == null) return new ReceiverSnapshot { Status = "已停止", DeviceName = "", Error = lastError };
                    return new ReceiverSnapshot { Listening = s.Running, Status = s.Active == null ? "等待手机连接" : "手机已连接",
                        DeviceName = s.DeviceName, Error = s.Error, ReceivedFrames = s.Received,
                        OutputFrames = s.Player == null ? 0 : s.Player.Completed,
                        DroppedFrames = s.Player == null ? 0 : s.Player.Dropped,
                        QueuedFrames = s.Player == null ? 0 : s.Player.QueuedFrames,
                        RejectedConnections = s.Rejected, ActiveClients = s.Active == null ? 0 : 1,
                        PendingConnections = s.Clients.Count - (s.Active == null ? 0 : 1), Rms = s.Rms, Peak = s.Peak,
                        LastFrameUtc = s.LastFrameUtc };
                }
            }
        }

        public void Start(int port, int outputDeviceId)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException("port");
            lock (gate) { if (current != null) throw new InvalidOperationException("监听已启动。"); }
            var session = new Session();
            try
            {
                if (outputDeviceId != LevelOnlyDevice) session.Player = new WaveOutPlayer(outputDeviceId);
                session.Key = new RSACng(2048);
                var request = new CertificateRequest("CN=LanMic local receiver", session.Key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                var usages = new OidCollection(); usages.Add(new Oid("1.3.6.1.5.5.7.3.1"));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
                session.Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
                using (var sha = SHA256.Create()) session.Pin = BitConverter.ToString(sha.ComputeHash(session.Certificate.RawData)).Replace("-", "").Substring(0, 20);
                // Rejection sampling keeps all six-digit codes equally likely.
                using (var rng = RandomNumberGenerator.Create())
                {
                    byte[] random = new byte[4]; uint value;
                    do { rng.GetBytes(random); value = BitConverter.ToUInt32(random, 0); } while (value >= 4294000000U);
                    session.Code = (value % 1000000U).ToString("D6");
                }
                session.Token = session.Pin.Substring(0, 4) + "-" + session.Pin.Substring(4, 4) + "-" + session.Pin.Substring(8, 4) + "-" +
                    session.Pin.Substring(12, 4) + "-" + session.Pin.Substring(16, 4) + "-" + session.Code;
                session.Listener = new TcpListener(IPAddress.Any, port);
                session.Listener.Server.ExclusiveAddressUse = true;
                session.Listener.Start(12);
                session.Running = true;
                lock (gate) { current = session; lastError = ""; }
                var thread = new Thread(delegate() { AcceptLoop(session); });
                thread.IsBackground = true; thread.Name = "LanMic accept"; thread.Start();
            }
            catch
            {
                if (session.Listener != null) session.Listener.Stop();
                if (session.Player != null) session.Player.Dispose();
                if (session.Certificate != null) session.Certificate.Dispose();
                if (session.Key != null) session.Key.Dispose();
                throw;
            }
        }

        void AcceptLoop(Session session)
        {
            while (session.Running)
            {
                TcpClient client;
                try { client = session.Listener.AcceptTcpClient(); }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                bool accepted;
                lock (gate)
                {
                    accepted = session.Running && session.Clients.Count - (session.Active == null ? 0 : 1) < MaxPending;
                    if (accepted) session.Clients.Add(client); else session.Rejected++;
                }
                if (!accepted) { client.Close(); continue; }
                ThreadPool.QueueUserWorkItem(delegate { HandleClient(session, client); });
            }
        }

        void HandleClient(Session session, TcpClient client)
        {
            bool authenticated = false;
            Timer deadline = null;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
                deadline = new Timer(delegate { try { client.Close(); } catch { } }, null, 5000, Timeout.Infinite);
                using (var ssl = new SslStream(client.GetStream(), false))
                {
                    ssl.ReadTimeout = 5000; ssl.WriteTimeout = 5000;
                    ssl.AuthenticateAsServer(session.Certificate, false, SslProtocols.Tls12, false);
                    byte[] hello = new byte[16];
                    ReadExact(ssl, hello, 16);
                    int nameLength = (hello[14] << 8) | hello[15];
                    bool codeMatches = true;
                    for (int i = 0; i < 6; i++) codeMatches &= hello[8 + i] == (byte)session.Code[i];
                    if (Encoding.ASCII.GetString(hello, 0, 8) != "LMIC0001" || !codeMatches || nameLength < 1 || nameLength > 128)
                    { Reject(session, ssl, "NO"); return; }
                    byte[] nameBytes = new byte[nameLength]; ReadExact(ssl, nameBytes, nameLength);
                    string name;
                    try { name = new UTF8Encoding(false, true).GetString(nameBytes); }
                    catch (DecoderFallbackException) { Reject(session, ssl, "NO"); return; }
                    if (String.IsNullOrWhiteSpace(name)) { Reject(session, ssl, "NO"); return; }
                    foreach (char c in name) if (Char.IsControl(c)) { Reject(session, ssl, "NO"); return; }
                    lock (gate)
                    {
                        if (!session.Running) return;
                        if (session.Active == null)
                        {
                            session.Active = client; session.DeviceName = name; session.Error = "";
                            authenticated = true;
                        }
                    }
                    if (!authenticated) { Reject(session, ssl, "BZ"); return; }
                    ssl.Write(new byte[] { 79, 75 }, 0, 2); ssl.Flush();
                    deadline.Dispose(); deadline = null;
                    byte[] size = new byte[2], frame = new byte[1920];
                    while (session.Running)
                    {
                        ReadExact(ssl, size, 2);
                        if (((size[0] << 8) | size[1]) != frame.Length) throw new InvalidDataException("手机发送了不支持的音频帧长度。");
                        ReadExact(ssl, frame, frame.Length);
                        double sum = 0, peak = 0;
                        for (int i = 0; i < frame.Length; i += 2)
                        {
                            int value = (short)(frame[i] | frame[i + 1] << 8);
                            double sample = value / 32768.0; sum += sample * sample; peak = Math.Max(peak, Math.Abs(sample));
                        }
                        int gain; bool silence;
                        lock (gate)
                        {
                            session.Received++; session.Rms = Math.Sqrt(sum / 960.0); session.Peak = peak;
                            session.LastFrameUtc = DateTime.UtcNow; gain = volume; silence = muted;
                        }
                        if (session.Player != null)
                        {
                            if (silence || gain == 0) Array.Clear(frame, 0, frame.Length);
                            else if (gain != 100)
                            {
                                for (int i = 0; i < frame.Length; i += 2)
                                {
                                    int value = (short)(frame[i] | frame[i + 1] << 8);
                                    value = Math.Max(Int16.MinValue, Math.Min(Int16.MaxValue, value * gain / 100));
                                    frame[i] = (byte)value; frame[i + 1] = (byte)(value >> 8);
                                }
                            }
                            session.Player.Submit(frame);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    if (session.Running && authenticated && !(ex is EndOfStreamException))
                        session.Error = ex is IOException || ex is SocketException ? "连接中断或超过 5 秒未收到音频。" : ex.Message;
                    if (!authenticated) session.Rejected++;
                }
            }
            finally
            {
                if (deadline != null) deadline.Dispose();
                client.Close();
                lock (gate)
                {
                    // Keep the active slot until the old playback queue is cleared,
                    // so an arriving sender can never have its audio reset by this one.
                    if (session.Active == client)
                    {
                        if (session.Player != null) { try { session.Player.Reset(); } catch (Exception ex) { session.Error = ex.Message; } }
                        session.Active = null; session.DeviceName = ""; session.Rms = session.Peak = 0;
                    }
                    session.Clients.Remove(client);
                    ReleaseCertificate(session);
                }
            }
        }

        void Reject(Session session, SslStream ssl, string code)
        {
            lock (gate) session.Rejected++;
            byte[] response = Encoding.ASCII.GetBytes(code); ssl.Write(response, 0, response.Length); ssl.Flush();
        }

        static void ReadExact(Stream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
        }

        void ReleaseCertificate(Session session)
        {
            if (!session.Running && session.Clients.Count == 0 && !session.CertificateDisposed)
            {
                session.CertificateDisposed = true;
                session.Certificate.Dispose(); session.Key.Dispose();
            }
        }

        public void Stop()
        {
            Session session;
            lock (gate)
            {
                session = current; if (session == null) return;
                session.Running = false; current = null;
                session.Listener.Stop();
                foreach (TcpClient client in session.Clients) client.Close();
                ReleaseCertificate(session);
            }
            if (session.Player != null) session.Player.Dispose();
        }

        public void Dispose() { Stop(); }
    }
}
