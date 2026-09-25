using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;

namespace Onta.Core;

public sealed partial class FileWavCodec
{
    /// <summary>
    /// CreateHeaderOfdm は、インスタンスまたはバッファを生成します。
    /// </summary>
    /// <param name="carrierGrid">搬送波グリッド（SC-8 族／SC-24 族）。</param>
    /// <returns>OfdmGenerator。</returns>
    private OfdmGenerator CreateHeaderOfdm(OfdmCarrierGrid? carrierGrid = null)
    {
        var headerBins = OfdmConfig.ResolveConceptualLeftBins(16);
        var grid = carrierGrid ?? OfdmConfig.ResolveCarrierGrid(_profile.ActiveSubcarriers);
        var fftSize = OfdmConfig.ResolveFftSize(_profile.ActiveSubcarriers, _profile.ChannelMode);
        var config = new OfdmConfig(
            fftSize: fftSize,
            activeSubcarriers: headerBins.Length,
            cyclicPrefixLength: _profile.HeaderCyclicPrefixLength,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Qpsk,
            channelMode: ChannelMode.Mono,
            pilotSpacing: 8,
            stereoFrequencyShiftBins: _profile.StereoFrequencyShiftBins,
            sampleRate: _profile.SampleRate,
            randomSeed: _profile.RandomSeed,
            conceptualLeftBins: headerBins,
            carrierGrid: grid);

        return new OfdmGenerator(config);
    }

    /// <summary>
    /// AlternateCarrierGrid は、代替値へ切り替えます。
    /// </summary>
    /// <param name="grid">grid。</param>
    /// <returns>OfdmCarrierGrid。</returns>
    private static OfdmCarrierGrid AlternateCarrierGrid(OfdmCarrierGrid grid) =>
        grid == OfdmCarrierGrid.Sc8Family ? OfdmCarrierGrid.Sc24Family : OfdmCarrierGrid.Sc8Family;

    /// <summary>
    /// DecodeHeaderPacketSyncedTryingGrids は、復号します。
    /// </summary>
    /// <param name="headerOfdm">ヘッダー用 OFDM 生成器。</param>
    /// <param name="leftSamples">L チャネル PCM。</param>
    /// <param name="rightSamples">R チャネル PCM。</param>
    /// <param name="warpedCursor">ワウ補正後カーソル（更新あり）。</param>
    /// <param name="logicalOffset">論理サンプルオフセット（更新あり）。</param>
    /// <param name="payloadLength">ペイロード長（バイト）。</param>
    /// <param name="expectedPilot">期待パイロットパターン。</param>
    /// <param name="searchRadius">探索半径（サンプル）。</param>
    /// <param name="onSyncProgress">同期進捗コールバック。</param>
    /// <param name="statusBoard">実行状態ボード。</param>
    /// <param name="frameKind">フレーム種別（FH/BH 等）。</param>
    /// <returns>結果の配列またはスライス。</returns>
    private byte[] DecodeHeaderPacketSyncedTryingGrids(
        ref OfdmGenerator headerOfdm,
        Complex[] leftSamples,
        Complex[] rightSamples,
        ref int warpedCursor,
        ref long logicalOffset,
        int payloadLength,
        byte[]? expectedPilot,
        int searchRadius,
        Action<int, int>? onSyncProgress = null,
        CoreExecutionStatusBoard? statusBoard = null,
        CoreFrameKind frameKind = CoreFrameKind.Fh)
    {
        var savedCursor = warpedCursor;
        var savedLogical = logicalOffset;
        try
        {
            return DecodeHeaderPacketSynced(
                leftSamples,
                rightSamples,
                ref warpedCursor,
                ref logicalOffset,
                headerOfdm,
                payloadLength,
                expectedPilot,
                searchRadius,
                onSyncProgress,
                statusBoard,
                frameKind);
        }
        catch (InvalidDataException)
        {
            warpedCursor = savedCursor;
            logicalOffset = savedLogical;
            headerOfdm = CreateHeaderOfdm(AlternateCarrierGrid(headerOfdm.CarrierGrid));
            return DecodeHeaderPacketSynced(
                leftSamples,
                rightSamples,
                ref warpedCursor,
                ref logicalOffset,
                headerOfdm,
                payloadLength,
                expectedPilot,
                searchRadius,
                onSyncProgress,
                statusBoard,
                frameKind);
        }
    }

    /// <summary>
    /// AppendFileHeaderPacket は、追記します。
    /// </summary>
    /// <param name="leftPcm">L チャネル PCM。</param>
    /// <param name="rightPcm">R チャネル PCM。</param>
    /// <param name="headerOfdm">ヘッダー用 OFDM 生成器。</param>
    /// <param name="fileHeader">ファイルヘッダーバイト列。</param>
    private void AppendFileHeaderPacket(
        List<Complex> leftPcm,
        List<Complex> rightPcm,
        OfdmGenerator headerOfdm,
        byte[] fileHeader)
    {
        AppendHeaderPackets(
            leftPcm,
            rightPcm,
            headerOfdm,
            fileHeader,
            fileHeader,
            _profile.FileHeaderUnmodulatedSamples);
    }

    /// <summary>
    /// AppendHeaderPackets は、追記します。
    /// </summary>
    /// <param name="leftPcm">L チャネル PCM。</param>
    /// <param name="rightPcm">R チャネル PCM。</param>
    /// <param name="ofdm">OFDM 生成器。</param>
    /// <param name="leftHeaderBytes">L ヘッダーバイト列。</param>
    /// <param name="rightHeaderBytes">R ヘッダーバイト列。</param>
    /// <param name="unmodulatedSamples">無変調サンプル数。</param>
    private void AppendHeaderPackets(
        List<Complex> leftPcm,
        List<Complex> rightPcm,
        OfdmGenerator ofdm,
        byte[] leftHeaderBytes,
        byte[] rightHeaderBytes,
        int unmodulatedSamples)
    {
        _ = rightHeaderBytes;
        if (unmodulatedSamples > 0)
        {
            var unmodulated = ofdm.GenerateUnmodulated(unmodulatedSamples);
            AppendHeaderPair(leftPcm, rightPcm, unmodulated);
        }

        var leftBits = BytesToBitsMsb(
            ConvolutionalCode.Encode(
                ApplyReedSolomon(leftHeaderBytes),
                terminate: true,
                punctureRate: HeaderPunctureRate));

        var modulated = ofdm.ModulateBits(leftBits, leftPcm.Count);
        AppendHeaderPair(leftPcm, rightPcm, modulated);
    }

    /// <summary>
    /// AppendHeaderPair は、追記します。
    /// </summary>
    /// <param name="leftPcm">L チャネル PCM。</param>
    /// <param name="rightPcm">R チャネル PCM。</param>
    /// <param name="pair">L/R サンプル対。</param>
    private void AppendHeaderPair(List<Complex> leftPcm, List<Complex> rightPcm, (Complex[] Left, Complex[] Right) pair)
    {
        AppendPair(leftPcm, rightPcm, pair);
        if (_profile.ChannelMode == ChannelMode.Stereo && pair.Right.Length == 0)
        {
            rightPcm.AddRange(pair.Left);
        }
    }

    /// <summary>
    /// HeaderUnmodulatedSamplesFor の結果を返します。
    /// </summary>
    /// <param name="packetLength">packetLength。</param>
    /// <returns>計算した整数値。</returns>
    private int HeaderUnmodulatedSamplesFor(int packetLength) =>
        packetLength == FileHeaderBytes
            ? _profile.FileHeaderUnmodulatedSamples
            : _profile.BlockHeaderUnmodulatedSamples;

    /// <summary>
    /// SkipHeaderUnmodulatedPreamble は、読み飛ばします。
    /// </summary>
    /// <param name="samples">入力サンプル列。</param>
    /// <param name="warpedCursor">ワウ補正後カーソル（更新あり）。</param>
    /// <param name="logicalOffset">論理サンプルオフセット（更新あり）。</param>
    /// <param name="unmodulatedSamples">無変調サンプル数。</param>
    /// <param name="statusBoard">実行状態ボード。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    private static void SkipHeaderUnmodulatedPreamble(
        Complex[] samples,
        ref int warpedCursor,
        ref long logicalOffset,
        int unmodulatedSamples,
        CoreExecutionStatusBoard? statusBoard = null,
        int sampleRate = 44100)
    {
        if (unmodulatedSamples <= 0)
        {
            return;
        }

        // 無変調区間でも FFT が止まって見えないよう、短いチャンクで進めつつ可視化する。
        var remaining = unmodulatedSamples;
        var chunk = Math.Max(ReceiveVizFftSize / 8, Math.Max(1, sampleRate / 20));
        Complex[]? window = null;
        Complex[]? work = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (remaining > 0)
        {
            var n = Math.Min(remaining, chunk);
            warpedCursor = SkipSamples(samples, warpedCursor, n);
            logicalOffset += n;
            remaining -= n;
            if (statusBoard is null)
            {
                continue;
            }

            if (remaining > 0 && clock.ElapsedMilliseconds < 33)
            {
                continue;
            }

            window ??= new Complex[ReceiveVizFftSize];
            work ??= new Complex[ReceiveVizFftSize];
            clock.Restart();
            PublishReceivePcmFft(
                statusBoard,
                samples,
                Array.Empty<Complex>(),
                warpedCursor,
                stereo: false,
                sampleRate,
                window,
                work);
        }
    }

    /// <summary>
    /// DecodeHeaderPacketSynced は、復号します。
    /// </summary>
    /// <param name="leftSamples">L チャネル PCM。</param>
    /// <param name="rightSamples">R チャネル PCM。</param>
    /// <param name="warpedCursor">ワウ補正後カーソル（更新あり）。</param>
    /// <param name="logicalOffset">論理サンプルオフセット（更新あり）。</param>
    /// <param name="ofdm">OFDM 生成器。</param>
    /// <param name="payloadLength">ペイロード長（バイト）。</param>
    /// <param name="expectedPilot">期待パイロットパターン。</param>
    /// <param name="searchRadius">探索半径（サンプル）。</param>
    /// <param name="onSyncProgress">同期進捗コールバック。</param>
    /// <param name="statusBoard">実行状態ボード。</param>
    /// <param name="frameKind">フレーム種別（FH/BH 等）。</param>
    /// <returns>結果の配列またはスライス。</returns>
    private static byte[] DecodeHeaderPacketSynced(
        Complex[] leftSamples,
        Complex[] rightSamples,
        ref int warpedCursor,
        ref long logicalOffset,
        OfdmGenerator ofdm,
        int payloadLength,
        byte[]? expectedPilot,
        int searchRadius,
        Action<int, int>? onSyncProgress = null,
        CoreExecutionStatusBoard? statusBoard = null,
        CoreFrameKind frameKind = CoreFrameKind.Fh)
    {
        var rsByteLength = GetReedSolomonEncodedLength(payloadLength);
        var convByteLength = GetConvolutionalEncodedLength(rsByteLength, HeaderPunctureRate);
        var bitCount = convByteLength * 8;
        var stereoSplit = false;
        var channelBitCount = stereoSplit ? (bitCount + 1) / 2 : bitCount;
        var sampleCount = ofdm.SampleCountForBitCount(channelBitCount);
        var symbolLength = ofdm.SamplesPerOfdmSymbol;
        var probeSymbols = Math.Clamp(sampleCount / symbolLength, 1, 4);

        Complex[]? fftWindow = null;
        Complex[]? fftWork = null;
        var fftClock = System.Diagnostics.Stopwatch.StartNew();
        void MaybePublishHeaderFft(int sampleEnd, bool force = false)
        {
            if (statusBoard is null)
            {
                return;
            }

            if (!force && fftClock.ElapsedMilliseconds < 33)
            {
                return;
            }

            fftWindow ??= new Complex[ReceiveVizFftSize];
            fftWork ??= new Complex[ReceiveVizFftSize];
            fftClock.Restart();
            PublishReceivePcmFft(
                statusBoard,
                leftSamples,
                rightSamples,
                sampleEnd,
                stereo: false,
                ofdm.SampleRate,
                fftWindow,
                fftWork);
        }

        onSyncProgress?.Invoke(0, 1);
        MaybePublishHeaderFft(warpedCursor, force: true);
        if (TryDecodeHeaderAt(
                leftSamples,
                rightSamples,
                warpedCursor,
                logicalOffset,
                ofdm,
                bitCount,
                channelBitCount,
                sampleCount,
                payloadLength,
                rsByteLength,
                expectedPilot,
                stereoSplit,
                perSymbolSearchRadius: 0,
                out var exactPayload,
                out var exactEnd,
                out _,
                statusBoard,
                frameKind))
        {
            warpedCursor = exactEnd;
            logicalOffset += sampleCount;
            MaybePublishHeaderFft(warpedCursor, force: true);
            onSyncProgress?.Invoke(1, 1);
            return exactPayload;
        }

        var candidateStarts = CollectSyncCandidates(
            leftSamples,
            warpedCursor,
            sampleCount,
            searchRadius,
            ofdm,
            probeSymbols,
            useRightChannel: false,
            onProbe: start => MaybePublishHeaderFft(start));

        Exception? lastError = null;
        for (var i = 0; i < candidateStarts.Count; i++)
        {
            var start = candidateStarts[i];
            onSyncProgress?.Invoke(i + 1, Math.Max(1, candidateStarts.Count));
            MaybePublishHeaderFft(start);
            if (start == warpedCursor)
            {
                continue;
            }

            try
            {
                if (TryDecodeHeaderAt(
                        leftSamples,
                        rightSamples,
                        start,
                        logicalOffset,
                        ofdm,
                        bitCount,
                        channelBitCount,
                        sampleCount,
                        payloadLength,
                        rsByteLength,
                        expectedPilot,
                        stereoSplit,
                        perSymbolSearchRadius: Math.Min(2, Math.Max(0, symbolLength / 16)),
                        out var payload,
                        out var endCursor,
                        out _,
                        statusBoard,
                        frameKind))
                {
                    warpedCursor = endCursor;
                    logicalOffset += sampleCount;
                    MaybePublishHeaderFft(warpedCursor, force: true);
                    return payload;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidDataException(
            $"ヘッダーの復号に失敗しました。sample={warpedCursor}, payload={payloadLength}。"
            + "プリアンブル長のずれ、またはワウ・ノイズが大きい可能性があります。",
            lastError);
    }

    /// <summary>
    /// 指定位置からヘッダー復号を試行します。
    /// </summary>
    /// <param name="leftSamples">L チャネル PCM。</param>
    /// <param name="rightSamples">R チャネル PCM。</param>
    /// <param name="start">開始位置。</param>
    /// <param name="logicalOffset">logicalOffset。</param>
    /// <param name="ofdm">ofdm。</param>
    /// <param name="totalBitCount">totalBitCount。</param>
    /// <param name="channelBitCount">channelBitCount。</param>
    /// <param name="sampleCount">sampleCount。</param>
    /// <param name="payloadLength">payloadLength。</param>
    /// <param name="rsByteLength">rsByteLength。</param>
    /// <param name="expectedPilot">expectedPilot。</param>
    /// <param name="stereoSplit">stereoSplit。</param>
    /// <param name="perSymbolSearchRadius">perSymbolSearchRadius。</param>
    /// <param name="payload">payload。</param>
    /// <param name="endCursor">endCursor。</param>
    /// <returns>成功または条件成立時 true。</returns>
    private static bool TryDecodeHeaderAt(
        Complex[] leftSamples,
        Complex[] rightSamples,
        int start,
        long logicalOffset,
        OfdmGenerator ofdm,
        int totalBitCount,
        int channelBitCount,
        int sampleCount,
        int payloadLength,
        int rsByteLength,
        byte[]? expectedPilot,
        bool stereoSplit,
        int perSymbolSearchRadius,
        out byte[] payload,
        out int endCursor)
    {
        return TryDecodeHeaderAt(
            leftSamples,
            rightSamples,
            start,
            logicalOffset,
            ofdm,
            totalBitCount,
            channelBitCount,
            sampleCount,
            payloadLength,
            rsByteLength,
            expectedPilot,
            stereoSplit,
            perSymbolSearchRadius,
            out payload,
            out endCursor,
            out _);
    }

    /// <summary>
    /// TryDecodeHeaderAt は、条件を満たす場合に処理を試行します。
    /// </summary>
    /// <param name="leftSamples">L チャネル PCM。</param>
    /// <param name="rightSamples">R チャネル PCM。</param>
    /// <param name="start">開始位置。</param>
    /// <param name="logicalOffset">論理サンプルオフセット（更新あり）。</param>
    /// <param name="ofdm">OFDM 生成器。</param>
    /// <param name="totalBitCount">totalBitCount。</param>
    /// <param name="channelBitCount">channelBitCount。</param>
    /// <param name="sampleCount">sampleCount。</param>
    /// <param name="payloadLength">ペイロード長（バイト）。</param>
    /// <param name="rsByteLength">rsByteLength。</param>
    /// <param name="expectedPilot">期待パイロットパターン。</param>
    /// <param name="stereoSplit">stereoSplit。</param>
    /// <param name="perSymbolSearchRadius">perSymbolSearchRadius。</param>
    /// <param name="payload">payload。</param>
    /// <param name="endCursor">endCursor。</param>
    /// <param name="meanAbsLlr">meanAbsLlr。</param>
    /// <param name="statusBoard">実行状態ボード。</param>
    /// <param name="frameKind">フレーム種別（FH/BH 等）。</param>
    /// <returns>成功または条件成立時 true。</returns>
    private static bool TryDecodeHeaderAt(
        Complex[] leftSamples,
        Complex[] rightSamples,
        int start,
        long logicalOffset,
        OfdmGenerator ofdm,
        int totalBitCount,
        int channelBitCount,
        int sampleCount,
        int payloadLength,
        int rsByteLength,
        byte[]? expectedPilot,
        bool stereoSplit,
        int perSymbolSearchRadius,
        out byte[] payload,
        out int endCursor,
        out double meanAbsLlr,
        CoreExecutionStatusBoard? statusBoard = null,
        CoreFrameKind frameKind = CoreFrameKind.Fh)
    {
        payload = Array.Empty<byte>();
        endCursor = start;
        meanAbsLlr = 0.0;
        if (start < 0 || start + sampleCount > leftSamples.Length)
        {
            return false;
        }

        if (stereoSplit && start + sampleCount > rightSamples.Length)
        {
            return false;
        }

        try
        {
            double[] llrs;
            if (perSymbolSearchRadius <= 0)
            {
                var cursor = start;
                llrs = DemodulateDataSoftLlrsFromStream(
                    ofdm,
                    leftSamples,
                    rightSamples,
                    ref cursor,
                    channelBitCount,
                    totalBitCount,
                    stereoSplit,
                    logicalOffset,
                    searchRadius: Math.Max(2, ofdm.SamplesPerOfdmSymbol / 16),
                    noiseVariance: 0.05,
                    ModulationScheme.Qpsk,
                    statusBoard,
                    onBlockProgress: null,
                    captureIq: false);
                endCursor = cursor;
            }
            else
            {
                var cursor = start;
                llrs = DemodulateDataSoftLlrsFromStream(
                    ofdm,
                    leftSamples,
                    rightSamples,
                    ref cursor,
                    channelBitCount,
                    totalBitCount,
                    stereoSplit,
                    logicalOffset,
                    perSymbolSearchRadius,
                    noiseVariance: 0.05,
                    ModulationScheme.Qpsk,
                    statusBoard,
                    onBlockProgress: null,
                    captureIq: false);
                endCursor = cursor;
            }

            var absSum = 0.0;
            for (var i = 0; i < llrs.Length; i++)
            {
                absSum += Math.Abs(llrs[i]);
            }

            meanAbsLlr = llrs.Length > 0 ? absSum / llrs.Length : 0.0;
            payload = DecodeHeaderFromSoftLlrs(
                llrs,
                payloadLength,
                rsByteLength,
                out var viterbiMetrics,
                out var rsMetrics,
                out _);
            if (expectedPilot is not null && !HeaderPrefixMatches(payload, expectedPilot))
            {
                return false;
            }

            statusBoard?.SetErrorRate(
                viterbiMetrics.CorrectionRate * 100.0,
                frameKind,
                CoreEccDecoderKind.Viterbi);
            statusBoard?.SetErrorRate(
                rsMetrics.PayloadCorrectionRate * 100.0,
                frameKind,
                CoreEccDecoderKind.ReedSolomon);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// DecodeHeaderFromSoftLlrs は、復号します。
    /// </summary>
    /// <param name="llrs">llrs。</param>
    /// <param name="payloadLength">ペイロード長（バイト）。</param>
    /// <param name="rsByteLength">rsByteLength。</param>
    /// <param name="viterbiMetrics">viterbiMetrics。</param>
    /// <param name="rsMetrics">rsMetrics。</param>
    /// <param name="infoLlrs">infoLlrs。</param>
    /// <returns>結果の配列またはスライス。</returns>
    private static byte[] DecodeHeaderFromSoftLlrs(
        double[] llrs,
        int payloadLength,
        int rsByteLength,
        out ConvolutionalCode.DecodeMetrics viterbiMetrics,
        out RsEcc256.DecodeMetrics rsMetrics,
        out double[] infoLlrs)
    {
        var rsEncoded = ConvolutionalCode.DecodeSoftToInfoLlrs(
            llrs,
            rsByteLength,
            out infoLlrs,
            out viterbiMetrics,
            terminated: true,
            punctureRate: HeaderPunctureRate);
        var paddedPayload = ApplyReedSolomonDecode(rsEncoded, out rsMetrics);
        var payload = new byte[payloadLength];
        Buffer.BlockCopy(paddedPayload, 0, payload, 0, payloadLength);
        return payload;
    }

    /// <summary>
    /// CollectSyncCandidates の結果を返します。
    /// </summary>
    /// <param name="samples">入力サンプル列。</param>
    /// <param name="expectedStart">expectedStart。</param>
    /// <param name="sampleCount">sampleCount。</param>
    /// <param name="searchRadius">探索半径（サンプル）。</param>
    /// <param name="ofdm">OFDM 生成器。</param>
    /// <param name="probeSymbols">probeSymbols。</param>
    /// <param name="useRightChannel">R 搬送波を使うか。</param>
    /// <param name="onProbe">onProbe。</param>
    /// <returns>結果のコレクション。</returns>
    private static List<int> CollectSyncCandidates(
        Complex[] samples,
        int expectedStart,
        int sampleCount,
        int searchRadius,
        OfdmGenerator ofdm,
        int probeSymbols,
        bool useRightChannel,
        Action<int>? onProbe = null)
    {
        var symbolLength = ofdm.SamplesPerOfdmSymbol;
        var step = Math.Max(1, symbolLength / 16);
        var unique = new List<int>();
        var seen = new HashSet<int>();

        void Add(int start)
        {
            if (start < 0 || start + sampleCount > samples.Length)
            {
                return;
            }

            if (seen.Add(start))
            {
                unique.Add(start);
            }
        }

        Add(expectedStart);
        onProbe?.Invoke(expectedStart);
        Add(ofdm.FindBestSymbolStart(samples, expectedStart, Math.Min(searchRadius, symbolLength), useRightChannel));

        var scored = new List<(int Start, double Score)>();
        for (var radius = 0; radius <= searchRadius; radius += Math.Max(step, symbolLength / 4))
        {
            var startA = expectedStart - radius;
            if (startA >= 0 && startA + sampleCount <= samples.Length)
            {
                onProbe?.Invoke(startA);
                scored.Add((startA, ofdm.ScoreLock(samples, startA, probeSymbols, useRightChannel)));
            }

            var startB = expectedStart + radius;
            if (startB >= 0 && startB + sampleCount <= samples.Length)
            {
                onProbe?.Invoke(startB);
                scored.Add((startB, ofdm.ScoreLock(samples, startB, probeSymbols, useRightChannel)));
            }
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var top = Math.Min(8, scored.Count);
        for (var i = 0; i < top; i++)
        {
            Add(scored[i].Start);
            Add(ofdm.FindBestSymbolStart(samples, scored[i].Start, step, useRightChannel));
        }

        return unique;
    }

    /// <summary>
    /// HeaderPrefixMatches の結果を返します。
    /// </summary>
    /// <param name="header">header。</param>
    /// <param name="expectedPilot">期待パイロットパターン。</param>
    /// <returns>成功または条件成立時 true。</returns>
    private static bool HeaderPrefixMatches(byte[] header, byte[] expectedPilot)
    {
        if (expectedPilot.Length != HeaderPilotBytes || HeaderVersion.Length != HeaderVersionBytes)
        {
            return false;
        }

        if (header.Length < HeaderPrefixBytes)
        {
            return false;
        }

        for (var i = 0; i < expectedPilot.Length; i++)
        {
            if (header[i] != expectedPilot[i])
            {
                return false;
            }
        }

        for (var i = 0; i < HeaderVersionBytes; i++)
        {
            if (header[HeaderPilotBytes + i] != HeaderVersion[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// ReadFileHeaderFileName は、読み取ります。
    /// </summary>
    /// <param name="fileHeader">ファイルヘッダーバイト列。</param>
    /// <returns>string。</returns>
    private static string ReadFileHeaderFileName(ReadOnlySpan<byte> fileHeader)
    {
        if (fileHeader.Length < HeaderPrefixBytes + FileNameBytes)
        {
            return string.Empty;
        }

        var nameBytes = fileHeader.Slice(HeaderPrefixBytes, FileNameBytes);
        var end = nameBytes.IndexOf((byte)0);
        if (end < 0)
        {
            end = nameBytes.Length;
        }
        else if (end == 0)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(nameBytes[..end]).Trim();
    }

    /// <summary>
    /// ReadFileHeaderTimestampUtc は、読み取ります。
    /// </summary>
    /// <param name="fileHeader">ファイルヘッダーバイト列。</param>
    /// <param name="offset">オフセット。</param>
    /// <returns>DateTime?。</returns>
    private static DateTime? ReadFileHeaderTimestampUtc(ReadOnlySpan<byte> fileHeader, int offset)
    {
        if (offset < 0 || fileHeader.Length < offset + 7)
        {
            return null;
        }

        var year = BinaryPrimitives.ReadUInt16BigEndian(fileHeader.Slice(offset, 2));
        var month = fileHeader[offset + 2];
        var day = fileHeader[offset + 3];
        var hour = fileHeader[offset + 4];
        var minute = fileHeader[offset + 5];
        var second = fileHeader[offset + 6];

        if (year is < 1900 or > 9999
            || month is < 1 or > 12
            || day is < 1 or > 31
            || hour > 23
            || minute > 59
            || second > 59)
        {
            return null;
        }

        try
        {
            var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
            return local.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// BuildFileHeader は、必要なテーブルまたは構造を構築します。
    /// </summary>
    /// <param name="fileInfo">fileInfo。</param>
    /// <param name="fileSize">fileSize。</param>
    /// <param name="blockCount">blockCount。</param>
    /// <param name="fileHash">fileHash。</param>
    /// <returns>結果の配列またはスライス。</returns>
    private static byte[] BuildFileHeader(FileInfo fileInfo, long fileSize, int blockCount, byte[] fileHash)
    {
        if (fileHash.Length != 64)
        {
            throw new ArgumentException("File hash must be SHA-512 (64 bytes).", nameof(fileHash));
        }

        var header = new byte[FileHeaderBytes];
        Buffer.BlockCopy(FileHeaderPilot, 0, header, 0, HeaderPilotBytes);
        Buffer.BlockCopy(HeaderVersion, 0, header, HeaderPilotBytes, HeaderVersionBytes);

        var nameBytes = Encoding.UTF8.GetBytes(fileInfo.Name);
        var copyLen = Math.Min(nameBytes.Length, FileNameBytes);
        Buffer.BlockCopy(nameBytes, 0, header, HeaderPrefixBytes, copyLen);
        Buffer.BlockCopy(fileHash, 0, header, HeaderPrefixBytes + FileNameBytes, 64);

        WriteFileAttributes(header.AsSpan(840, 20), fileInfo);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(860, 8), fileSize);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(868, 8), blockCount);
        var crc = Crc32.Compute(header.AsSpan(HeaderCrcDataOffset, FileHeaderBytes - HeaderCrcDataOffset - CrcBytes));
        Crc32.WriteBigEndian(header.AsSpan(FileHeaderBytes - CrcBytes, CrcBytes), crc);
        return header;
    }

    /// <summary>
    /// WriteFileAttributes は、書き込みます。
    /// </summary>
    /// <param name="dest">dest。</param>
    /// <param name="fileInfo">fileInfo。</param>
    private static void WriteFileAttributes(Span<byte> dest, FileInfo fileInfo)
    {
        WriteTimestamp(dest.Slice(0, 7), fileInfo.CreationTime);
        WriteTimestamp(dest.Slice(7, 7), fileInfo.LastWriteTime);

        var attrs = new byte[6];
        byte flags = 0;
        if (fileInfo.IsReadOnly)
        {
            flags |= 0x01;
        }

        var ext = fileInfo.Extension;
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            flags |= 0x02;
        }

        attrs[0] = flags;
        attrs.CopyTo(dest.Slice(14, 6));
    }

    /// <summary>
    /// WriteTimestamp は、書き込みます。
    /// </summary>
    /// <param name="dest">dest。</param>
    /// <param name="timestamp">timestamp。</param>
    private static void WriteTimestamp(Span<byte> dest, DateTime timestamp)
    {
        var local = timestamp.ToLocalTime();
        BinaryPrimitives.WriteUInt16BigEndian(dest, (ushort)local.Year);
        dest[2] = (byte)local.Month;
        dest[3] = (byte)local.Day;
        dest[4] = (byte)local.Hour;
        dest[5] = (byte)local.Minute;
        dest[6] = (byte)local.Second;
    }

    /// <summary>
    /// BuildBlockHeader は、必要なテーブルまたは構造を構築します。
    /// </summary>
    /// <param name="subcarriers">subcarriers。</param>
    /// <param name="modulationMode">modulationMode。</param>
    /// <param name="channelMode">モノラル／ステレオ。</param>
    /// <param name="blockIndex">blockIndex。</param>
    /// <param name="blockSize">blockSize。</param>
    /// <param name="blockHash">blockHash。</param>
    /// <param name="fileHash">fileHash。</param>
    /// <returns>結果の配列またはスライス。</returns>
    private static byte[] BuildBlockHeader(
        byte subcarriers,
        byte modulationMode,
        byte channelMode,
        long blockIndex,
        int blockSize,
        byte[] blockHash,
        byte[] fileHash)
    {
        if (blockHash.Length != 32)
        {
            throw new ArgumentException("Block hash must be SHA-256 (32 bytes).", nameof(blockHash));
        }

        if (fileHash.Length != 64)
        {
            throw new ArgumentException("File hash must be SHA-512 (64 bytes).", nameof(fileHash));
        }

        var header = new byte[BlockHeaderBytes];
        Buffer.BlockCopy(BlockHeaderPilot, 0, header, 0, HeaderPilotBytes);
        Buffer.BlockCopy(HeaderVersion, 0, header, HeaderPilotBytes, HeaderVersionBytes);
        header[8] = subcarriers;
        header[9] = modulationMode;
        header[10] = channelMode;
        header[11] = 0;
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(12, 8), blockIndex);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20, 4), blockSize);
        Buffer.BlockCopy(blockHash, 0, header, 24, 32);
        Buffer.BlockCopy(fileHash, 0, header, 56, 64);
        var crc = Crc32.Compute(header.AsSpan(HeaderCrcDataOffset, BlockHeaderBytes - HeaderCrcDataOffset - CrcBytes));
        Crc32.WriteBigEndian(header.AsSpan(BlockHeaderBytes - CrcBytes, CrcBytes), crc);
        return header;
    }

    /// <summary>
    /// EnsureHeaderCrc は、前提条件を満たすよう確保します。
    /// </summary>
    /// <param name="header">header。</param>
    /// <param name="headerName">headerName。</param>
    private static void EnsureHeaderCrc(byte[] header, string headerName)
    {
        if (header.Length < HeaderCrcDataOffset + CrcBytes)
        {
            throw new InvalidDataException($"Invalid {headerName}: too short for CRC.");
        }

        var dataLen = header.Length - HeaderCrcDataOffset - CrcBytes;
        if (!Crc32.Matches(header.AsSpan(HeaderCrcDataOffset, dataLen), header.AsSpan(header.Length - CrcBytes, CrcBytes)))
        {
            throw new InvalidDataException($"Invalid {headerName}: CRC-32 mismatch.");
        }
    }

    /// <summary>
    /// EnsureHeaderPilot は、前提条件を満たすよう確保します。
    /// </summary>
    /// <param name="header">header。</param>
    /// <param name="expectedPilot">期待パイロットパターン。</param>
    /// <param name="headerName">headerName。</param>
    private static void EnsureHeaderPilot(byte[] header, byte[] expectedPilot, string headerName)
    {
        if (expectedPilot.Length != HeaderPilotBytes || HeaderVersion.Length != HeaderVersionBytes)
        {
            throw new InvalidDataException($"Invalid {headerName}: pilot/version definition mismatch.");
        }

        if (header.Length < HeaderPrefixBytes)
        {
            throw new InvalidDataException($"Invalid {headerName}: too short for pilot.");
        }

        for (var i = 0; i < expectedPilot.Length; i++)
        {
            if (header[i] != expectedPilot[i])
            {
                throw new InvalidDataException($"Invalid {headerName} pilot at byte {i}.");
            }
        }

        for (var i = 0; i < HeaderVersionBytes; i++)
        {
            if (header[HeaderPilotBytes + i] != HeaderVersion[i])
            {
                throw new InvalidDataException($"Invalid {headerName} version at byte {HeaderPilotBytes + i}.");
            }
        }
    }
}


