using Onta.Core;

namespace Onta.Stream.Opus;

/// <summary>
/// libopus デコーダの薄いラッパです（出力を指定 sampleRate へ変換）。
/// </summary>
public sealed class OpusDecoder : IDisposable
{
    private readonly IntPtr _decoder;
    private readonly int _outputSampleRate;
    private readonly short[] _pcm48 = new short[OpusEncoder.FrameSamplesPerChannel * 2 * 2];
    private byte[] _packet = new byte[256];
    private bool _disposed;

    /// <summary>
    /// デコーダを生成します。
    /// </summary>
    /// <param name="outputSampleRate">出力PCMのサンプリング周波数。</param>
    public OpusDecoder(int outputSampleRate)
    {
        if (!OpusNative.IsLibraryAvailable())
        {
            throw new DllNotFoundException(
                CoreText.OpusDllNotFound);
        }

        _outputSampleRate = Math.Max(1, outputSampleRate);
        _decoder = OpusNative.opus_decoder_create(OpusEncoder.OpusSampleRate, 2, out var err);
        if (_decoder == IntPtr.Zero || err != OpusNative.Ok)
        {
            throw new InvalidOperationException($"opus_decoder_create failed: {err}");
        }
    }

    /// <summary>
    /// Opus パケットを指定 sampleRate のステレオ PCM へ復号します。
    /// </summary>
    /// <param name="packet">Opus パケット。</param>
    /// <param name="left">L 出力。</param>
    /// <param name="right">R 出力。</param>
    /// <returns>サンプル数（片チャネル）。</returns>
    public int DecodeToPcm(ReadOnlySpan<byte> packet, out double[] left, out double[] right)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_packet.Length < packet.Length)
        {
            _packet = new byte[packet.Length];
        }

        packet.CopyTo(_packet);
        var samples = OpusNative.opus_decode(
            _decoder,
            _packet,
            packet.Length,
            _pcm48,
            OpusEncoder.FrameSamplesPerChannel * 2,
            0);
        return ConvertDecoded(samples, out left, out right);
    }

    /// <summary>
    /// パケットが無い 1 フレーム（20 ms）分を、libopus のパケット損失補償（PLC）で直前の音声から補って取り出します。
    /// </summary>
    /// <param name="left">L 出力。</param>
    /// <param name="right">R 出力。</param>
    /// <returns>サンプル数（片チャネル）。</returns>
    public int ConcealToPcm(out double[] left, out double[] right)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var samples = OpusNative.opus_decode(
            _decoder,
            null,
            0,
            _pcm48,
            OpusEncoder.FrameSamplesPerChannel,
            0);
        return ConvertDecoded(samples, out left, out right);
    }

    /// <summary>
    /// 48 kHz で復号したインターリーブ PCM を、出力サンプリング周波数の L/R 配列へ変換します。
    /// </summary>
    /// <param name="samples">opus_decode の戻り値（片チャネルのサンプル数。負はエラー）。</param>
    /// <param name="left">L 出力。</param>
    /// <param name="right">R 出力。</param>
    /// <returns>サンプル数（片チャネル）。</returns>
    private int ConvertDecoded(int samples, out double[] left, out double[] right)
    {
        if (samples <= 0)
        {
            left = Array.Empty<double>();
            right = Array.Empty<double>();
            return 0;
        }

        // 48k → 出力 sampleRate へ簡易変換
        var outCount = (int)Math.Round(samples * (_outputSampleRate / (double)OpusEncoder.OpusSampleRate));
        outCount = Math.Max(1, outCount);
        left = new double[outCount];
        right = new double[outCount];
        for (var i = 0; i < outCount; i++)
        {
            var src = Math.Min(samples - 1, (int)Math.Round(i * (OpusEncoder.OpusSampleRate / (double)_outputSampleRate)));
            left[i] = _pcm48[src * 2] / 32768.0;
            right[i] = _pcm48[(src * 2) + 1] / 32768.0;
        }

        return outCount;
    }

    /// <summary>
    /// libopus デコーダを破棄します。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_decoder != IntPtr.Zero)
        {
            OpusNative.opus_decoder_destroy(_decoder);
        }
    }
}
