using System;
using System.Threading;
using LanMic;

internal static class AudioOutputSmokeTests
{
    public static int Main()
    {
        try
        {
            var devices = WaveOutPlayer.GetDevices();
            foreach (AudioDevice device in devices) Console.WriteLine("OUTPUT " + device.Id + " " + device.Name);
            if (devices.Count == 0) { Console.WriteLine("SKIP no physical or virtual playback endpoint is present"); return 0; }
            // Use silence only, never capture the user's microphone or change defaults.
            byte[] silence = new byte[1920];
            for (int cycle = 0; cycle < 3; cycle++)
            {
                using (WaveOutPlayer player = new WaveOutPlayer(devices[0].Id))
                {
                    for (int frame = 0; frame < 15; frame++) { player.Submit(silence); Thread.Sleep(20); }
                    Thread.Sleep(160);
                    if (player.Completed == 0) throw new Exception("Playback device did not return any completed frame");
                    if (player.QueuedFrames > 6) throw new Exception("Native audio queue exceeded 120 ms");
                    for (int frame = 0; frame < 100; frame++) player.Submit(silence);
                    if (player.QueuedFrames > 6 || player.Dropped == 0) throw new Exception("Backlog was not bounded and discarded");
                    player.Reset();
                    if (player.QueuedFrames != 0) throw new Exception("Reset left pending audio");
                }
            }
            Console.WriteLine("PASS real waveOut accepts/completes silent audio, bounds overload, resets and reopens across 3 cycles");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
