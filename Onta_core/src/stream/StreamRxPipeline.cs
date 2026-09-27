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
    private readonly OpusDecoder _opus = new();
    private readonly List<Complex> _leftBuf = new(StreamConstants.SampleRate);
    private readonly List<Complex> _rightBuf = new(StreamConstants.SampleRate);
    private readonly StreamOfdmCodec _headerCodec = new(StreamModeId.Rate18k);
    private readonly Dictionary<StreamModeId, StreamOfdmCodec> _codecs = new();
    private StreamModeId? _modeId;
    private bool _synced;
    private bool _disposed;

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
        _leftBuf.AddRange(left);
        _rightBuf.AddRange(right);
        // バッファ肥大防止（最大約 8 秒）。同期中に捨てると境界がずれるので再同期させる
        const int max = StreamConstants.SampleRate * 8;
        if (_leftBuf.Count > max)
        {
            var drop = _leftBuf.Count - max;
            _leftBuf.RemoveRange(0, drop);
            _rightBuf.RemoveRange(0, drop);
            _synced = false;
        }
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
        if (_leftBuf.Count < _headerCodec.HeaderSectionSamples + TailMargin)
        {
            return pcmOut;
        }

        var left = _leftBuf.ToArray();
        var right = _rightBuf.ToArray();
        var power = BuildPowerPrefix(left, right);
        var cursor = 0;
        while (true)
        {
            if (!_synced)
            {
                var found = FindPacketStart(left, right, power, cursor, out var lastCandidate);
                if (found < 0)
                {
                    // 探し終えた範囲は捨て、次回はパケット先頭になり得る末尾から探す
                    cursor = Math.Max(cursor, lastCandidate);
                    break;
                }

                cursor = found;
                _synced = true;
            }

            if (cursor + RefineRadius + _headerCodec.HeaderSectionSamples + TailMargin > left.Length)
            {
                break;
            }

            // ヘッダーは数十サンプルずれても読めるが、データ部はずれに弱いのでプリアンブル終端へ合わせる
            cursor = RefinePacketStart(power, cursor, left.Length);

            if (!_headerCodec.TryDemodulateHeader(left, right, cursor, out var modeId))
            {
                _synced = false;
                status = "sync lost";
                cursor += 1;
                continue;
            }

            var codec = ResolveCodec(modeId);
            if (cursor + codec.PacketSamples + TailMargin > left.Length)
            {
                break;
            }

            if (!TryDemodulateNear(codec, left, right, cursor, out var next, out var packet))
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
                if (_opus.DecodeToPcm44100(frame, out var l, out var r) > 0)
                {
                    pcmOut.Add((l, r));
                }
            }
        }

        var consumed = Math.Min(cursor, _leftBuf.Count);
        if (consumed > 0)
        {
            _leftBuf.RemoveRange(0, consumed);
            _rightBuf.RemoveRange(0, consumed);
        }

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
    private int FindPacketStart(Complex[] left, Complex[] right, double[] power, int from, out int lastCandidate)
    {
        var preamble = StreamConstants.PreambleSamples;
        var headerSpan = _headerCodec.HeaderSectionSamples - preamble;
        var end = left.Length - _headerCodec.HeaderSectionSamples - TailMargin - RefineRadius;
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

            if (_headerCodec.TryDemodulateHeader(left, right, p, out _))
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
        var preamble = StreamConstants.PreambleSamples;
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

            List<Complex>? iqPoints = null;
            List<byte>? iqGroups = null;
            Action<Complex[], byte[], int>? onIq = null;
            if (report is not null)
            {
                iqPoints = new List<Complex>(DefaultIqPointsPerPacket);
                iqGroups = new List<byte>(DefaultIqPointsPerPacket);
                onIq = (symbols, groups, count) =>
                {
                    for (var i = 0; i < count; i++)
                    {
                        iqPoints.Add(symbols[i]);
                        iqGroups.Add(groups[i]);
                    }
                };
            }

            var ok = codec.TryDemodulatePacket(left, right, ref cursor, out var decoded, out var body, onIq)
                && decoded is not null;
            if (report is not null)
            {
                var attempt = new StreamRxPacketReport(ok, codec.Mode, body, iqPoints!.ToArray(), iqGroups!.ToArray());
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
    private static double[] BuildPowerPrefix(Complex[] left, Complex[] right)
    {
        var power = new double[left.Length + 1];
        for (var i = 0; i < left.Length; i++)
        {
            power[i + 1] = power[i] + (left[i].Real * left[i].Real) + (right[i].Real * right[i].Real);
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
            codec = new StreamOfdmCodec(modeId);
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
