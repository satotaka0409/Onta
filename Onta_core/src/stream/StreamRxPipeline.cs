using System.Numerics;
using Onta.Stream.Opus;

namespace Onta.Stream;

/// <summary>
/// 1 パケット分の受信結果（画面のエラー率・I-Q グラフ用）です。
/// </summary>
/// <param name="Success">CRC まで通って受理できたか。</param>
/// <param name="Mode">ヘッダーから求めた速度モード。</param>
/// <param name="Body">曲情報／データ部のビタビ中間訂正率。ビタビ復号まで届かなかった場合は null。</param>
/// <param name="IqPoints">曲情報／データ部の等化後シンボル（L → R の順）。</param>
/// <param name="IqGroups">各点のサブキャリアグループ（0=A .. 7=H）。</param>
public sealed record StreamRxPacketReport(
    bool Success,
    StreamModeInfo Mode,
    StreamBodyDiagnostics? Body,
    Complex[] IqPoints,
    byte[] IqGroups);

/// <summary>
/// 再生（OFDM→パケット→Opus→PCM）の受信状態です。
/// </summary>
/// <remarks>
/// パケット先頭をプリアンブル（無音）とヘッダーのパイロットで探して同期し、ヘッダーの速度 ID で
/// データ部のコーデックを選びます。パケット全体が揃うまで復調しないため、途中から再生しても受信できます。
/// </remarks>
public sealed class StreamRxPipeline : IDisposable
{
    /// <summary>同期探索の刻み（サンプル）。ヘッダー復調側のシンボル先頭探索（±16）で残りを吸収する。</summary>
    private const int SyncStep = 16;

    /// <summary>パケット先頭をプリアンブル電力で補正する探索半径（サンプル）。同期探索の刻みより広く取る。</summary>
    private const int RefineRadius = 24;

    /// <summary>1 パケットの I-Q 点数の目安（初期容量）。</summary>
    private const int DefaultIqPointsPerPacket = 4096;

    /// <summary>データ部の復調に失敗したときに試すパケット先頭のずらし量（サンプル）。</summary>
    private static readonly int[] RetryOffsets = { 0, 1, -1, 2, -2 };

    /// <summary>復調時のシンボル先頭探索が末尾を越えないよう確保する余白（サンプル）。</summary>
    private const int TailMargin = 64;

    /// <summary>ヘッダー区間の平均電力がこれ未満なら無信号とみなし、同期候補にしない。</summary>
    private const double MinSignalPower = 1e-8;

    /// <summary>プリアンブル区間の電力がヘッダー区間の何倍以下なら同期候補とするか。</summary>
    private const double PreamblePowerRatio = 0.25;

    private readonly StreamMetaAssembler _meta = new();
    private readonly OpusDecoder _opus;
    private readonly int _sampleRate;
    private readonly int _preambleSamples;
    private readonly List<Complex> _iqPoints = new(DefaultIqPointsPerPacket);
    private readonly List<byte> _iqGroups = new(DefaultIqPointsPerPacket);
    private Complex[] _leftBuf;
    private Complex[] _rightBuf;
    private double[] _power;
    private int _count;
    private readonly StreamOfdmCodec _headerCodec;
    private readonly Dictionary<StreamModeId, StreamOfdmCodec> _codecs = new();
    private StreamModeId? _modeId;
    private bool _synced;
    private bool _disposed;

    /// <summary>
    /// サンプリング周波数を指定して受信パイプラインを構築します。
    /// </summary>
    /// <param name="sampleRate">受信PCMのサンプリング周波数。</param>
    public StreamRxPipeline(int sampleRate)
    {
        _sampleRate = Math.Max(1, sampleRate);
        _preambleSamples = StreamConstants.PreambleSamples(_sampleRate);
        _opus = new OpusDecoder(_sampleRate);
        _leftBuf = new Complex[_sampleRate];
        _rightBuf = new Complex[_sampleRate];
        _power = new double[_sampleRate + 1];
        _headerCodec = new StreamOfdmCodec(StreamModeId.Rate18k, _sampleRate);
    }

    /// <summary>メタアセンブラ。</summary>
    public StreamMetaAssembler Meta => _meta;

    /// <summary>検出中の速度 ID。</summary>
    public StreamModeId? DetectedModeId => _modeId;

    /// <summary>受信に成功したパケット数。</summary>
    public int PacketsReceived { get; private set; }

    /// <summary>ヘッダーは読めたがデータ部の復調に失敗したパケット数。</summary>
    public int PacketErrors { get; private set; }

    /// <summary>ヘッダーが読めたパケットごとに（成否によらず）<see cref="Pump"/> のスレッドで呼ばれます。</summary>
    public Action<StreamRxPacketReport>? PacketReported { get; set; }

    /// <summary>
    /// キャプチャ PCM を追加します。
    /// </summary>
    public void PushCapture(Complex[] left, Complex[] right)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var n = Math.Min(left.Length, right.Length);
        EnsureCapacity(_count + n);
        Array.Copy(left, 0, _leftBuf, _count, n);
        Array.Copy(right, 0, _rightBuf, _count, n);
        _count += n;
        // バッファ肥大防止（最大約 8 秒）。同期中に捨てると境界がずれるので再同期させる
        var max = _sampleRate * 8;
        if (_count > max)
        {
            Consume(_count - max);
            _synced = false;
        }
    }

    /// <summary>
    /// キャプチャバッファの容量を確保します。
    /// </summary>
    /// <param name="needed">必要なサンプル数。</param>
    private void EnsureCapacity(int needed)
    {
        if (_leftBuf.Length >= needed)
        {
            return;
        }

        var cap = _leftBuf.Length;
        while (cap < needed)
        {
            cap *= 2;
        }

        Array.Resize(ref _leftBuf, cap);
        Array.Resize(ref _rightBuf, cap);
    }

    /// <summary>
    /// バッファ先頭のサンプルを捨て、残りを前へ詰めます。
    /// </summary>
    /// <param name="samples">捨てるサンプル数。</param>
    private void Consume(int samples)
    {
        if (samples <= 0)
        {
            return;
        }

        if (samples > _count)
        {
            samples = _count;
        }

        var remain = _count - samples;
        if (remain > 0)
        {
            Array.Copy(_leftBuf, samples, _leftBuf, 0, remain);
            Array.Copy(_rightBuf, samples, _rightBuf, 0, remain);
        }

        _count = remain;
    }

    /// <summary>
    /// バッファから揃ったパケットを可能な限り取り出し、復号した PCM を返します。
    /// </summary>
    /// <param name="status">速度検出・ストリーム切替・同期喪失などの状態文言（無ければ null）。</param>
    /// <returns>復号した Opus フレームごとの PCM。</returns>
    public List<(double[] Left, double[] Right)> Pump(out string? status)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        status = null;
        var pcmOut = new List<(double[] Left, double[] Right)>();
        if (_count < _headerCodec.HeaderSectionSamples + TailMargin)
        {
            return pcmOut;
        }

        var left = _leftBuf;
        var right = _rightBuf;
        var length = _count;
        var power = BuildPowerPrefix(length);
        var cursor = 0;
        while (true)
        {
            if (!_synced)
            {
                var found = FindPacketStart(left, right, power, cursor, length, out var lastCandidate);
                if (found < 0)
                {
                    // 探し終えた範囲は捨て、次回はパケット先頭になり得る末尾から探す
                    cursor = Math.Max(cursor, lastCandidate);
                    break;
                }

                cursor = found;
                _synced = true;
            }

            if (cursor + RefineRadius + _headerCodec.HeaderSectionSamples + TailMargin > length)
            {
                break;
            }

            // ヘッダーは数十サンプルずれても読めるが、データ部はずれに弱いのでプリアンブル終端へ合わせる
            cursor = RefinePacketStart(power, cursor, length);

            if (!_headerCodec.TryDemodulateHeader(left, right, cursor, out var modeId, length))
            {
                _synced = false;
                status = "sync lost";
                cursor += 1;
                continue;
            }

            var codec = ResolveCodec(modeId);
            if (cursor + codec.PacketSamples + TailMargin > length)
            {
                break;
            }

            if (!TryDemodulateNear(codec, left, right, cursor, length, out var next, out var packet))
            {
                // ヘッダーが読めていればパケット長は分かるので、境界を保ったまま次へ進む
                PacketErrors++;
                status = "packet error";
                cursor += codec.PacketSamples;
                continue;
            }

            cursor = next;
            PacketsReceived++;
            if (_modeId != packet.ModeId)
            {
                _modeId = packet.ModeId;
                status = $"mode={StreamMode.Resolve(packet.ModeId).DisplayKbps}kbps";
            }

            if (_meta.Ingest(
                    packet.StreamId,
                    packet.MetaKind,
                    packet.MetaTotalBlocks,
                    packet.MetaBlockIndex,
                    packet.MetaData))
            {
                status = "stream-id changed";
            }

            foreach (var frame in StreamOpusPayload.Unpack(packet.Payload))
            {
                if (_opus.DecodeToPcm(frame, out var l, out var r) > 0)
                {
                    pcmOut.Add((l, r));
                }
            }
        }

        Consume(Math.Min(cursor, length));

        return pcmOut;
    }

    /// <summary>
    /// パケット先頭（プリアンブル無音の直後にヘッダーが始まる位置）を探します。
    /// </summary>
    /// <remarks>電力で候補を絞ってから、ヘッダーのパイロット 3 バイトと速度 ID で確定します。</remarks>
    /// <param name="left">L PCM。</param>
    /// <param name="right">R PCM。</param>
    /// <param name="from">探索開始位置。</param>
    /// <param name="lastCandidate">見つからなかった場合に次回の探索を始める位置。</param>
    /// <returns>見つかったパケット先頭。無ければ -1。</returns>
    private int FindPacketStart(
        Complex[] left,
        Complex[] right,
        double[] power,
        int from,
        int length,
        out int lastCandidate)
    {
        var preamble = _preambleSamples;
        var headerSpan = _headerCodec.HeaderSectionSamples - preamble;
        var end = length - _headerCodec.HeaderSectionSamples - TailMargin - RefineRadius;
        lastCandidate = Math.Max(from, end + 1);
        if (end < from)
        {
            lastCandidate = from;
            return -1;
        }

        for (var p = from; p <= end; p += SyncStep)
        {
            var preamblePower = (power[p + preamble] - power[p]) / preamble;
            var headerPower = (power[p + preamble + headerSpan] - power[p + preamble]) / headerSpan;
            if (headerPower < MinSignalPower || preamblePower > headerPower * PreamblePowerRatio)
            {
                continue;
            }

            if (_headerCodec.TryDemodulateHeader(left, right, p, out _, length))
            {
                return p;
            }
        }

        return -1;
    }

    /// <summary>
    /// パケット先頭の候補から ±<see cref="RefineRadius"/> の範囲で、「ヘッダー区間電力 − プリアンブル区間電力」が最大になる位置を返します。
    /// </summary>
    /// <remarks>ヘッダーは前後を無音のプリアンブルで挟まれているため、真の先頭でだけ最大になります。</remarks>
    /// <param name="power">L²+R² の累積和（長さ = サンプル数 + 1）。</param>
    /// <param name="start">パケット先頭の候補。</param>
    /// <param name="length">バッファのサンプル数。</param>
    /// <returns>補正後のパケット先頭。</returns>
    private int RefinePacketStart(double[] power, int start, int length)
    {
        var preamble = _preambleSamples;
        var section = _headerCodec.HeaderSectionSamples;
        var from = Math.Max(0, start - RefineRadius);
        var to = Math.Min(length - section, start + RefineRadius);
        var best = start;
        var bestScore = double.NegativeInfinity;
        for (var p = from; p <= to; p++)
        {
            var preamblePower = power[p + preamble] - power[p];
            var headerPower = power[p + section] - power[p + preamble];
            var score = headerPower - preamblePower;
            if (score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }

        return best;
    }

    /// <summary>
    /// パケット先頭で復調し、失敗したら先頭を数サンプル前後させて再試行します。
    /// </summary>
    /// <remarks>
    /// <see cref="PacketReported"/> へは 1 パケットにつき 1 回だけ報告します
    /// （成功時はその試行、全滅時は補正位置そのままの初回試行の I-Q・訂正率）。
    /// </remarks>
    /// <param name="codec">パケットのモードに対応するコーデック。</param>
    /// <param name="left">L PCM。</param>
    /// <param name="right">R PCM。</param>
    /// <param name="start">パケット先頭。</param>
    /// <param name="end">成功時のパケット末尾。</param>
    /// <param name="packet">成功時のパケット。</param>
    /// <returns>いずれかの位置で復調できたら true。</returns>
    private bool TryDemodulateNear(
        StreamOfdmCodec codec,
        Complex[] left,
        Complex[] right,
        int start,
        int length,
        out int end,
        out StreamPacket packet)
    {
        var report = PacketReported;
        StreamRxPacketReport? firstAttempt = null;
        foreach (var delta in RetryOffsets)
        {
            var cursor = start + delta;
            if (cursor < 0)
            {
                continue;
            }

            Action<Complex[], byte[], int>? onIq = null;
            if (report is not null)
            {
                _iqPoints.Clear();
                _iqGroups.Clear();
                onIq = (symbols, groups, count) =>
                {
                    for (var i = 0; i < count; i++)
                    {
                        _iqPoints.Add(symbols[i]);
                        _iqGroups.Add(groups[i]);
                    }
                };
            }

            var ok = codec.TryDemodulatePacket(left, right, ref cursor, out var decoded, out var body, onIq, length)
                && decoded is not null;
            if (report is not null)
            {
                var attempt = new StreamRxPacketReport(ok, codec.Mode, body, _iqPoints.ToArray(), _iqGroups.ToArray());
                if (ok)
                {
                    report(attempt);
                }
                else
                {
                    firstAttempt ??= attempt;
                }
            }

            if (ok)
            {
                end = cursor;
                packet = decoded!;
                return true;
            }
        }

        if (firstAttempt is not null)
        {
            report!(firstAttempt);
        }

        end = start;
        packet = null!;
        return false;
    }

    /// <summary>
    /// L²+R² の累積和を作ります（区間電力を O(1) で求めるため）。
    /// </summary>
    private double[] BuildPowerPrefix(int length)
    {
        if (_power.Length < length + 1)
        {
            _power = new double[length + 1];
        }

        var power = _power;
        power[0] = 0;
        for (var i = 0; i < length; i++)
        {
            var l = _leftBuf[i].Real;
            var r = _rightBuf[i].Real;
            power[i + 1] = power[i] + (l * l) + (r * r);
        }

        return power;
    }

    /// <summary>
    /// 速度 ID に対応するコーデックを返します（初回だけ生成してキャッシュ）。
    /// </summary>
    private StreamOfdmCodec ResolveCodec(StreamModeId modeId)
    {
        if (!_codecs.TryGetValue(modeId, out var codec))
        {
            codec = new StreamOfdmCodec(modeId, _sampleRate);
            _codecs[modeId] = codec;
        }

        return codec;
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
