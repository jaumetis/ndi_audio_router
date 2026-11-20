using System.Runtime.InteropServices;

namespace NdiAudioRouter;

/// <summary>P/Invoke mínim cap a NDI SDK (v5/v6) per enviar àudio i vídeo.</summary>
internal static class Ndi
{
    const string Lib = "Processing.NDI.Lib.x64.dll";

    // NDI_LIB_FOURCC('F','L','T','p') -> àudio float 32 planar (la 'p' final és minúscula = 0x70)
    public const int FourCC_FLTP = 0x70544C46;
    public const int FourCC_BGRA = 0x41524742;   // 'B','G','R','A'

    [StructLayout(LayoutKind.Sequential)]
    public struct SendCreate
    {
        public IntPtr NdiName;
        public IntPtr Groups;
        [MarshalAs(UnmanagedType.U1)] public bool ClockVideo;
        [MarshalAs(UnmanagedType.U1)] public bool ClockAudio;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioFrameV3
    {
        public int SampleRate;
        public int NoChannels;
        public int NoSamples;
        public long Timecode;            // long.MaxValue = sintetitzat per l'SDK
        public int FourCC;
        public IntPtr Data;
        public int ChannelStrideInBytes;
        public IntPtr Metadata;
        public long Timestamp;           // long.MaxValue = indefinit
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioFrameV2
    {
        public int SampleRate;
        public int NoChannels;
        public int NoSamples;
        public long Timecode;
        public IntPtr Data;                // float* planar
        public int ChannelStrideInBytes;
        public IntPtr Metadata;
        public long Timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoFrameV2
    {
        public int Xres;
        public int Yres;
        public int FourCC;
        public int FrameRateN;
        public int FrameRateD;
        public float PictureAspectRatio;
        public int FrameFormatType;        // 1 = progressiu
        public long Timecode;
        public IntPtr Data;
        public int LineStrideInBytes;
        public IntPtr Metadata;
        public long Timestamp;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_send_send_audio_v2(IntPtr instance, ref AudioFrameV2 frame);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_send_send_video_v2(IntPtr instance, ref VideoFrameV2 frame);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int NDIlib_send_get_no_connections(IntPtr instance, uint timeoutMs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool NDIlib_initialize();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_destroy();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_send_create(ref SendCreate settings);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_send_destroy(IntPtr instance);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_send_send_audio_v3(IntPtr instance, ref AudioFrameV3 frame);

    public static void RegisterResolver()
    {
        NativeLibrary.SetDllImportResolver(typeof(Ndi).Assembly, (name, _, _) =>
        {
            if (name != Lib) return IntPtr.Zero;
            foreach (var v in new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5", "NDI_RUNTIME_DIR_V4" })
            {
                var dir = Environment.GetEnvironmentVariable(v);
                if (string.IsNullOrEmpty(dir)) continue;
                if (NativeLibrary.TryLoad(System.IO.Path.Combine(dir, Lib), out var h)) return h;
            }
            return IntPtr.Zero; // cerca per defecte (carpeta de l'aplicació)
        });
    }
}
