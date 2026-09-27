using System.Numerics;
using Onta.Stream.Opus;

namespace Onta.Stream;

/// <summary>
/// 再生（OFDM→パケット→Opus→PCM）の受信状態です。
/// </summary>
public sealed class StreamRxPipeline : IDisposable
{
    private readonly StreamMetaAssembler _meta = new();
    private readonly OpusDecoder _opus = new();
    private readonly List<Complex> _leftBuf = new(StreamConstants.SampleRate);
    private readonly List<Complex> _rightBuf = new(StreamConstants.SampleRate);
    private StreamOfdmCodec? _codec;
    private StreamModeId? _modeId;
    private bool _disposed;

    /// <summary>メタアセンブラ。</summary>
    public StreamMetaAssembler Meta => _meta;

    /// <summary>検出中の速度 ID。</summary>
    public StreamModeId? DetectedModeId => _modeId;

    /// <summary>
    /// キャプチャ PCM を追加します。
    /// </summary>
    public void PushCapture(Complex[] left, Complex[] right)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _leftBuf.AddRange(left);
        _rightBuf.AddRange(right);
        // バッファ肥大防止（最大約 8 秒）
        const int max = StreamConstants.SampleRate * 8;
        if (_leftBuf.Count > max)
        {
            var drop = _leftBuf.Count - max;
            _leftBuf.RemoveRange(0, drop);
            _rightBuf.RemoveRange(0, drop);
        }
    }

    /// <summary>
    /// バッファからパケットを可能な限り取り出し、PCM を返します。
    /// </summary>
    public List<(double[] Left, double[] Right)> Pump(out string? status)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        status = null;
        var pcmOut = new List<(double[] Left, double[] Right)>();
        if (_leftBuf.Count < StreamConstants.SampleRate / 5)
        {
            return pcmOut;
        }

        // モード未確定時は 01 で試し、ヘッダーが通れば切替
        _codec ??= new StreamOfdmCodec(StreamModeId.Rate18k);
        var left = _leftBuf.ToArray();
        var right = _rightBuf.ToArray();
        var cursor = 0;
        var consumed = 0;
        var attempts = 0;
        while (cursor + StreamConstants.SampleRate / 10 < left.Length && attempts < 8)
        {
            attempts++;
            var start = cursor;
            if (!_codec.TryDemodulatePacket(left, right, ref cursor, out var packet) || packet is null)
            {
                cursor = start + StreamConstants.PreambleSamples;
                if (cursor <= start)
                {
                    cursor = start + 64;
                }

                continue;
            }

            consumed = cursor;
            if (_modeId is null || _modeId.Value != packet.ModeId)
            {
                _modeId = packet.ModeId;
                _codec = new StreamOfdmCodec(packet.ModeId);
                status = $"mode={StreamMode.Resolve(packet.ModeId).DisplayKbps}kbps";
            }

            var reset = _meta.Ingest(
                packet.StreamId,
                packet.MetaKind,
                packet.MetaTotalBlocks,
                packet.MetaBlockIndex,
                packet.MetaData);
            if (reset)
            {
                status = "stream-id changed";
            }

            foreach (var frame in StreamOpusPayload.Unpack(packet.Payload))
            {
                var n = _opus.DecodeToPcm44100(frame, out var l, out var r);
                if (n > 0)
                {
                    pcmOut.Add((l, r));
                }
            }
        }

        if (consumed > 0)
        {
            _leftBuf.RemoveRange(0, Math.Min(consumed, _leftBuf.Count));
            _rightBuf.RemoveRange(0, Math.Min(consumed, _rightBuf.Count));
        }

        return pcmOut;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _opus.Dispose();
    }
}
