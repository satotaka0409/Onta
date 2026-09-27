using System.Runtime.InteropServices;

namespace Onta.Stream.Opus;

/// <summary>
/// native libopus の P/Invoke 定義です。実行ディレクトリの opus.dll を読みます。
/// </summary>
internal static class OpusNative
{
    private const string DllName = "opus";

    public const int Ok = 0;
    public const int ApplicationAudio = 2049;
    public const int ApplicationVoip = 2048;
    public const int SetBitrateRequest = 4002;
    public const int SetVbrRequest = 4006;
    public const int SetComplexityRequest = 4010;
    public const int SetSignalRequest = 4024;
    public const int SignalMusic = 3002;

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr opus_encoder_create(int Fs, int channels, int application, out int error);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void opus_encoder_destroy(IntPtr encoder);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_encode(
        IntPtr encoder,
        short[] pcm,
        int frame_size,
        byte[] data,
        int max_data_bytes);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_encoder_ctl(IntPtr encoder, int request, int value);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr opus_decoder_create(int Fs, int channels, out int error);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void opus_decoder_destroy(IntPtr decoder);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_decode(
        IntPtr decoder,
        byte[]? data,
        int len,
        short[] pcm,
        int frame_size,
        int decode_fec);

    /// <summary>
    /// opus.dll の有無を軽く確認します。
    /// </summary>
    public static bool IsLibraryAvailable()
    {
        try
        {
            var enc = opus_encoder_create(48000, 2, ApplicationAudio, out var err);
            if (enc != IntPtr.Zero)
            {
                opus_encoder_destroy(enc);
            }

            return err == Ok || enc != IntPtr.Zero;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}
