namespace Onta.Stream.Opus;

/// <summary>
/// libopus デコーダの薄いラッパです（出力を 44.1 kHz へ簡易変換）。
/// </summary>
public sealed class OpusDecoder : IDisposable
{
    private readonly IntPtr _decoder;
    private readonly short[] _pcm48 = new short[OpusEncoder.FrameSamplesPerChannel * 2 * 2];
    private byte[] _packet = new byte[256];
    private bool _disposed;

    /// <summary>
    /// デコーダを生成します。
    /// </summary>
    public OpusDecoder()
    {
        if (!OpusNative.IsLibraryAvailable())
        {
            throw new DllNotFoundException(
                "opus.dll が見つかりません。Onta_core/native/opus/<rid>/opus.dll を配置してください。");
        }

        _decoder = OpusNative.opus_decoder_create(OpusEncoder.OpusSampleRate, 2, out var err);
        if (_decoder == IntPtr.Zero || err != OpusNative.Ok)
        {
            throw new InvalidOperationException($"opus_decoder_create failed: {err}");
        }
    }

    /// <summary>
    /// Opus パケットを 44.1 kHz ステレオ PCM へ復号します。
    /// </summary>
    /// <param name="packet">Opus パケット。</param>
    /// <param name="left">L 出力。</param>
    /// <param name="right">R 出力。</param>
    /// <returns>サンプル数（片チャネル）。</returns>
    public int DecodeToPcm44100(ReadOnlySpan<byte> packet, out double[] left, out double[] right)
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
        if (samples <= 0)
        {
            left = Array.Empty<double>();
            right = Array.Empty<double>();
            return 0;
        }

        // 48k → 44.1k 簡易デシメーション
        var outCount = (int)Math.Round(samples * (44100.0 / 48000.0));
        left = new double[outCount];
        right = new double[outCount];
        for (var i = 0; i < outCount; i++)
        {
            var src = Math.Min(samples - 1, (int)Math.Round(i * (48000.0 / 44100.0)));
            left[i] = _pcm48[src * 2] / 32768.0;
            right[i] = _pcm48[(src * 2) + 1] / 32768.0;
        }

        return outCount;
    }

    /// <inheritdoc />
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
