using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LanMic;

internal static class JavaBridgeTests
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Expected java executable and compiled class directory");
            TcpListener reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            using (ReceiverServer server = new ReceiverServer())
            {
                server.Start(port, -2);
                ProcessStartInfo info = new ProcessStartInfo(args[0]);
                info.Arguments = "-cp \"" + args[1] + "\" JavaWireIntegration " + port + " " + server.PairingToken;
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                using (Process child = Process.Start(info))
                {
                    if (!child.WaitForExit(25000)) { child.Kill(); throw new Exception("Java integration client exceeded 25 seconds"); }
                    if (child.ExitCode != 0) throw new Exception("Java integration client failed");
                }
                if (server.Snapshot.ReceivedFrames != 27) throw new Exception("Expected 27 real Java frames, got " + server.Snapshot.ReceivedFrames);
                DateTime deadline = DateTime.UtcNow.AddSeconds(2);
                while (server.Snapshot.ActiveClients != 0 && DateTime.UtcNow < deadline) Thread.Sleep(20);
                if (server.Snapshot.ActiveClients != 0) throw new Exception("Java sender was not released");
                if (server.Snapshot.OutputFrames != 0) throw new Exception("Meter-only mode claimed hardware output");
                Console.WriteLine("PASS C# independently confirms all 27 Java PCM frames arrived and connection was released");
                server.Stop();
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
