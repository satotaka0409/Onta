using System.Numerics;
using Onta.Core;
using Onta.Stream.Opus;

namespace Onta.Stream;

/// <summary>
/// 録音（PCM→Opus→パケット→OFDM）の連続生成です。
/// </summary>
public sealed class StreamTxPipeline : IDisposable
{
    private readonly StreamOfdmCodec _codec;
    private readonly StreamMetaRotator _meta;
    private readonly OpusEncoder _opus;
    private readonly ushort _streamId;
    private readonly List<byte[]> _opusQueue = new();
    private readonly List<byte[]> _payloadQueue = new();
    private readonly StreamOpusPayloadPacker _packer = new();
    private long _sampleOffset;
    private bool _disposed;

    /// <summary>
    /// パイプラインを構築します。
    /// </summary>
    public StreamTxPipeline(StreamModeId modeId, string title, string artist, byte[]? coverBytes, int sampleRate)
    {
        _codec = new StreamOfdmCodec(modeId, sampleRate);
        _meta = new StreamMetaRotator(title, artist, coverBytes);
        _opus = new OpusEncoder(_codec.Mode.OpusBitrateBps, sampleRate);
        _streamId = (ushort)Random.Shared.Next(1, ushort.MaxValue);
    }

    /// <summary>ストリーム ID。</summary>
    public ushort StreamId => _streamId;

    /// <summary>モード。</summary>
    public StreamModeInfo Mode => _codec.Mode;

    /// <summary>変復調のサンプリング周波数（Hz）。</summary>
    public int SampleRate => _codec.SampleRate;

    /// <summary>
    /// 送信スペクトル監視を取り付けます（FFT 表示用）。
    /// </summary>
    public void AttachTxSpectrumObserver(OfdmGenerator.TxSpectrumHandler observer, int stride = 1) =>
        _codec.AttachTxSpectrumObserver(observer, stride);

    /// <summary>
    /// 送信スペクトル監視を外します。
    /// </summary>
    public void ClearTxSpectrumObserver() => _codec.ClearTxSpectrumObserver();

    /// <summary>
    /// PCM を取り込み、変調済み OFDM チャンクがあれば返します。
    /// </summary>
    public List<(Complex[] Left, Complex[] Right)> PushPcm(ReadOnlySpan<double> left, ReadOnlySpan<double> right)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _opus.EncodePcm(left, right, _opusQueue);
        foreach (var frame in _opusQueue)
        {
            _packer.Add(frame, _payloadQueue);
        }

        _opusQueue.Clear();
        return ModulateQueuedPayloads();
    }

    /// <summary>
    /// 詰めかけの Opus ペイロードを送出します（入力終端で 1 回呼ぶ）。
    /// </summary>
    /// <returns>変調済み OFDM チャンク（未送出データが無ければ空）。</returns>
    public List<(Complex[] Left, Complex[] Right)> Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var partial = _packer.FlushPartial();
        if (partial is not null)
        {
            _payloadQueue.Add(partial);
        }

        return ModulateQueuedPayloads();
    }

    /// <summary>
    /// 確定済みペイロードを曲情報と組み合わせてパケット化し、OFDM 変調します。
    /// </summary>
    /// <returns>変調済み OFDM チャンク。</returns>
    private List<(Complex[] Left, Complex[] Right)> ModulateQueuedPayloads()
    {
        var result = new List<(Complex[] Left, Complex[] Right)>(_payloadQueue.Count);
        foreach (var payload in _payloadQueue)
        {
            var (kind, total, index, data) = _meta.Next();
            var packet = new StreamPacket
            {
                ModeId = _codec.Mode.Id,
                StreamId = _streamId,
                MetaKind = kind,
                MetaTotalBlocks = total,
                MetaBlockIndex = index,
                MetaData = data,
                Payload = payload,
            };
            var (l, r) = _codec.ModulatePacket(packet, _sampleOffset);
            _sampleOffset += l.Length;
            result.Add((l, r));
        }

        _payloadQueue.Clear();
        return result;
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
