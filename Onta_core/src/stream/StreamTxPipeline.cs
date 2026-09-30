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
    private long _carriedOpusSamples;
    private bool _disposed;

    /// <summary>
    /// パイプラインを構築します。
    /// </summary>
    /// <param name="modeId">ストリーム速度 ID。</param>
    /// <param name="title">曲タイトル。</param>
    /// <param name="artist">アーティスト。</param>
    /// <param name="coverBytes">ジャケ写。無ければ null。</param>
    /// <param name="sampleRate">入力 PCM のサンプリング周波数（Hz）。</param>
    /// <param name="streamId">ストリーム ID。0 なら乱数で決めます。</param>
    public StreamTxPipeline(StreamModeId modeId, string title, string artist, byte[]? coverBytes, int sampleRate, ushort streamId = 0)
    {
        _codec = new StreamOfdmCodec(modeId, sampleRate);
        _meta = new StreamMetaRotator(title, artist, coverBytes);
        _opus = new OpusEncoder(_codec.Mode.OpusBitrateBps, sampleRate);
        _streamId = streamId != 0 ? streamId : (ushort)Random.Shared.Next(1, ushort.MaxValue);
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
    /// <param name="observer">L/R 周波数ビン通知。</param>
    /// <param name="stride">何シンボルごとに通知するか（1=毎シンボル）。</param>
    public void AttachTxSpectrumObserver(OfdmGenerator.TxSpectrumHandler observer, int stride = 1) =>
        _codec.AttachTxSpectrumObserver(observer, stride);

    /// <summary>
    /// 送信スペクトル監視を外します。
    /// </summary>
    public void ClearTxSpectrumObserver() => _codec.ClearTxSpectrumObserver();

    /// <summary>
    /// PCM を取り込み、変調済み OFDM チャンクがあれば返します。
    /// </summary>
    /// <param name="left">L PCM。</param>
    /// <param name="right">R PCM。</param>
    /// <returns>変調済み OFDM チャンク。ペイロードが満杯でなければ空。</returns>
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
    /// <remarks>
    /// パケットの送出時間は運ぶ音声より短いため、パケット末尾に無音を足して累計の送出時間を音声時間へ揃える
    /// （揃えないと受信側で音声が実時間より速く溜まり、再生が破綻する）。
    /// </remarks>
    /// <returns>変調済み OFDM チャンク（末尾の無音を含む）。</returns>
    private List<(Complex[] Left, Complex[] Right)> ModulateQueuedPayloads()
    {
        var result = new List<(Complex[] Left, Complex[] Right)>(_payloadQueue.Count);
        foreach (var payload in _payloadQueue)
        {
            _carriedOpusSamples += (long)StreamOpusPayload.Unpack(payload).Count * OpusEncoder.FrameSamplesPerChannel;

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
            var carried = _carriedOpusSamples * _codec.SampleRate / OpusEncoder.OpusSampleRate;
            var gap = carried - (_sampleOffset + l.Length);
            if (gap > 0)
            {
                Array.Resize(ref l, l.Length + (int)gap);
                Array.Resize(ref r, r.Length + (int)gap);
            }

            _sampleOffset += l.Length;
            result.Add((l, r));
        }

        _payloadQueue.Clear();
        return result;
    }

    /// <summary>
    /// Opus エンコーダを破棄します。
    /// </summary>
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
