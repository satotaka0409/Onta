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
/// 受理した 1 パケット分の Opus フレーム（未復号）と、その直前に失われたフレーム数です。
/// </summary>
/// <param name="LostFrames">直前に失われたパケットの分として PLC で埋めるフレーム数。</param>
/// <param name="Frames">パケットが運ぶ Opus フレーム列。</param>
/// <param name="LagSeconds">受理した時点で、パケット末尾より後にすでに取り込まれていた入力の長さ（秒）。復調が入力に遅れている量。</param>
public sealed record StreamRxAudioPacket(int LostFrames, IReadOnlyList<byte[]> Frames, double LagSeconds = 0.0);

/// <summary>
/// 再生（OFDM→パケット→Opus→PCM）の受信状態です。
/// </summary>
/// <remarks>
/// パケット先頭をプリアンブル（無音）とヘッダーのパイロットで探して同期し、ヘッダーの速度 ID で
/// データ部のコーデックを選びます。パケット全体が揃うまで復調しないため、途中から再生しても受信できます。
/// テープ速度のずれはデータ部パイロットの位相から推定してパケットごとに補間で戻し、パケット内のワウもシンボル単位で追従します。
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

    /// <summary>速度補正した信号の先頭に置く余白（サンプル）。再試行のずらしとシンボル先頭探索の後戻り用。</summary>
    private const int WarpPad = 32;

    /// <summary>キャプチャバッファに溜める最大秒数。受信処理が一時的に遅れても入力を捨てずに追いつけるよう長めに取る。</summary>
    private const int MaxCaptureSeconds = 32;

    /// <summary>
    /// 速度補正の比（受信サンプル数 / 送信サンプル数）の 1 からの許容幅。
    /// テープ速度 ±4% は比で 1/1.04〜1/0.96（−3.8%〜+4.2%）になるので、それを含むよう広めに取る。
    /// </summary>
    private const double MaxSpeedDeviation = 0.045;

    /// <summary>未ロック時にヘッダーで総当たりする速度比の範囲と刻み（テープ速度 ±4% を含む）。</summary>
    private const double SpeedScanRange = 0.042;
    private const double SpeedScanStep = 0.001;

    /// <summary>パケット 1 つで測った速度の残差を推定へ反映する割合。</summary>
    private const double SpeedGain = 0.8;

    /// <summary>1 パケットで測った残差がこれを超えたら推定の誤りとみなし、使わない（ヘッダーは約 1% ずれても読めるため広めに取る）。</summary>
    private const double MaxSpeedResidual = 0.03;

    /// <summary>復調に失敗したとき、残差がこれ以上なら速度を直して再試行する。</summary>
    private const double MinRetrySpeedResidual = 5e-5;

    /// <summary>速度を直して再試行する最大回数。</summary>
    private const int MaxSpeedRetries = 3;

    /// <summary>シンボル間のずれを均す片側のシンボル数。</summary>
    private const int DriftSmoothRadius = 2;

    /// <summary>データ部のシンボル先頭探索半径。速度とワウはパイロットで戻すので探索せず、先頭の揺れで等化が乱れるのを防ぐ。</summary>
    private const int BodySymbolSearchRadius = 0;

    /// <summary>連続してこの回数失敗したら速度ロックを外し、総当たりをやり直す。</summary>
    private const int SpeedUnlockFailures = 4;

    /// <summary>Opus 1 フレームの長さ（秒）。</summary>
    private const double StreamOpusFrameSeconds = OpusEncoder.FrameSamplesPerChannel / (double)OpusEncoder.OpusSampleRate;

    /// <summary>直前のパケットからこの数を超えて間が空いたら、途切れとみなして PLC で埋めない。</summary>
    private const int MaxConcealPackets = 2;

    private readonly StreamMetaAssembler _meta = new();
    private readonly int _decodedSampleRate;
    private StreamPlayoutRegulator? _playout;
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
    private double _speed = 1.0;
    private bool _speedLocked;
    private int _speedFailures;
    private Complex[] _warpL = Array.Empty<Complex>();
    private Complex[] _warpR = Array.Empty<Complex>();
    private double[] _drift = Array.Empty<double>();
    private double[] _tau = Array.Empty<double>();
    private double[] _positions = Array.Empty<double>();
    private long _consumedTotal;
    private long _lastPacketStart = -1;
    private int _lastPacketFrames;
    private int _lastPacketSamples;
    private bool _disposed;

    /// <summary>
    /// サンプリング周波数を指定して受信パイプラインを構築します。
    /// </summary>
    /// <param name="sampleRate">受信PCMのサンプリング周波数。</param>
    /// <param name="decodedSampleRate">復号音声（Opus 出力）のサンプリング周波数。0 以下なら sampleRate と同じ。</param>
    public StreamRxPipeline(int sampleRate, int decodedSampleRate = 0)
    {
        _sampleRate = Math.Max(1, sampleRate);
        _preambleSamples = StreamConstants.PreambleSamples(_sampleRate);
        _decodedSampleRate = decodedSampleRate > 0 ? decodedSampleRate : _sampleRate;
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

    /// <summary>
    /// 速度が未ロックで、パケット先頭を速度の総当たりで探している間 true。
    /// 無音・雑音の中を探すこの状態は重く、入力の実時間に遅れることがある。
    /// </summary>
    public bool IsSearching => !_speedLocked;

    /// <summary>
    /// 取り込み済みで未処理の入力をすべて捨て、次の入力からパケット先頭を探し直します。
    /// </summary>
    public void DiscardCapture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Consume(_count);
        _synced = false;
    }

    /// <summary>推定した再生速度の偏差（+0.01 なら録音時より 1% 速く再生されている）。</summary>
    public double SpeedDeviation => (1.0 / _speed) - 1.0;

    /// <summary>ヘッダーが読めたパケットごとに（成否によらず）<see cref="Pump"/> のスレッドで呼ばれます。</summary>
    public Action<StreamRxPacketReport>? PacketReported { get; set; }

    /// <summary>
    /// キャプチャ PCM を追加します。
    /// </summary>
    /// <param name="left">L PCM。</param>
    /// <param name="right">R PCM。</param>
    public void PushCapture(Complex[] left, Complex[] right)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var n = Math.Min(left.Length, right.Length);
        EnsureCapacity(_count + n);
        Array.Copy(left, 0, _leftBuf, _count, n);
        Array.Copy(right, 0, _rightBuf, _count, n);
        _count += n;
        // バッファ肥大防止。同期中に捨てると境界がずれるので再同期させる
        var max = _sampleRate * MaxCaptureSeconds;
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
        _consumedTotal += samples;
    }

    /// <summary>
    /// バッファから揃ったパケットを可能な限り取り出し、復号した PCM を返します（失ったパケットは PLC で埋める）。
    /// </summary>
    /// <param name="status">速度検出・ストリーム切替・同期喪失などの状態文言（無ければ null）。</param>
    /// <returns>復号した Opus フレームごとの PCM。</returns>
    public List<(double[] Left, double[] Right)> Pump(out string? status)
    {
        var pcmOut = new List<(double[] Left, double[] Right)>();
        var packets = PumpPackets(out status);
        if (packets.Count == 0)
        {
            return pcmOut;
        }

        _playout ??= new StreamPlayoutRegulator(_decodedSampleRate);
        foreach (var packet in packets)
        {
            _playout.ConcealLost(packet.LostFrames, pcmOut);
            _playout.Decode(packet.Frames, pcmOut);
        }

        return pcmOut;
    }

    /// <summary>
    /// バッファから揃ったパケットを可能な限り取り出し、Opus フレームを復号せずに返します（復号は再生側のスレッドで行う）。
    /// </summary>
    /// <param name="status">速度検出・ストリーム切替・同期喪失などの状態文言（無ければ null）。</param>
    /// <returns>受理したパケットごとの Opus フレームと、直前に失われたフレーム数。</returns>
    public List<StreamRxAudioPacket> PumpPackets(out string? status)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        status = null;
        var audio = new List<StreamRxAudioPacket>();
        if (_count < _headerCodec.HeaderSectionSamples + TailMargin)
        {
            return audio;
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

            var speed = _speed;
            if (cursor + RefineRadius + RawSpan(_headerCodec.HeaderSectionSamples + TailMargin, speed) > length)
            {
                break;
            }

            // ヘッダーは数十サンプルずれても読めるが、データ部はずれに弱いのでプリアンブル終端へ合わせる
            var candidate = cursor;
            cursor = RefinePacketStart(power, candidate, length, speed);
            if (!TryHeaderAt(left, right, cursor, speed, length, out var modeId))
            {
                // 補正は候補より前へ動き得る。補正先から探し直すと同じ候補を見つけ続けて止まるので、候補位置で読み直し、だめなら候補の先から探す
                cursor = candidate;
                if (!TryHeaderAt(left, right, cursor, speed, length, out modeId))
                {
                    _synced = false;
                    status = "sync lost";
                    CountSpeedFailure();
                    cursor = candidate + 1;
                    continue;
                }
            }

            var codec = ResolveCodec(modeId);
            var warpCount = WarpPad + codec.PacketSamples + TailMargin;
            if (cursor + RawSpan(warpCount - WarpPad, speed) > length)
            {
                break;
            }

            var packetStart = _consumedTotal + cursor;

            // テープ速度のずれとパケット内のワウを戻した信号で復調する
            var ok = DemodulateWithSpeedTracking(codec, left, right, cursor, length, warpCount, ref speed, out var next, out var tauEnd, out var packet);
            if (!ok)
            {
                // ヘッダーが読めていればパケット長は分かるので、境界を保ったまま次へ進む
                PacketErrors++;
                status = "packet error";
                CountSpeedFailure();
                cursor += (int)Math.Round(codec.PacketSamples * speed) - RefineRadius;
                _synced = false;
                continue;
            }

            _speedLocked = true;
            _speedFailures = 0;
            cursor += (int)Math.Round((next - WarpPad + tauEnd) * speed);
            // 送信側はパケット間に無音を挟んで音声の実時間に合わせるので、次の先頭は改めて探す
            cursor = Math.Max(0, cursor - RefineRadius);
            _synced = false;
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

            var frames = StreamOpusPayload.Unpack(packet.Payload);
            var lagSeconds = Math.Max(0, length - cursor) / (double)_sampleRate;
            audio.Add(new StreamRxAudioPacket(CountLostFrames(packetStart, speed), frames, lagSeconds));
            _lastPacketStart = packetStart;
            _lastPacketFrames = frames.Count;
            _lastPacketSamples = codec.PacketSamples;
        }

        Consume(Math.Min(cursor, length));

        return audio;
    }

    /// <summary>
    /// 直前に受理したパケットとの間隔から、間で失われたパケットのフレーム数を求めます。
    /// 送信側はパケットの先頭間隔を運ぶ音声時間（フレーム数 × 20 ms）に揃えるので、間隔を割れば失った数が分かる。
    /// ただし運ぶ音声がパケットの送出時間より短いとき（速度切替直後など）は、送出時間より詰めては送れない。
    /// </summary>
    /// <param name="packetStart">今回のパケット先頭（受信開始からの通算サンプル位置）。</param>
    /// <param name="speed">受信サンプル数 / 送信サンプル数。</param>
    /// <returns>PLC で埋めるフレーム数。途切れが長すぎる・前のパケットが無いときは 0。</returns>
    private int CountLostFrames(long packetStart, double speed)
    {
        if (_lastPacketStart < 0 || _lastPacketFrames <= 0)
        {
            return 0;
        }

        var interval = Math.Max(_lastPacketFrames * StreamOpusFrameSeconds * _sampleRate, _lastPacketSamples) * speed;
        var missing = (int)Math.Round((packetStart - _lastPacketStart) / interval) - 1;
        return missing is >= 1 and <= MaxConcealPackets ? missing * _lastPacketFrames : 0;
    }

    /// <summary>
    /// パケット先頭（プリアンブル無音の直後にヘッダーが始まる位置）を探します。
    /// </summary>
    /// <remarks>電力で候補を絞ってから、ヘッダーのパイロット 3 バイトと速度 ID で確定します。</remarks>
    /// <param name="left">L PCM。</param>
    /// <param name="right">R PCM。</param>
    /// <param name="power">L²+R² の累積和。</param>
    /// <param name="from">探索開始位置。</param>
    /// <param name="length">バッファのサンプル数。</param>
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
        var end = length
            - RawSpan(_headerCodec.HeaderSectionSamples + TailMargin, 1.0 + MaxSpeedDeviation)
            - RefineRadius;
        lastCandidate = Math.Max(from, end + 1);
        if (end < from)
        {
            lastCandidate = from;
            return -1;
        }

        var nextScan = from;
        for (var p = from; p <= end; p += SyncStep)
        {
            var preamblePower = (power[p + preamble] - power[p]) / preamble;
            var headerPower = (power[p + preamble + headerSpan] - power[p + preamble]) / headerSpan;
            if (headerPower < MinSignalPower || preamblePower > headerPower * PreamblePowerRatio)
            {
                continue;
            }

            if (TryHeaderAt(left, right, p, _speed, length, out _))
            {
                return p;
            }

            // 速度が大きくずれているとヘッダーも読めないので、パケット先頭らしい位置で速度を総当たりする
            // 電力条件は真の先頭よりヘッダー長近く手前から通るので、先頭はヘッダー長の範囲まで先を探して決める
            if (!_speedLocked && p >= nextScan)
            {
                var refined = RefinePacketStart(power, p, length, 1.0, headerSpan);
                if (TryScanSpeed(left, right, power, refined, length, out var scanned))
                {
                    return scanned;
                }

                nextScan = refined + _headerCodec.HeaderSectionSamples;
            }
        }

        return -1;
    }

    /// <summary>
    /// 速度偏差を 0 から外側へ順に変えてヘッダー復調を試し、読めた速度を採用します。
    /// </summary>
    /// <param name="left">L PCM。</param>
    /// <param name="right">R PCM。</param>
    /// <param name="power">L²+R² の累積和。</param>
    /// <param name="candidate">パケット先頭の候補（速度ごとにプリアンブル電力で補正してから試す）。</param>
    /// <param name="length">バッファのサンプル数。</param>
    /// <param name="packetStart">読めた速度で補正したパケット先頭。</param>
    /// <returns>いずれかの速度でヘッダーが読めたら true。</returns>
    private bool TryScanSpeed(Complex[] left, Complex[] right, double[] power, int candidate, int length, out int packetStart)
    {
        packetStart = candidate;
        var steps = (int)Math.Round(SpeedScanRange / SpeedScanStep);
        for (var i = 0; i <= steps; i++)
        {
            for (var sign = 1; sign >= -1; sign -= 2)
            {
                if (i == 0 && sign < 0)
                {
                    continue;
                }

                var speed = 1.0 + (sign * i * SpeedScanStep);
                var start = RefinePacketStart(power, candidate, length, speed);
                if (TryHeaderAt(left, right, start, speed, length, out _))
                {
                    _speed = speed;
                    packetStart = start;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 速度補正した信号でヘッダーを復調します。
    /// </summary>
    /// <param name="left">L PCM。</param>
    /// <param name="right">R PCM。</param>
    /// <param name="packetStart">パケット先頭（受信サンプル位置）。</param>
    /// <param name="speed">受信サンプル数 / 送信サンプル数。</param>
    /// <param name="length">バッファのサンプル数。</param>
    /// <param name="modeId">成功時のストリーム速度 ID。</param>
    /// <returns>パイロットと速度 ID が妥当なら true。</returns>
    private bool TryHeaderAt(Complex[] left, Complex[] right, int packetStart, double speed, int length, out StreamModeId modeId)
    {
        modeId = default;
        var count = WarpPad + _headerCodec.HeaderSectionSamples + TailMargin;
        if (packetStart + RawSpan(count - WarpPad, speed) > length)
        {
            return false;
        }

        WarpInto(left, right, packetStart, speed, count, length);
        return _headerCodec.TryDemodulateHeader(_warpL, _warpR, WarpPad, out modeId, count);
    }

    /// <summary>
    /// パケット先頭から送信時のサンプル間隔に戻した信号を <see cref="_warpL"/> / <see cref="_warpR"/> へ作ります（先頭に <see cref="WarpPad"/> の余白）。
    /// </summary>
    /// <param name="left">L PCM。</param>
    /// <param name="right">R PCM。</param>
    /// <param name="packetStart">パケット先頭（受信サンプル位置）。</param>
    /// <param name="speed">受信サンプル数 / 送信サンプル数。</param>
    /// <param name="count">作るサンプル数（余白込み）。</param>
    /// <param name="length">バッファのサンプル数。</param>
    private void WarpInto(Complex[] left, Complex[] right, int packetStart, double speed, int count, int length)
    {
        if (_warpL.Length < count)
        {
            _warpL = new Complex[count];
            _warpR = new Complex[count];
        }

        var start = packetStart - (WarpPad * speed);
        StreamSpeedWarp.WarpStereo(left, right, length, start, speed, _warpL, _warpR, count);
    }

    /// <summary>
    /// 送信時 nominal サンプル分を読むのに必要な受信サンプル数（補間の片側タップ込み）を返します。
    /// </summary>
    /// <param name="nominal">送信時のサンプル数。</param>
    /// <param name="speed">受信サンプル数 / 送信サンプル数。</param>
    /// <returns>必要な受信サンプル数。</returns>
    private static int RawSpan(int nominal, double speed) =>
        (int)Math.Ceiling(nominal * speed) + StreamSpeedWarp.HalfTaps;

    /// <summary>
    /// 一定速度で補正した信号のパイロットから速度推定を更新し、パケット内のずれ（ワウ）も戻した信号で復調します。
    /// </summary>
    /// <remarks>推定し直した速度との差が大きいのに失敗したときは、速度を直して最大 <see cref="MaxSpeedRetries"/> 回やり直します。</remarks>
    /// <param name="codec">パケットのモードに対応するコーデック。</param>
    /// <param name="left">L PCM。</param>
    /// <param name="right">R PCM。</param>
    /// <param name="packetStart">パケット先頭（受信サンプル位置）。</param>
    /// <param name="length">バッファのサンプル数。</param>
    /// <param name="warpCount">補正信号のサンプル数（余白込み）。</param>
    /// <param name="speed">最後に使った速度（受信サンプル数 / 送信サンプル数）。</param>
    /// <param name="end">成功時の補正信号上のパケット末尾。</param>
    /// <param name="tauEnd">パケット末尾でのずれ（補正信号のサンプル）。受信位置へ戻すときに足す。</param>
    /// <param name="packet">成功時のパケット。</param>
    /// <returns>復調できたら true。</returns>
    private bool DemodulateWithSpeedTracking(
        StreamOfdmCodec codec,
        Complex[] left,
        Complex[] right,
        int packetStart,
        int length,
        int warpCount,
        ref double speed,
        out int end,
        out double tauEnd,
        out StreamPacket packet)
    {
        if (_drift.Length < codec.DataSymbolCount)
        {
            _drift = new double[codec.DataSymbolCount];
            _tau = new double[codec.DataSymbolCount + 1];
        }

        var ok = false;
        var initialSpeed = _speed;
        end = WarpPad;
        tauEnd = 0;
        packet = null!;
        for (var attempt = 0; attempt <= MaxSpeedRetries; attempt++)
        {
            speed = _speed;
            WarpInto(left, right, packetStart, speed, warpCount, length);
            var pairs = codec.MeasureBodyDrift(_warpL, _warpR, WarpPad, warpCount, _drift, out var meanDrift);
            var residual = UpdateSpeed(meanDrift, codec.DataSymbolSamples);
            tauEnd = pairs > 0
                ? WarpTracked(left, right, packetStart, speed, warpCount, length, codec, pairs, meanDrift)
                : 0;
            ok = TryDemodulateNear(codec, _warpL, _warpR, WarpPad, warpCount, out end, out _, out packet);
            if (ok || Math.Abs(residual) < MinRetrySpeedResidual)
            {
                break;
            }
        }

        // 復調できなかったパケット（ドロップアウトや途中で切れた信号）の測定で速度推定を乱さない
        if (!ok)
        {
            _speed = initialSpeed;
            speed = initialSpeed;
        }

        return ok;
    }

    /// <summary>
    /// パイロットで測った 1 シンボルあたりのずれから、速度推定を更新します。
    /// </summary>
    /// <param name="meanDrift">1 シンボルあたりのずれ（NaN なら更新しない）。</param>
    /// <param name="symbolSamples">1 シンボルのサンプル数。</param>
    /// <returns>測った速度の残差（反映前）。使えなければ 0。</returns>
    private double UpdateSpeed(double meanDrift, int symbolSamples)
    {
        if (double.IsNaN(meanDrift) || symbolSamples <= 0)
        {
            return 0;
        }

        // 補正後もシンボルが後ろへずれていく＝まだ速度を小さく見積もっている
        var residual = meanDrift / symbolSamples;
        if (Math.Abs(residual) > MaxSpeedResidual)
        {
            return 0;
        }

        _speed = Math.Clamp(_speed * (1.0 + (SpeedGain * residual)), 1.0 - MaxSpeedDeviation, 1.0 + MaxSpeedDeviation);
        return residual;
    }

    /// <summary>
    /// シンボル間のずれを積算した時間軸に沿って、パケットを補正信号へ取り出し直します（ヘッダー側は一定速度のまま）。
    /// </summary>
    /// <param name="left">L PCM。</param>
    /// <param name="right">R PCM。</param>
    /// <param name="packetStart">パケット先頭（受信サンプル位置）。</param>
    /// <param name="speed">一定速度の補正に使った速度。</param>
    /// <param name="count">補正信号のサンプル数（余白込み）。</param>
    /// <param name="length">バッファのサンプル数。</param>
    /// <param name="codec">パケットのモードに対応するコーデック。</param>
    /// <param name="pairs">測れたシンボル間の数。</param>
    /// <param name="meanDrift">全体の平均のずれ（シンボル間が測れなかった所の代わり）。</param>
    /// <returns>パケット末尾でのずれ（補正信号のサンプル）。</returns>
    private double WarpTracked(
        Complex[] left,
        Complex[] right,
        int packetStart,
        double speed,
        int count,
        int length,
        StreamOfdmCodec codec,
        int pairs,
        double meanDrift)
    {
        var fallback = double.IsNaN(meanDrift) ? 0.0 : meanDrift;
        _tau[0] = 0;
        for (var s = 0; s < pairs; s++)
        {
            // ワウは数 Hz まで、シンボルは約 160 Hz なので、前後数シンボルで均して雑音を抑える
            double sum = 0;
            var n = 0;
            for (var j = Math.Max(0, s - DriftSmoothRadius); j <= Math.Min(pairs - 1, s + DriftSmoothRadius); j++)
            {
                sum += double.IsNaN(_drift[j]) ? fallback : _drift[j];
                n++;
            }

            _tau[s + 1] = _tau[s] + (sum / n);
        }

        if (_positions.Length < count)
        {
            _positions = new double[count];
        }

        var symbolLength = codec.DataSymbolSamples;
        var firstCenter = WarpPad + codec.BodyOffset + (symbolLength / 2.0);
        var origin = packetStart - (WarpPad * speed);
        for (var m = 0; m < count; m++)
        {
            var x = (m - firstCenter) / symbolLength;
            double tau;
            if (x <= 0)
            {
                tau = 0;
            }
            else if (x >= pairs)
            {
                tau = _tau[pairs];
            }
            else
            {
                var i = (int)x;
                tau = _tau[i] + ((x - i) * (_tau[i + 1] - _tau[i]));
            }

            _positions[m] = origin + ((m + tau) * speed);
        }

        StreamSpeedWarp.WarpAtStereo(left, right, length, _positions, _warpL, _warpR, count);
        return _tau[pairs];
    }

    /// <summary>
    /// 復調失敗を数え、続いたら速度ロックを外します。
    /// </summary>
    private void CountSpeedFailure()
    {
        if (++_speedFailures >= SpeedUnlockFailures)
        {
            _speedLocked = false;
        }
    }

    /// <summary>
    /// パケット先頭の候補から ±<see cref="RefineRadius"/> の範囲で、「ヘッダー区間電力 − プリアンブル区間電力」が最大になる位置を返します。
    /// </summary>
    /// <remarks>ヘッダーは前後を無音のプリアンブルで挟まれているため、真の先頭でだけ最大になります。</remarks>
    /// <param name="power">L²+R² の累積和（長さ = サンプル数 + 1）。</param>
    /// <param name="start">パケット先頭の候補。</param>
    /// <param name="length">バッファのサンプル数。</param>
    /// <param name="speed">受信サンプル数 / 送信サンプル数。区間長をこの比で伸縮する（数 % ずれると先頭が十数サンプルずれるため）。</param>
    /// <param name="forwardRadius">候補より後ろを探す範囲（サンプル）。</param>
    /// <returns>補正後のパケット先頭。</returns>
    private int RefinePacketStart(double[] power, int start, int length, double speed, int forwardRadius = RefineRadius)
    {
        var preamble = (int)Math.Round(_preambleSamples * speed);
        var section = (int)Math.Round(_headerCodec.HeaderSectionSamples * speed);
        var from = Math.Max(0, start - RefineRadius);
        var to = Math.Min(length - section, start + forwardRadius);
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
    /// <param name="length">バッファのサンプル数。</param>
    /// <param name="end">成功時のパケット末尾。</param>
    /// <param name="usedStart">成功時に使ったパケット先頭（失敗時は start）。</param>
    /// <param name="packet">成功時のパケット。</param>
    /// <returns>いずれかの位置で復調できたら true。</returns>
    private bool TryDemodulateNear(
        StreamOfdmCodec codec,
        Complex[] left,
        Complex[] right,
        int start,
        int length,
        out int end,
        out int usedStart,
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
                usedStart = start + delta;
                packet = decoded!;
                return true;
            }
        }

        if (firstAttempt is not null)
        {
            report!(firstAttempt);
        }

        end = start;
        usedStart = start;
        packet = null!;
        return false;
    }

    /// <summary>
    /// L²+R² の累積和を作ります（区間電力を O(1) で求めるため）。
    /// </summary>
    /// <param name="length">累積するサンプル数。</param>
    /// <returns>長さ length+1 の累積和。先頭は 0。</returns>
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
    /// <param name="modeId">ストリーム速度 ID。</param>
    /// <returns>対応するコーデック。</returns>
    private StreamOfdmCodec ResolveCodec(StreamModeId modeId)
    {
        if (!_codecs.TryGetValue(modeId, out var codec))
        {
            codec = new StreamOfdmCodec(modeId, _sampleRate) { BodySymbolSearchRadius = BodySymbolSearchRadius };
            _codecs[modeId] = codec;
        }

        return codec;
    }

    /// <summary>
    /// Opus デコーダを破棄します。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _playout?.Dispose();
    }
}
