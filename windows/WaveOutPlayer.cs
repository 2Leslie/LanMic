using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace LanMic
{
    public sealed class AudioDevice
    {
        public int Id { get; private set; }
        public string Name { get; private set; }
        public bool IsCable { get { return Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0; } }
        public AudioDevice(int id, string name) { Id = id; Name = name; }
        public override string ToString() { return Name; }
    }

    // Every native header and sample block stays allocated until winmm returns it.
    // No managed array is handed to waveOut, and no callback runs after disposal.
    public sealed class WaveOutPlayer : IDisposable
    {
        const int SlotCount = 6;
        const int FrameBytes = 1920;
        const uint Done = 1;
        const uint StillPlaying = 33;
        readonly object sync = new object();
        readonly List<Slot> slots = new List<Slot>();
        IntPtr handle;
        bool disposed;
        long submitted;
        long completed;
        long dropped;

        sealed class Slot
        {
            public IntPtr Data;
            public IntPtr Header;
            public bool Prepared;
            public bool Queued;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        struct WaveFormat
        {
            public ushort FormatTag, Channels;
            public uint SamplesPerSec, AvgBytesPerSec;
            public ushort BlockAlign, BitsPerSample, ExtraSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHeader
        {
            public IntPtr Data;
            public uint BufferLength, BytesRecorded;
            public UIntPtr User;
            public uint Flags, Loops;
            public IntPtr Next;
            public UIntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WaveCaps
        {
            public ushort Manufacturer, Product;
            public uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            public uint Formats;
            public ushort Channels, Reserved;
            public uint Support;
        }

        [DllImport("winmm.dll")] static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint waveOutGetDevCapsW(UIntPtr id, out WaveCaps caps, uint size);
        [DllImport("winmm.dll")] static extern uint waveOutOpen(out IntPtr device, uint id, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern uint waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern uint waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern uint waveOutWrite(IntPtr device, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern uint waveOutReset(IntPtr device);
        [DllImport("winmm.dll")] static extern uint waveOutClose(IntPtr device);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint waveOutGetErrorTextW(uint error, System.Text.StringBuilder text, uint length);
        static readonly uint HeaderSize = (uint)Marshal.SizeOf(typeof(WaveHeader));

        public static IList<AudioDevice> GetDevices()
        {
            var result = new List<AudioDevice>();
            uint count = waveOutGetNumDevs();
            for (uint i = 0; i < count; i++)
            {
                WaveCaps caps;
                if (waveOutGetDevCapsW(new UIntPtr(i), out caps, (uint)Marshal.SizeOf(typeof(WaveCaps))) == 0)
                    result.Add(new AudioDevice((int)i, caps.Name));
            }
            return result;
        }

        public WaveOutPlayer(int deviceId)
        {
            if (deviceId < 0) throw new ArgumentOutOfRangeException("deviceId");
            var format = new WaveFormat { FormatTag = 1, Channels = 1, SamplesPerSec = 48000,
                AvgBytesPerSec = 96000, BlockAlign = 2, BitsPerSample = 16, ExtraSize = 0 };
            Check(waveOutOpen(out handle, (uint)deviceId, ref format, IntPtr.Zero, IntPtr.Zero, 0), "打开所选音频输出失败");
            try
            {
                for (int i = 0; i < SlotCount; i++)
                {
                    var slot = new Slot();
                    slots.Add(slot);
                    slot.Data = Marshal.AllocHGlobal(FrameBytes);
                    slot.Header = Marshal.AllocHGlobal((int)HeaderSize);
                    Marshal.StructureToPtr(new WaveHeader { Data = slot.Data, BufferLength = FrameBytes }, slot.Header, false);
                    Check(waveOutPrepareHeader(handle, slot.Header, HeaderSize), "准备音频缓冲失败");
                    slot.Prepared = true;
                }
            }
            catch { Dispose(); throw; }
        }

        public long Submitted { get { lock (sync) return submitted; } }
        public long Completed { get { lock (sync) { Collect(); return completed; } } }
        public long Dropped { get { lock (sync) return dropped; } }
        public int QueuedFrames { get { lock (sync) { Collect(); return CountQueued(); } } }

        void Collect()
        {
            if (disposed) return;
            foreach (Slot slot in slots)
            {
                if (slot.Queued && (((WaveHeader)Marshal.PtrToStructure(slot.Header, typeof(WaveHeader))).Flags & Done) != 0)
                { slot.Queued = false; completed++; }
            }
        }

        int CountQueued() { int n = 0; foreach (Slot slot in slots) if (slot.Queued) n++; return n; }

        public void Submit(byte[] samples)
        {
            if (samples == null || samples.Length != FrameBytes) throw new ArgumentException("A frame must contain exactly 1920 bytes.");
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("WaveOutPlayer");
                Collect();
                // Six frames = a strict 120 ms native-queue ceiling. Drop old audio
                // instead of allowing latency to grow when a device stalls.
                if (CountQueued() == SlotCount) ResetCore();
                foreach (Slot slot in slots)
                {
                    if (slot.Queued) continue;
                    Marshal.Copy(samples, 0, slot.Data, FrameBytes);
                    Check(waveOutWrite(handle, slot.Header, HeaderSize), "向所选音频设备写入失败");
                    slot.Queued = true;
                    submitted++;
                    return;
                }
            }
        }

        void ResetCore()
        {
            Collect();
            int count = CountQueued();
            Check(waveOutReset(handle), "清空音频缓冲失败");
            foreach (Slot slot in slots) slot.Queued = false;
            dropped += count;
        }

        public void Reset() { lock (sync) { if (!disposed) ResetCore(); } }

        static void Check(uint result, string context)
        {
            if (result == 0) return;
            var text = new System.Text.StringBuilder(256);
            waveOutGetErrorTextW(result, text, 256);
            throw new InvalidOperationException(context + " (" + result + "): " + text.ToString());
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                if (handle == IntPtr.Zero) return;
                waveOutReset(handle);
                bool allReleased = true;
                foreach (Slot slot in slots)
                {
                    uint result = 0;
                    if (slot.Prepared)
                    {
                        for (int retry = 0; retry < 100; retry++)
                        {
                            result = waveOutUnprepareHeader(handle, slot.Header, HeaderSize);
                            if (result != StillPlaying) break;
                            Thread.Sleep(5);
                        }
                    }
                    if (result == 0)
                    {
                        if (slot.Header != IntPtr.Zero) Marshal.FreeHGlobal(slot.Header);
                        if (slot.Data != IntPtr.Zero) Marshal.FreeHGlobal(slot.Data);
                        slot.Header = slot.Data = IntPtr.Zero;
                    }
                    else allReleased = false; // Retain native-owned memory if a faulty driver refuses to return it.
                }
                if (allReleased) { waveOutClose(handle); handle = IntPtr.Zero; }
            }
        }
    }
}
