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

    /// <summary>
    /// libopus を呼び出してエンコーダを生成します。
    /// </summary>
    /// <param name="Fs">サンプリング周波数（Hz）。</param>
    /// <param name="channels">チャネル数。</param>
    /// <param name="application">用途（音声・VoIP など）。</param>
    /// <param name="error">生成結果のエラーコード。</param>
    /// <returns>エンコーダのポインタ。失敗時は <see cref="IntPtr.Zero"/>。</returns>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr opus_encoder_create(int Fs, int channels, int application, out int error);

    /// <summary>
    /// libopus を呼び出してエンコーダを破棄します。
    /// </summary>
    /// <param name="encoder">破棄するエンコーダ。</param>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void opus_encoder_destroy(IntPtr encoder);

    /// <summary>
    /// libopus を呼び出して PCM を Opus パケットへ符号化します。
    /// </summary>
    /// <param name="encoder">エンコーダ。</param>
    /// <param name="pcm">インターリーブされた 16 ビット PCM。</param>
    /// <param name="frame_size">片チャネルのサンプル数。</param>
    /// <param name="data">符号化結果の書き込み先。</param>
    /// <param name="max_data_bytes">書き込み先の最大バイト数。</param>
    /// <returns>書き込んだバイト数。失敗時は負のエラーコード。</returns>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_encode(
        IntPtr encoder,
        short[] pcm,
        int frame_size,
        byte[] data,
        int max_data_bytes);

    /// <summary>
    /// libopus を呼び出してエンコーダへ制御要求を送ります。
    /// </summary>
    /// <param name="encoder">エンコーダ。</param>
    /// <param name="request">制御要求 ID。</param>
    /// <param name="value">要求に渡す値。</param>
    /// <returns>成功時は 0。失敗時は負のエラーコード。</returns>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_encoder_ctl(IntPtr encoder, int request, int value);

    /// <summary>
    /// libopus を呼び出してデコーダを生成します。
    /// </summary>
    /// <param name="Fs">サンプリング周波数（Hz）。</param>
    /// <param name="channels">チャネル数。</param>
    /// <param name="error">生成結果のエラーコード。</param>
    /// <returns>デコーダのポインタ。失敗時は <see cref="IntPtr.Zero"/>。</returns>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr opus_decoder_create(int Fs, int channels, out int error);

    /// <summary>
    /// libopus を呼び出してデコーダを破棄します。
    /// </summary>
    /// <param name="decoder">破棄するデコーダ。</param>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void opus_decoder_destroy(IntPtr decoder);

    /// <summary>
    /// libopus を呼び出して Opus パケットを PCM へ復号します。
    /// </summary>
    /// <param name="decoder">デコーダ。</param>
    /// <param name="data">Opus パケット。無ければ null。</param>
    /// <param name="len">パケット長。</param>
    /// <param name="pcm">復号 PCM の書き込み先。</param>
    /// <param name="frame_size">片チャネルの最大サンプル数。</param>
    /// <param name="decode_fec">前方誤り訂正を復号するとき 1。</param>
    /// <returns>復号した片チャネルのサンプル数。失敗時は負のエラーコード。</returns>
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
    /// <returns>読み込めてエンコーダを生成できれば true。</returns>
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
