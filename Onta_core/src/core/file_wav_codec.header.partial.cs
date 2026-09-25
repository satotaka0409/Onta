using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;

namespace Onta.Core;

public sealed partial class FileWavCodec
{
    /// <summary>
    /// ヘッダー部（FH/BH）用の OFDM 生成器を作成します。
    /// </summary>
    /// <param name="carrierGrid">搬送波グリッド（SC-8 族／SC-24 族）。null ならプロファイルから決定。</param>
    /// <returns>ヘッダー変調用に構成した OFDM 生成器。</returns>
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
    /// SC-8 族と SC-24 族の搬送波グリッドを互いに切り替えます。
    /// </summary>
    /// <param name="grid">現在の搬送波グリッド。</param>
    /// <returns>反対側の搬送波グリッド族。</returns>
    private static OfdmCarrierGrid AlternateCarrierGrid(OfdmCarrierGrid grid) =>
        grid == OfdmCarrierGrid.Sc8Family ? OfdmCarrierGrid.Sc24Family : OfdmCarrierGrid.Sc8Family;

    /// <summary>
    /// ヘッダーパケットを同期復号し、失敗時は反対の搬送波グリッドで再試行します。
    /// </summary>
    /// <param name="headerOfdm">ヘッダー用 OFDM 生成器（グリッド切替時に差し替え）。</param>
    /// <param name="leftSamples">左チャネル PCM。</param>
    /// <param name="rightSamples">右チャネル PCM。</param>
    /// <param name="warpedCursor">ワウ補正後カーソル（成功時に更新）。</param>
    /// <param name="logicalOffset">論理サンプルオフセット（成功時に更新）。</param>
    /// <param name="payloadLength">期待するヘッダーペイロード長（バイト）。</param>
    /// <param name="expectedPilot">期待する先頭パイロットパターン。</param>
    /// <param name="searchRadius">同期探索半径（サンプル）。</param>
    /// <param name="onSyncProgress">同期進捗（完了数, 総数）の通知。</param>
    /// <param name="statusBoard">画面共有用の実行状態ボード。</param>
    /// <param name="frameKind">フレーム種別（FH/BH 等）。</param>
    /// <returns>復号したヘッダーバイト列。</returns>
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
    /// ファイルヘッダー（無変調区間付き）を PCM バッファへ追記します。
    /// </summary>
    /// <param name="leftPcm">左チャネル出力 PCM バッファ。</param>
    /// <param name="rightPcm">右チャネル出力 PCM バッファ。</param>
    /// <param name="headerOfdm">ヘッダー用 OFDM 生成器。</param>
    /// <param name="fileHeader">送信するファイルヘッダーバイト列。</param>
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
    /// ヘッダーバイト列を符号化・変調し、無変調区間付きで PCM へ追記します。
    /// </summary>
    /// <param name="leftPcm">左チャネル出力 PCM バッファ。</param>
    /// <param name="rightPcm">右チャネル出力 PCM バッファ。</param>
    /// <param name="ofdm">変調に使う OFDM 生成器。</param>
    /// <param name="leftHeaderBytes">符号化するヘッダーバイト列（L 基準）。</param>
    /// <param name="rightHeaderBytes">互換用の右ヘッダー（現状未使用）。</param>
    /// <param name="unmodulatedSamples">先頭に付ける無変調サンプル数。</param>
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
    /// ヘッダー用サンプル対を追記し、ステレオ時は R 欠落を L で補います。
    /// </summary>
    /// <param name="leftPcm">左チャネル出力 PCM バッファ。</param>
    /// <param name="rightPcm">右チャネル出力 PCM バッファ。</param>
    /// <param name="pair">追加する L/R サンプル配列の組。</param>
    private void AppendHeaderPair(List<Complex> leftPcm, List<Complex> rightPcm, (Complex[] Left, Complex[] Right) pair)
    {
        AppendPair(leftPcm, rightPcm, pair);
        if (_profile.ChannelMode == ChannelMode.Stereo && pair.Right.Length == 0)
        {
            rightPcm.AddRange(pair.Left);
        }
    }

    /// <summary>
    /// パケット長から FH/BH いずれかの無変調サンプル数を返します。
    /// </summary>
    /// <param name="packetLength">ヘッダーパケット長（バイト。FH=880 / BH=124）。</param>
    /// <returns>先頭に付ける無変調サンプル数。</returns>
    private int HeaderUnmodulatedSamplesFor(int packetLength) =>
        packetLength == FileHeaderBytes
            ? _profile.FileHeaderUnmodulatedSamples
            : _profile.BlockHeaderUnmodulatedSamples;

    /// <summary>
    /// ヘッダー先頭の無変調区間をカーソルから読み飛ばします。
    /// </summary>
    /// <param name="samples">入力サンプル列。</param>
    /// <param name="warpedCursor">ワウ補正後カーソル（読み飛ばし分更新）。</param>
    /// <param name="logicalOffset">論理サンプルオフセット（読み飛ばし分更新）。</param>
    /// <param name="unmodulatedSamples">読み飛ばす無変調サンプル数。</param>
    /// <param name="statusBoard">FFT 可視化用の実行状態ボード。</param>
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
    /// 同期探索付きでヘッダーパケットを復号します。
    /// </summary>
    /// <param name="leftSamples">左チャネル PCM。</param>
    /// <param name="rightSamples">右チャネル PCM。</param>
    /// <param name="warpedCursor">ワウ補正後カーソル（成功時に更新）。</param>
    /// <param name="logicalOffset">論理サンプルオフセット（成功時に更新）。</param>
    /// <param name="ofdm">復調に使う OFDM 生成器。</param>
    /// <param name="payloadLength">期待するヘッダーペイロード長（バイト）。</param>
    /// <param name="expectedPilot">期待する先頭パイロットパターン。</param>
    /// <param name="searchRadius">同期探索半径（サンプル）。</param>
    /// <param name="onSyncProgress">同期進捗（完了数, 総数）の通知。</param>
    /// <param name="statusBoard">画面共有用の実行状態ボード。</param>
    /// <param name="frameKind">フレーム種別（FH/BH 等）。</param>
    /// <returns>復号したヘッダーバイト列。</returns>
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
        /// <summary>
        /// ヘッダー同期中の受信 PCM FFT をステータスボードへ公開します。
        /// </summary>
        /// <param name="sampleEnd">FFT 窓の終端サンプル位置。</param>
        /// <param name="force">スロットルを無視して即時公開する場合 true。</param>
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
    /// 指定位置からヘッダー復号を試行します（meanAbsLlr なし簡易版）。
    /// </summary>
    /// <param name="leftSamples">左チャネル PCM。</param>
    /// <param name="rightSamples">右チャネル PCM。</param>
    /// <param name="start">復調開始サンプル位置。</param>
    /// <param name="logicalOffset">論理サンプルオフセット。</param>
    /// <param name="ofdm">復調に使う OFDM 生成器。</param>
    /// <param name="totalBitCount">結合後の総ビット数。</param>
    /// <param name="channelBitCount">1 チャネルあたりのビット数。</param>
    /// <param name="sampleCount">復調に必要なサンプル数。</param>
    /// <param name="payloadLength">期待するヘッダーペイロード長（バイト）。</param>
    /// <param name="rsByteLength">RS 符号化後バイト長。</param>
    /// <param name="expectedPilot">期待する先頭パイロット。null なら検査しない。</param>
    /// <param name="stereoSplit">左右分割復調を行うか。</param>
    /// <param name="perSymbolSearchRadius">シンボル単位の開始位置探索半径。</param>
    /// <param name="payload">復号に成功したヘッダーバイト列。</param>
    /// <param name="endCursor">復調終了後のサンプルカーソル。</param>
    /// <returns>パイロット一致を含め復号に成功したとき true。</returns>
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
    /// 指定位置からヘッダーをソフト復調し、パイロット一致を確認します。
    /// </summary>
    /// <param name="leftSamples">左チャネル PCM。</param>
    /// <param name="rightSamples">右チャネル PCM。</param>
    /// <param name="start">復調開始サンプル位置。</param>
    /// <param name="logicalOffset">論理サンプルオフセット。</param>
    /// <param name="ofdm">復調に使う OFDM 生成器。</param>
    /// <param name="totalBitCount">結合後の総ビット数。</param>
    /// <param name="channelBitCount">1 チャネルあたりのビット数。</param>
    /// <param name="sampleCount">復調に必要なサンプル数。</param>
    /// <param name="payloadLength">期待するヘッダーペイロード長（バイト）。</param>
    /// <param name="rsByteLength">RS 符号化後バイト長。</param>
    /// <param name="expectedPilot">期待する先頭パイロット。null なら検査しない。</param>
    /// <param name="stereoSplit">左右分割復調を行うか。</param>
    /// <param name="perSymbolSearchRadius">シンボル単位の開始位置探索半径。</param>
    /// <param name="payload">復号に成功したヘッダーバイト列。</param>
    /// <param name="endCursor">復調終了後のサンプルカーソル。</param>
    /// <param name="meanAbsLlr">復調 LLR の平均絶対値。</param>
    /// <param name="statusBoard">エラー率通知先の実行状態ボード。</param>
    /// <param name="frameKind">フレーム種別（FH/BH 等）。</param>
    /// <returns>パイロット一致を含め復号に成功したとき true。</returns>
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
    /// ソフト LLR から畳み込み BCJR と RS 復号でヘッダーを復元します。
    /// </summary>
    /// <param name="llrs">チャネルから得たソフト LLR 列。</param>
    /// <param name="payloadLength">復元するヘッダーペイロード長（バイト）。</param>
    /// <param name="rsByteLength">RS 符号化後バイト長。</param>
    /// <param name="viterbiMetrics">畳み込み復号の訂正メトリクス。</param>
    /// <param name="rsMetrics">RS 復号の訂正メトリクス。</param>
    /// <param name="infoLlrs">畳み込み復号後の情報 LLR。</param>
    /// <returns>復元したヘッダーペイロード。</returns>
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
    /// ヘッダー同期候補の開始サンプル位置を収集します。
    /// </summary>
    /// <param name="samples">探索対象のサンプル列。</param>
    /// <param name="expectedStart">期待する開始位置。</param>
    /// <param name="sampleCount">パケットに必要なサンプル数。</param>
    /// <param name="searchRadius">探索半径（サンプル）。</param>
    /// <param name="ofdm">ロック評価に使う OFDM 生成器。</param>
    /// <param name="probeSymbols">スコア計算に使うプローブシンボル数。</param>
    /// <param name="useRightChannel">R 搬送波を使うか。</param>
    /// <param name="onProbe">候補位置を探査したときの通知。</param>
    /// <returns>重複除去済みの開始位置候補リスト。</returns>
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

        /// <summary>
        /// 有効範囲内の同期候補開始位置を重複なくリストへ追加します。
        /// </summary>
        /// <param name="start">候補とする開始サンプル位置。</param>
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
    /// ヘッダー先頭のパイロットとバージョンが期待値と一致するか判定します。
    /// </summary>
    /// <param name="header">検査対象のヘッダーバイト列。</param>
    /// <param name="expectedPilot">期待するパイロットパターン。</param>
    /// <returns>パイロットとバージョンが一致するとき true。</returns>
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
    /// ファイルヘッダーから UTF-8 ファイル名を読み取ります。
    /// </summary>
    /// <param name="fileHeader">ファイルヘッダーバイト列。</param>
    /// <returns>切り出したファイル名。欠損時は空文字。</returns>
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
    /// ファイルヘッダー内の 7 バイトロール時刻を UTC の DateTime へ変換します。
    /// </summary>
    /// <param name="fileHeader">ファイルヘッダーバイト列。</param>
    /// <param name="offset">タイムスタンプ先頭のバイトオフセット。</param>
    /// <returns>UTC 時刻。不正なら null。</returns>
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
    /// ファイル名・ハッシュ・属性などからファイルヘッダーを組み立てます。
    /// </summary>
    /// <param name="fileInfo">ファイル名と属性の取得元。</param>
    /// <param name="fileSize">ファイルサイズ（バイト）。</param>
    /// <param name="blockCount">総ブロック数。</param>
    /// <param name="fileHash">ファイル全体の SHA-512 ハッシュ（64 バイト）。</param>
    /// <returns>CRC 付きのファイルヘッダーバイト列。</returns>
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
    /// 作成／更新日時と属性フラグをファイル属性領域へ書き込みます。
    /// </summary>
    /// <param name="dest">20 バイトの属性書き込み先。</param>
    /// <param name="fileInfo">日時・属性の取得元。</param>
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
    /// ローカル時刻を 7 バイト（年〜秒）形式で書き込みます。
    /// </summary>
    /// <param name="dest">7 バイトの書き込み先。</param>
    /// <param name="timestamp">書き込む日時。</param>
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
    /// データ部変調情報とハッシュからブロックヘッダーを組み立てます。
    /// </summary>
    /// <param name="subcarriers">データ部サブキャリア数。</param>
    /// <param name="modulationMode">データ部変調方式コード。</param>
    /// <param name="channelMode">モノラル／ステレオ（0/1）。</param>
    /// <param name="blockIndex">ファイル内のブロック番号。</param>
    /// <param name="blockSize">ブロックペイロード長（バイト）。</param>
    /// <param name="blockHash">ブロックの SHA-256 ハッシュ（32 バイト）。</param>
    /// <param name="fileHash">ファイル全体の SHA-512 ハッシュ（64 バイト）。</param>
    /// <returns>CRC 付きのブロックヘッダーバイト列。</returns>
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
    /// ヘッダー末尾の CRC-32 が一致することを検証します。
    /// </summary>
    /// <param name="header">検証対象のヘッダーバイト列。</param>
    /// <param name="headerName">例外メッセージ用のヘッダー名称。</param>
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
    /// ヘッダー先頭のパイロットとバージョンが期待値と一致することを検証します。
    /// </summary>
    /// <param name="header">検証対象のヘッダーバイト列。</param>
    /// <param name="expectedPilot">期待するパイロットパターン。</param>
    /// <param name="headerName">例外メッセージ用のヘッダー名称。</param>
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


