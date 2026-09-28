namespace Onta.Stream.Opus;

/// <summary>
/// libopus エンコーダの薄いラッパです（入力 sampleRate → 内部 48 kHz）。
/// </summary>
public sealed class OpusEncoder : IDisposable
{
    /// <summary>Opus サンプルレート。</summary>
    public const int OpusSampleRate = 48000;

    /// <summary>20ms フレーム（48kHz・片チャネル）。</summary>
    public const int FrameSamplesPerChannel = 960;

    private readonly IntPtr _encoder;
    private readonly short[] _frame = new short[FrameSamplesPerChannel * 2];
    private readonly byte[] _packet = new byte[4000];
    private short[] _leftBuf = new short[FrameSamplesPerChannel * 4];
    private short[] _rightBuf = new short[FrameSamplesPerChannel * 4];
    private int _buffered;
    private readonly int _inputSampleRate;
    private bool _disposed;

    /// <summary>
    /// エンコーダを生成します。
    /// </summary>
    /// <param name="bitrateBps">目標ビットレート。</param>
    /// <param name="inputSampleRate">入力PCMのサンプリング周波数。</param>
    public OpusEncoder(int bitrateBps, int inputSampleRate)
    {
        if (!OpusNative.IsLibraryAvailable())
        {
            throw new DllNotFoundException(
                "opus.dll が見つかりません。Onta_core/native/opus/<rid>/opus.dll を配置してください。");
        }

        _inputSampleRate = Math.Max(1, inputSampleRate);
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
    /// sampleRate 指定のステレオ PCM を追加し、完成した Opus パケットを <paramref name="packets"/> へ追加します。
    /// </summary>
    /// <param name="left">L PCM。</param>
    /// <param name="right">R PCM。</param>
    /// <param name="packets">完成した Opus パケットの追加先。</param>
    public void EncodePcm(ReadOnlySpan<double> left, ReadOnlySpan<double> right, List<byte[]> packets)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var n = Math.Min(left.Length, right.Length);
        if (n <= 0)
        {
            return;
        }

        // 線形補間で入力 sampleRate → 48k
        var ratio = OpusSampleRate / (double)_inputSampleRate;
        var outCount = (int)Math.Ceiling(n * ratio);
        for (var o = 0; o < outCount; o++)
        {
            var srcPos = o / ratio;
            var i0 = Math.Min(n - 1, (int)srcPos);
            var i1 = Math.Min(n - 1, i0 + 1);
            var t = srcPos - i0;
            var l = (left[i0] * (1.0 - t)) + (left[i1] * t);
            var r = (right[i0] * (1.0 - t)) + (right[i1] * t);
            Append(ToShort(l), ToShort(r));
        }

        while (_buffered >= FrameSamplesPerChannel)
        {
            for (var s = 0; s < FrameSamplesPerChannel; s++)
            {
                _frame[s * 2] = _leftBuf[s];
                _frame[(s * 2) + 1] = _rightBuf[s];
            }

            var remain = _buffered - FrameSamplesPerChannel;
            if (remain > 0)
            {
                Array.Copy(_leftBuf, FrameSamplesPerChannel, _leftBuf, 0, remain);
                Array.Copy(_rightBuf, FrameSamplesPerChannel, _rightBuf, 0, remain);
            }

            _buffered = remain;

            var len = OpusNative.opus_encode(_encoder, _frame, FrameSamplesPerChannel, _packet, _packet.Length);
            if (len > 0)
            {
                var exact = new byte[len];
                Buffer.BlockCopy(_packet, 0, exact, 0, len);
                packets.Add(exact);
            }
        }
    }

    /// <summary>
    /// 48 kHz の 1 サンプルを左右バッファへ追加します。
    /// </summary>
    /// <param name="left">L サンプル。</param>
    /// <param name="right">R サンプル。</param>
    private void Append(short left, short right)
    {
        if (_buffered == _leftBuf.Length)
        {
            var grown = _leftBuf.Length * 2;
            Array.Resize(ref _leftBuf, grown);
            Array.Resize(ref _rightBuf, grown);
        }

        _leftBuf[_buffered] = left;
        _rightBuf[_buffered] = right;
        _buffered++;
    }

    /// <summary>
    /// 実数サンプルを 16 ビット PCM へ変換します。
    /// </summary>
    /// <param name="sample">-1.0〜1.0 付近のサンプル。</param>
    /// <returns>32767 倍して short 範囲へ収めた値。</returns>
    private static short ToShort(double sample)
    {
        var v = sample * 32767.0;
        return (short)Math.Clamp(v, short.MinValue, short.MaxValue);
    }

    /// <summary>
    /// libopus エンコーダを破棄します。
    /// </summary>
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
