namespace Onta.Stream.Opus;

/// <summary>
/// libopus エンコーダの薄いラッパです（入力 44.1 kHz → 内部 48 kHz）。
/// </summary>
public sealed class OpusEncoder : IDisposable
{
    /// <summary>Opus サンプルレート。</summary>
    public const int OpusSampleRate = 48000;

    /// <summary>20ms フレーム（48kHz・片チャネル）。</summary>
    public const int FrameSamplesPerChannel = 960;

    private readonly IntPtr _encoder;
    private readonly short[] _frame = new short[FrameSamplesPerChannel * 2];
    private readonly List<short> _leftBuf = new(FrameSamplesPerChannel * 2);
    private readonly List<short> _rightBuf = new(FrameSamplesPerChannel * 2);
    private bool _disposed;

    /// <summary>
    /// エンコーダを生成します。
    /// </summary>
    /// <param name="bitrateBps">目標ビットレート。</param>
    public OpusEncoder(int bitrateBps)
    {
        if (!OpusNative.IsLibraryAvailable())
        {
            throw new DllNotFoundException(
                "opus.dll が見つかりません。Onta_core/native/opus/<rid>/opus.dll を配置してください。");
        }

        var bitrate = Math.Clamp(bitrateBps, 8000, 128000);
        _encoder = OpusNative.opus_encoder_create(OpusSampleRate, 2, OpusNative.ApplicationAudio, out var err);
        if (_encoder == IntPtr.Zero || err != OpusNative.Ok)
        {
            throw new InvalidOperationException($"opus_encoder_create failed: {err}");
        }

        OpusNative.opus_encoder_ctl(_encoder, OpusNative.SetBitrateRequest, bitrate);
        // テープへの連続送出はパケット容量が固定なので、VBR の超過で実時間から遅れないよう CBR にする。
        OpusNative.opus_encoder_ctl(_encoder, OpusNative.SetVbrRequest, 0);
        OpusNative.opus_encoder_ctl(_encoder, OpusNative.SetComplexityRequest, 5);
        OpusNative.opus_encoder_ctl(_encoder, OpusNative.SetSignalRequest, OpusNative.SignalMusic);
    }

    /// <summary>
    /// 44.1 kHz ステレオ PCM を追加し、完成した Opus パケットを <paramref name="packets"/> へ追加します。
    /// </summary>
    public void EncodePcm44100(ReadOnlySpan<double> left, ReadOnlySpan<double> right, List<byte[]> packets)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var n = Math.Min(left.Length, right.Length);
        if (n <= 0)
        {
            return;
        }

        // 線形補間で 44.1k → 48k
        const double ratio = 48000.0 / 44100.0;
        var outCount = (int)Math.Ceiling(n * ratio);
        for (var o = 0; o < outCount; o++)
        {
            var srcPos = o / ratio;
            var i0 = Math.Min(n - 1, (int)srcPos);
            var i1 = Math.Min(n - 1, i0 + 1);
            var t = srcPos - i0;
            var l = (left[i0] * (1.0 - t)) + (left[i1] * t);
            var r = (right[i0] * (1.0 - t)) + (right[i1] * t);
            _leftBuf.Add(ToShort(l));
            _rightBuf.Add(ToShort(r));
        }

        while (_leftBuf.Count >= FrameSamplesPerChannel)
        {
            for (var s = 0; s < FrameSamplesPerChannel; s++)
            {
                _frame[s * 2] = _leftBuf[s];
                _frame[(s * 2) + 1] = _rightBuf[s];
            }

            _leftBuf.RemoveRange(0, FrameSamplesPerChannel);
            _rightBuf.RemoveRange(0, FrameSamplesPerChannel);

            var packet = new byte[4000];
            var len = OpusNative.opus_encode(_encoder, _frame, FrameSamplesPerChannel, packet, packet.Length);
            if (len > 0)
            {
                var exact = new byte[len];
                Buffer.BlockCopy(packet, 0, exact, 0, len);
                packets.Add(exact);
            }
        }
    }

    private static short ToShort(double sample)
    {
        var v = sample * 32767.0;
        return (short)Math.Clamp(v, short.MinValue, short.MaxValue);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_encoder != IntPtr.Zero)
        {
            OpusNative.opus_encoder_destroy(_encoder);
        }
    }
}
