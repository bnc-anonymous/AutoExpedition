using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace AutoExpedition;

/// <summary>
/// Plays a PCM .wav held in memory through the Windows wave output, each sound on a device handle of its own, so two
/// sounds overlap. System.Media.SoundPlayer goes through PlaySound, which plays one sound per process: a second sound
/// cut the first off, as the instant loot success sound did the instant loot sound (2026-10-11). The buffer and header
/// are unmanaged, held until the device reports the buffer done, then released on a background task.
/// </summary>
internal static class WaveOutSound
{
    /// <summary>
    /// Starts the sound and returns at once; false when the bytes are not a PCM .wav or the device will not open, for
    /// the caller to fall back on.
    /// </summary>
    public static bool Play(byte[] wav)
    {
        if (!TryReadPcm(wav, out var format, out var dataStart, out var dataLength))
            return false;

        if (WaveOutOpen(out var device, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, CallbackNull) != 0)
            return false;

        var data = Marshal.AllocHGlobal(dataLength);
        var header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());

        Marshal.Copy(wav, dataStart, data, dataLength);
        Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = (uint)dataLength }, header, false);

        var headerSize = (uint)Marshal.SizeOf<WaveHeader>();

        if (WaveOutPrepareHeader(device, header, headerSize) != 0 || WaveOutWrite(device, header, headerSize) != 0)
        {
            WaveOutReset(device);
            WaveOutUnprepareHeader(device, header, headerSize);
            WaveOutClose(device);
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(data);

            return false;
        }

        // How long the sound lasts, and a margin, as the most to wait for the device to say it is done.
        var lastsMs = format.AverageBytesPerSecond > 0 ? dataLength * 1000L / format.AverageBytesPerSecond : 5000L;
        var flagsAt = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));

        Task.Run(async () =>
        {
            var giveUpAt = Environment.TickCount64 + lastsMs + 2000;

            while ((Marshal.ReadInt32(header, flagsAt) & WhdrDone) == 0 && Environment.TickCount64 < giveUpAt)
                await Task.Delay(50);

            WaveOutReset(device);
            WaveOutUnprepareHeader(device, header, headerSize);
            WaveOutClose(device);
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(data);
        });

        return true;
    }

    /// <summary>The format and data chunk of a RIFF WAVE in PCM; false for anything else.</summary>
    private static bool TryReadPcm(byte[] wav, out WaveFormat format, out int dataStart, out int dataLength)
    {
        format = default;
        dataStart = 0;
        dataLength = 0;

        if (wav.Length <= 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
            return false;

        var haveFormat = false;

        for (var at = 12; at + 8 <= wav.Length;)
        {
            var id = Encoding.ASCII.GetString(wav, at, 4);
            var size = BitConverter.ToInt32(wav, at + 4);
            var start = at + 8;

            if (size < 0 || start + size > wav.Length)
                break;

            if (id == "fmt " && size >= 16)
            {
                format = new WaveFormat
                {
                    FormatTag = BitConverter.ToUInt16(wav, start),
                    Channels = BitConverter.ToUInt16(wav, start + 2),
                    SamplesPerSecond = BitConverter.ToUInt32(wav, start + 4),
                    AverageBytesPerSecond = BitConverter.ToUInt32(wav, start + 8),
                    BlockAlign = BitConverter.ToUInt16(wav, start + 12),
                    BitsPerSample = BitConverter.ToUInt16(wav, start + 14),
                };
                haveFormat = format.FormatTag == 1;
            }
            else if (id == "data")
            {
                dataStart = start;
                dataLength = size;
            }

            at = start + size + (size & 1);
        }

        return haveFormat && dataLength > 0;
    }

    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint CallbackNull = 0;
    private const int WhdrDone = 0x1;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [DllImport("winmm.dll", EntryPoint = "waveOutOpen")]
    private static extern int WaveOutOpen(out IntPtr device, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll", EntryPoint = "waveOutPrepareHeader")]
    private static extern int WaveOutPrepareHeader(IntPtr device, IntPtr header, uint size);

    [DllImport("winmm.dll", EntryPoint = "waveOutWrite")]
    private static extern int WaveOutWrite(IntPtr device, IntPtr header, uint size);

    [DllImport("winmm.dll", EntryPoint = "waveOutReset")]
    private static extern int WaveOutReset(IntPtr device);

    [DllImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader")]
    private static extern int WaveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);

    [DllImport("winmm.dll", EntryPoint = "waveOutClose")]
    private static extern int WaveOutClose(IntPtr device);
}
