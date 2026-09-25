using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Onta.Core;

/// <summary>
/// 1024バイト固定長のターボ符号エンコード/復号を提供します。
/// </summary>
public static class TurboEcc1024
{
    /// <summary>
    /// 復号時の補正メトリクスです。
    /// </summary>
    /// <param name="CorrectedBitCount">ハード判定との差分として数えた訂正ビット数。</param>
    /// <param name="CorrectedByteCount">訂正ビットから換算した訂正バイト数。</param>
    /// <param name="PayloadBitLength">ペイロードのビット長。</param>
    /// <param name="CorrectionRate">ペイロードに対する訂正率（0〜1）。</param>
    public readonly record struct DecodeMetrics(
        int CorrectedBitCount,
        int CorrectedByteCount,
        int PayloadBitLength,
        double CorrectionRate);

    /// <summary>
    /// 情報ブロック長（バイト）です。
    /// </summary>
    public const int DataUnitBytes = 1024;

    /// <summary>
    /// 情報ブロック長（ビット）です。
    /// </summary>
    public const int DataUnitBits = DataUnitBytes * 8;

    /// <summary>
    /// 符号語長（ビット）です。
    /// </summary>
    public const int EncodedBits = DataUnitBits * 3;

    /// <summary>
    /// 符号語長（バイト）です。
    /// </summary>
    public const int EncodedBytes = EncodedBits / 8;

    /// <summary>
    /// RSC 復号トレリスの状態数（3bit メモリ）です。
    /// </summary>

    private const int StateCount = 8;
    /// <summary>
    /// 復号メトリクス初期化で使う疑似負無限大です。
    /// </summary>

    private const double NegativeInfinity = -1e30;

    /// <summary>
    /// ターボ符号の固定インターリーバを生成します。
    /// </summary>

    private static readonly int[] Interleaver = BuildInterleaver(DataUnitBits, seed: 20260903);
    /// <summary>
    /// 逆順変換用のデインターリーバを生成します。
    /// </summary>

    private static readonly int[] Deinterleaver = BuildDeinterleaver(Interleaver);
    private static readonly byte[] NextState = BuildNextStateTable();
    private static readonly byte[] ParityBit = BuildParityTable();
    private static readonly byte[] NextStateWhenInput0 = BuildNextStateByInput(inputBit: 0);
    private static readonly byte[] NextStateWhenInput1 = BuildNextStateByInput(inputBit: 1);
    private static readonly byte[] ParityWhenInput0 = BuildParityByInput(inputBit: 0);
    private static readonly byte[] ParityWhenInput1 = BuildParityByInput(inputBit: 1);
    private static readonly byte[] ByteToBitsLookup = BuildByteToBitsLookup();

    /// <summary>
    /// 1024バイト情報ブロックをターボ符号化します。
    /// </summary>
    /// <param name="data1024">1024バイトの入力データ。</param>
    /// <returns>符号化後バイト列。</returns>
    public static byte[] Encode(byte[] data1024)
    {
        ArgumentNullException.ThrowIfNull(data1024);
        if (data1024.Length != DataUnitBytes)
        {
            throw new ArgumentException($"Input must be exactly {DataUnitBytes} bytes.", nameof(data1024));
        }

        var dataBits = BytesToBits(data1024);
        var interleavedBits = InterleaveBits(dataBits, Interleaver);

        var parity1 = EncodeRscParity(dataBits);
        var parity2 = EncodeRscParity(interleavedBits);

        var encodedTriples = new bool[EncodedBits];
        for (var i = 0; i < DataUnitBits; i++)
        {
            var baseIndex = i * 3;
            encodedTriples[baseIndex] = dataBits[i];
            encodedTriples[baseIndex + 1] = parity1[i];
            encodedTriples[baseIndex + 2] = parity2[i];
        }

        return PackBits(encodedTriples);
    }

    /// <summary>
    /// 符号語を復号します。
    /// </summary>
    /// <param name="encoded">符号化データ。</param>
    /// <param name="iterations">反復回数。</param>
    /// <param name="channelReliability">LLR変換に用いるチャネル信頼度。</param>
    /// <returns>復号した1024バイトデータ。</returns>
    public static byte[] Decode(byte[] encoded, int iterations = 6, double channelReliability = 2.0)
    {
        return Decode(encoded, out _, iterations, channelReliability);
    }

    /// <summary>
    /// 符号語を復号し、補正メトリクスを返します。
    /// </summary>
    /// <param name="encoded">符号化データ。</param>
    /// <param name="metrics">復号時の補正メトリクス。</param>
    /// <param name="iterations">反復回数。</param>
    /// <param name="channelReliability">LLR変換に用いるチャネル信頼度。</param>
    /// <returns>復号した1024バイトデータ。</returns>
    public static byte[] Decode(byte[] encoded, out DecodeMetrics metrics, int iterations = 6, double channelReliability = 2.0)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length != EncodedBytes)
        {
            throw new ArgumentException($"Input must be exactly {EncodedBytes} bytes.", nameof(encoded));
        }

        if (iterations <= 0)
        {
            throw new ArgumentException("Iterations must be > 0.", nameof(iterations));
        }

        if (channelReliability <= 0)
        {
            throw new ArgumentException("Channel reliability must be > 0.", nameof(channelReliability));
        }

        var sysBits = new bool[DataUnitBits];
        var systematic = ArrayPool<double>.Shared.Rent(DataUnitBits);
        var parity1 = ArrayPool<double>.Shared.Rent(DataUnitBits);
        var parity2 = ArrayPool<double>.Shared.Rent(DataUnitBits);

        try
        {
            for (var i = 0; i < DataUnitBits; i++)
            {
                var baseIndex = i * 3;
                var sys = ReadPackedBit(encoded, baseIndex);
                var p1 = ReadPackedBit(encoded, baseIndex + 1);
                var p2 = ReadPackedBit(encoded, baseIndex + 2);
                sysBits[i] = sys;
                systematic[i] = sys ? -channelReliability : channelReliability;
                parity1[i] = p1 ? -channelReliability : channelReliability;
                parity2[i] = p2 ? -channelReliability : channelReliability;
            }

            return DecodeFromComponentLlrs(
                systematic.AsSpan(0, DataUnitBits),
                parity1.AsSpan(0, DataUnitBits),
                parity2.AsSpan(0, DataUnitBits),
                sysBits,
                iterations,
                out metrics);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(systematic, clearArray: false);
            ArrayPool<double>.Shared.Return(parity1, clearArray: false);
            ArrayPool<double>.Shared.Return(parity2, clearArray: false);
        }
    }

    /// <summary>
    /// チャネルLLR列から復号します。
    /// </summary>
    /// <param name="encodedBitLlrs">符号語ビットのLLR列。</param>
    /// <param name="iterations">反復回数。</param>
    /// <returns>復号した1024バイトデータ。</returns>
    public static byte[] DecodeFromChannelLlrs(
        ReadOnlySpan<double> encodedBitLlrs,
        int iterations = 12)
    {
        return DecodeFromChannelLlrs(encodedBitLlrs, out _, iterations);
    }

    /// <summary>
    /// チャネルLLR列から復号し、補正メトリクスを返します。
    /// </summary>
    /// <param name="encodedBitLlrs">符号語ビットのLLR列。</param>
    /// <param name="metrics">復号時の補正メトリクス。</param>
    /// <param name="iterations">反復回数。</param>
    /// <returns>復号した1024バイトデータ。</returns>
    public static byte[] DecodeFromChannelLlrs(
        ReadOnlySpan<double> encodedBitLlrs,
        out DecodeMetrics metrics,
        int iterations = 12)
    {
        if (encodedBitLlrs.Length < EncodedBits)
        {
            throw new ArgumentException(
                $"Channel LLR length {encodedBitLlrs.Length} is shorter than required {EncodedBits}.",
                nameof(encodedBitLlrs));
        }

        if (iterations <= 0)
        {
            throw new ArgumentException("Iterations must be > 0.", nameof(iterations));
        }

        var systematicBuf = ArrayPool<double>.Shared.Rent(DataUnitBits);
        var parity1Buf = ArrayPool<double>.Shared.Rent(DataUnitBits);
        var parity2Buf = ArrayPool<double>.Shared.Rent(DataUnitBits);
        var sysBitsBuf = ArrayPool<bool>.Shared.Rent(DataUnitBits);
        try
        {
            var systematic = systematicBuf.AsSpan(0, DataUnitBits);
            var parity1 = parity1Buf.AsSpan(0, DataUnitBits);
            var parity2 = parity2Buf.AsSpan(0, DataUnitBits);
            var sysBits = sysBitsBuf.AsSpan(0, DataUnitBits);
            for (var i = 0; i < DataUnitBits; i++)
            {
                var baseIndex = i * 3;
                systematic[i] = encodedBitLlrs[baseIndex];
                parity1[i] = encodedBitLlrs[baseIndex + 1];
                parity2[i] = encodedBitLlrs[baseIndex + 2];
                sysBits[i] = systematic[i] < 0.0;
            }

            return DecodeFromComponentLlrs(systematic, parity1, parity2, sysBits, iterations, out metrics);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(systematicBuf, clearArray: false);
            ArrayPool<double>.Shared.Return(parity1Buf, clearArray: false);
            ArrayPool<double>.Shared.Return(parity2Buf, clearArray: false);
            ArrayPool<bool>.Shared.Return(sysBitsBuf, clearArray: false);
        }
    }

    /// <summary>
    /// 成分LLR（系統/パリティ）から反復復号を実行します。
    /// </summary>
    /// <param name="systematic">系統ビットLLR。</param>
    /// <param name="parity1">1本目パリティLLR。</param>
    /// <param name="parity2">2本目パリティLLR。</param>
    /// <param name="sysBits">系統ビットのハード判定。</param>
    /// <param name="iterations">反復回数。</param>
    /// <param name="metrics">復号時の補正メトリクス。</param>
    /// <returns>復号したバイト列。</returns>
    private static byte[] DecodeFromComponentLlrs(
        ReadOnlySpan<double> systematic,
        ReadOnlySpan<double> parity1,
        ReadOnlySpan<double> parity2,
        ReadOnlySpan<bool> sysBits,
        int iterations,
        out DecodeMetrics metrics)
    {
        var n = systematic.Length;
        if (parity1.Length != n || parity2.Length != n || sysBits.Length != n)
        {
            throw new ArgumentException("Turbo decode input lengths must match.");
        }

        var systematicInterleaved = ArrayPool<double>.Shared.Rent(n);
        var apriori1 = ArrayPool<double>.Shared.Rent(n);
        var apriori2 = ArrayPool<double>.Shared.Rent(n);
        var extrinsic1 = ArrayPool<double>.Shared.Rent(n);
        var extrinsic2 = ArrayPool<double>.Shared.Rent(n);

        try
        {
            InterleaveDoublesInto(systematic, Interleaver, systematicInterleaved.AsSpan(0, n));
            Array.Clear(apriori1, 0, n);

            var stableHardIters = 0;
            var prevHard = ArrayPool<bool>.Shared.Rent(n);
            try
            {
                for (var iter = 0; iter < iterations; iter++)
                {
                    DecodeSisoMaxLogMapInto(
                        systematic,
                        parity1,
                        apriori1.AsSpan(0, n),
                        extrinsic1.AsSpan(0, n));
                    InterleaveDoublesInto(extrinsic1.AsSpan(0, n), Interleaver, apriori2.AsSpan(0, n));
                    DecodeSisoMaxLogMapInto(
                        systematicInterleaved.AsSpan(0, n),
                        parity2,
                        apriori2.AsSpan(0, n),
                        extrinsic2.AsSpan(0, n));
                    DeinterleaveDoublesInto(extrinsic2.AsSpan(0, n), Deinterleaver, apriori1.AsSpan(0, n));

                    var hardChanged = false;
                    for (var i = 0; i < n; i++)
                    {
                        var hard = (systematic[i] + apriori1[i]) < 0.0;
                        if (iter > 0 && hard != prevHard[i])
                        {
                            hardChanged = true;
                        }

                        prevHard[i] = hard;
                    }

                    if (iter > 0 && !hardChanged)
                    {
                        stableHardIters++;
                        if (stableHardIters >= 2)
                        {
                            break;
                        }
                    }
                    else
                    {
                        stableHardIters = 0;
                    }
                }
            }
            finally
            {
                ArrayPool<bool>.Shared.Return(prevHard, clearArray: false);
            }

            var decodedBits = new bool[n];
            for (var i = 0; i < n; i++)
            {
                decodedBits[i] = (systematic[i] + apriori1[i]) < 0.0;
            }

            var correctedBitCount = CountDifferentBits(decodedBits, sysBits);
            var decodedBytes = BitsToBytes(decodedBits);
            var correctedByteCount = CountDifferentBytesAgainstBits(decodedBytes, sysBits);

            metrics = new DecodeMetrics(
                CorrectedBitCount: correctedBitCount,
                CorrectedByteCount: correctedByteCount,
                PayloadBitLength: n,
                CorrectionRate: (double)correctedBitCount / n);

            return decodedBytes;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(systematicInterleaved, clearArray: false);
            ArrayPool<double>.Shared.Return(apriori1, clearArray: false);
            ArrayPool<double>.Shared.Return(apriori2, clearArray: false);
            ArrayPool<double>.Shared.Return(extrinsic1, clearArray: false);
            ArrayPool<double>.Shared.Return(extrinsic2, clearArray: false);
        }
    }

    /// <summary>
    /// 例外を投げずに復号を試行します。
    /// </summary>
    /// <param name="encoded">符号化データ。</param>
    /// <param name="decoded1024">成功時の復号データ。</param>
    /// <param name="iterations">反復回数。</param>
    /// <param name="channelReliability">LLR変換に用いるチャネル信頼度。</param>
    /// <returns>復号成功時 true。</returns>
    public static bool TryDecode(byte[] encoded, out byte[] decoded1024, int iterations = 6, double channelReliability = 2.0)
    {
        return TryDecode(encoded, out decoded1024, out _, iterations, channelReliability);
    }

    /// <summary>
    /// 例外を投げずに復号を試行し、メトリクスを返します。
    /// </summary>
    /// <param name="encoded">符号化データ。</param>
    /// <param name="decoded1024">成功時の復号データ。</param>
    /// <param name="metrics">復号時の補正メトリクス。</param>
    /// <param name="iterations">反復回数。</param>
    /// <param name="channelReliability">LLR変換に用いるチャネル信頼度。</param>
    /// <returns>復号成功時 true。</returns>
    public static bool TryDecode(byte[] encoded, out byte[] decoded1024, out DecodeMetrics metrics, int iterations = 6, double channelReliability = 2.0)
    {
        decoded1024 = Array.Empty<byte>();
        metrics = default;
        try
        {
            decoded1024 = Decode(encoded, out metrics, iterations, channelReliability);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// RSC 符号化器でパリティビット列を生成します。
    /// </summary>
    /// <param name="inputBits">系統（情報）ビット列。</param>
    /// <returns>パリティビット列。</returns>
    private static bool[] EncodeRscParity(bool[] inputBits)
    {
        var parity = new bool[inputBits.Length];
        var state = 0;

        for (var i = 0; i < inputBits.Length; i++)
        {
            if (inputBits[i])
            {
                parity[i] = ParityWhenInput1[state] == 1;
                state = NextStateWhenInput1[state];
            }
            else
            {
                parity[i] = ParityWhenInput0[state] == 1;
                state = NextStateWhenInput0[state];
            }
        }

        return parity;
    }

    /// <summary>
    /// Max-Log-MAP SISO 復号で外部LLRを算出します。
    /// </summary>
    /// <param name="systematic">系統ビットLLR。</param>
    /// <param name="parity">パリティビットLLR。</param>
    /// <param name="apriori">事前LLR。</param>
    /// <param name="extrinsic">外部LLRの出力先。</param>
    private static void DecodeSisoMaxLogMapInto(
        ReadOnlySpan<double> systematic,
        ReadOnlySpan<double> parity,
        ReadOnlySpan<double> apriori,
        Span<double> extrinsic)
    {
        var n = systematic.Length;
        var rowWidth = StateCount;
        var matrixLength = (n + 1) * rowWidth;
        var alpha = ArrayPool<double>.Shared.Rent(matrixLength);
        var beta = ArrayPool<double>.Shared.Rent(matrixLength);

        try
        {
            // ArrayPool 由来のゴミを避けるため、先頭行のみ初期化し各段で次行を明示クリアする
            for (var s = 0; s < StateCount; s++)
            {
                alpha[s] = s == 0 ? 0.0 : NegativeInfinity;
                beta[(n * rowWidth) + s] = 0.0;
            }

            for (var k = 0; k < n; k++)
            {
                var rowBase = k * rowWidth;
                var nextRowBase = (k + 1) * rowWidth;
                for (var ns = 0; ns < StateCount; ns++)
                {
                    alpha[nextRowBase + ns] = NegativeInfinity;
                }

                var su = systematic[k] + apriori[k];
                var p = parity[k];
                var u0p0 = 0.5 * (su + p);
                var u0p1 = 0.5 * (su - p);
                var u1p0 = 0.5 * (-su + p);
                var u1p1 = 0.5 * (-su - p);

                for (var s = 0; s < StateCount; s++)
                {
                    var a = alpha[rowBase + s];
                    if (a <= NegativeInfinity / 2)
                    {
                        continue;
                    }

                    var ns0 = NextStateWhenInput0[s];
                    var branch0 = ParityWhenInput0[s] == 0 ? u0p0 : u0p1;
                    var candidate0 = a + branch0;
                    var idx0 = nextRowBase + ns0;
                    if (candidate0 > alpha[idx0])
                    {
                        alpha[idx0] = candidate0;
                    }

                    var ns1 = NextStateWhenInput1[s];
                    var branch1 = ParityWhenInput1[s] == 0 ? u1p0 : u1p1;
                    var candidate1 = a + branch1;
                    var idx1 = nextRowBase + ns1;
                    if (candidate1 > alpha[idx1])
                    {
                        alpha[idx1] = candidate1;
                    }
                }
            }

            for (var k = n - 1; k >= 0; k--)
            {
                var rowBase = k * rowWidth;
                var nextRowBase = (k + 1) * rowWidth;
                var su = systematic[k] + apriori[k];
                var p = parity[k];
                var u0p0 = 0.5 * (su + p);
                var u0p1 = 0.5 * (su - p);
                var u1p0 = 0.5 * (-su + p);
                var u1p1 = 0.5 * (-su - p);

                for (var s = 0; s < StateCount; s++)
                {
                    var ns0 = NextStateWhenInput0[s];
                    var branch0 = ParityWhenInput0[s] == 0 ? u0p0 : u0p1;
                    var candidate0 = branch0 + beta[nextRowBase + ns0];

                    var ns1 = NextStateWhenInput1[s];
                    var branch1 = ParityWhenInput1[s] == 0 ? u1p0 : u1p1;
                    var candidate1 = branch1 + beta[nextRowBase + ns1];

                    beta[rowBase + s] = candidate0 > candidate1 ? candidate0 : candidate1;
                }
            }

            for (var k = 0; k < n; k++)
            {
                var rowBase = k * rowWidth;
                var nextRowBase = (k + 1) * rowWidth;
                var maxOne = NegativeInfinity;
                var maxZero = NegativeInfinity;
                var su = systematic[k] + apriori[k];
                var p = parity[k];
                var u0p0 = 0.5 * (su + p);
                var u0p1 = 0.5 * (su - p);
                var u1p0 = 0.5 * (-su + p);
                var u1p1 = 0.5 * (-su - p);

                for (var s = 0; s < StateCount; s++)
                {
                    var a = alpha[rowBase + s];

                    var ns0 = NextStateWhenInput0[s];
                    var branch0 = ParityWhenInput0[s] == 0 ? u0p0 : u0p1;
                    var metric0 = a + branch0 + beta[nextRowBase + ns0];
                    if (metric0 > maxZero)
                    {
                        maxZero = metric0;
                    }

                    var ns1 = NextStateWhenInput1[s];
                    var branch1 = ParityWhenInput1[s] == 0 ? u1p0 : u1p1;
                    var metric1 = a + branch1 + beta[nextRowBase + ns1];
                    if (metric1 > maxOne)
                    {
                        maxOne = metric1;
                    }
                }

                var llr = maxOne - maxZero;
                extrinsic[k] = llr - systematic[k] - apriori[k];
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(alpha, clearArray: false);
            ArrayPool<double>.Shared.Return(beta, clearArray: false);
        }
    }

    /// <summary>
    /// トレリス枝の Max-Log メトリクスを計算します。
    /// </summary>
    /// <param name="systematic">系統ビット LLR。</param>
    /// <param name="parity">パリティビット LLR。</param>
    /// <param name="apriori">事前 LLR。</param>
    /// <param name="informationBit">想定情報ビット（0/1）。</param>
    /// <param name="parityBit">想定パリティビット（0/1）。</param>
    /// <returns>枝メトリクス。</returns>
    private static double BranchMetric(double systematic, double parity, double apriori, int informationBit, int parityBit)
    {
        var uSign = informationBit == 0 ? 1.0 : -1.0;
        var pSign = parityBit == 0 ? 1.0 : -1.0;
        return 0.5 * ((systematic + apriori) * uSign + parity * pSign);
    }

    /// <summary>
    /// 状態と入力ビットからトレリス添字を計算します。
    /// </summary>
    /// <param name="state">現在状態。</param>
    /// <param name="inputBit">入力ビット（0/1）。</param>
    /// <returns>トレリス添字。</returns>
    private static int TrellisIndex(int state, int inputBit) => (state << 1) | inputBit;

    /// <summary>
    /// 入力ビット固定時の次状態テーブルを構築します。
    /// </summary>
    /// <param name="inputBit">固定する入力ビット（0/1）。</param>
    /// <returns>状態→次状態の対応表。</returns>
    private static byte[] BuildNextStateByInput(int inputBit)
    {
        var table = new byte[StateCount];
        for (var state = 0; state < StateCount; state++)
        {
            table[state] = NextState[TrellisIndex(state, inputBit)];
        }

        return table;
    }

    /// <summary>
    /// 入力ビット固定時のパリティ出力テーブルを構築します。
    /// </summary>
    /// <param name="inputBit">固定する入力ビット（0/1）。</param>
    /// <returns>状態→パリティビットの対応表。</returns>
    private static byte[] BuildParityByInput(int inputBit)
    {
        var table = new byte[StateCount];
        for (var state = 0; state < StateCount; state++)
        {
            table[state] = ParityBit[TrellisIndex(state, inputBit)];
        }

        return table;
    }

    /// <summary>
    /// トレリス添字（状態・入力）から次状態を引く表を構築します。
    /// </summary>
    /// <returns>次状態テーブル。</returns>
    private static byte[] BuildNextStateTable()
    {
        return [0, 1, 3, 2, 4, 5, 7, 6, 1, 0, 2, 3, 5, 4, 6, 7];
    }

    /// <summary>
    /// トレリス添字（状態・入力）からパリティを引く表を構築します。
    /// </summary>
    /// <returns>パリティビットテーブル。</returns>
    private static byte[] BuildParityTable()
    {
        return [0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1];
    }

    /// <summary>
    /// M 系列に基づくターボ用インターリーバを構築します。
    /// </summary>
    /// <param name="length">置換長（ビット数）。</param>
    /// <param name="seed">M 系列初期化用シード。</param>
    /// <returns>出力位置→入力位置の置換表。</returns>
    private static int[] BuildInterleaver(int length, int seed)
    {
        var permutation = new int[length];
        for (var i = 0; i < length; i++)
        {
            permutation[i] = i;
        }

        var state = MSequence31.InitializeState(MSequenceUsage.TurboEccInterleaver, seed);
        for (var i = length - 1; i > 0; i--)
        {
            var j = (int)(MSequence31.NextWord(ref state) % (uint)(i + 1));
            (permutation[i], permutation[j]) = (permutation[j], permutation[i]);
        }

        return permutation;
    }

    /// <summary>
    /// インターリーバの逆置換（デインターリーバ）を構築します。
    /// </summary>
    /// <param name="interleaver">順方向の置換表。</param>
    /// <returns>逆置換表。</returns>
    private static int[] BuildDeinterleaver(int[] interleaver)
    {
        var deinterleaver = new int[interleaver.Length];
        for (var i = 0; i < interleaver.Length; i++)
        {
            deinterleaver[interleaver[i]] = i;
        }

        return deinterleaver;
    }

    /// <summary>
    /// ビット列を置換表に従ってインターリーブします。
    /// </summary>
    /// <param name="input">入力ビット列。</param>
    /// <param name="permutation">出力位置→入力位置の置換表。</param>
    /// <returns>インターリーブ後のビット列。</returns>
    private static bool[] InterleaveBits(bool[] input, int[] permutation)
    {
        var output = new bool[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            output[i] = input[permutation[i]];
        }

        return output;
    }

    /// <summary>
    /// 倍精度列を置換表に従ってインターリーブします。
    /// </summary>
    /// <param name="input">入力列。</param>
    /// <param name="permutation">出力位置→入力位置の置換表。</param>
    /// <returns>インターリーブ後の列。</returns>
    private static double[] InterleaveDoubles(double[] input, int[] permutation)
    {
        var output = new double[input.Length];
        InterleaveDoublesInto(input, permutation, output);

        return output;
    }

    /// <summary>
    /// 倍精度列を逆置換表に従ってデインターリーブします。
    /// </summary>
    /// <param name="input">入力列。</param>
    /// <param name="deinterleaver">逆置換表。</param>
    /// <returns>デインターリーブ後の列。</returns>
    private static double[] DeinterleaveDoubles(double[] input, int[] deinterleaver)
    {
        var output = new double[input.Length];
        DeinterleaveDoublesInto(input, deinterleaver, output);

        return output;
    }

    /// <summary>
    /// 倍精度列を置換表に従ってインターリーブし、出力先へ書き込みます。
    /// </summary>
    /// <param name="input">入力列。</param>
    /// <param name="permutation">出力位置→入力位置の置換表。</param>
    /// <param name="output">インターリーブ結果の出力先。</param>
    private static void InterleaveDoublesInto(ReadOnlySpan<double> input, int[] permutation, Span<double> output)
    {
        for (var i = 0; i < permutation.Length; i++)
        {
            output[i] = input[permutation[i]];
        }
    }

    /// <summary>
    /// 倍精度列を逆置換表に従ってデインターリーブし、出力先へ書き込みます。
    /// </summary>
    /// <param name="input">入力列。</param>
    /// <param name="deinterleaver">逆置換表。</param>
    /// <param name="output">デインターリーブ結果の出力先。</param>
    private static void DeinterleaveDoublesInto(ReadOnlySpan<double> input, int[] deinterleaver, Span<double> output)
    {
        for (var i = 0; i < deinterleaver.Length; i++)
        {
            output[i] = input[deinterleaver[i]];
        }
    }

    /// <summary>
    /// ビット列をチャネル信頼度付き LLR に変換します（0→+R / 1→−R）。
    /// </summary>
    /// <param name="bits">ビット列。</param>
    /// <param name="reliability">LLR の絶対値（チャネル信頼度）。</param>
    /// <returns>LLR 列。</returns>
    private static double[] BitsToLlr(bool[] bits, double reliability)
    {
        var llr = new double[bits.Length];
        for (var i = 0; i < bits.Length; i++)
        {
            llr[i] = bits[i] ? -reliability : reliability;
        }

        return llr;
    }

    /// <summary>
    /// バイト列を MSB 先頭のビット列へ展開します。
    /// </summary>
    /// <param name="bytes">バイト列。</param>
    /// <returns>展開後のビット列。</returns>
    private static bool[] BytesToBits(byte[] bytes)
    {
        var bits = new bool[bytes.Length * 8];
        var bitBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(bits.AsSpan());
        for (var i = 0; i < bytes.Length; i++)
        {
            var srcOffset = bytes[i] * 8;
            var dstOffset = i * 8;
            ref var src = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(ByteToBitsLookup.AsSpan(srcOffset, 8));
            ref var dst = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(bitBytes.Slice(dstOffset, 8));
            System.Runtime.CompilerServices.Unsafe.WriteUnaligned(
                ref dst,
                System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>(ref src));
        }

        return bits;
    }

    /// <summary>
    /// 0..255 の各バイトを 8 ビット展開したルックアップ表を構築します。
    /// </summary>
    /// <returns>バイト値×8 要素のビット展開テーブル。</returns>
    private static byte[] BuildByteToBitsLookup()
    {
        var lookup = new byte[256 * 8];
        for (var value = 0; value < 256; value++)
        {
            var offset = value * 8;
            lookup[offset] = (byte)((value >> 7) & 1);
            lookup[offset + 1] = (byte)((value >> 6) & 1);
            lookup[offset + 2] = (byte)((value >> 5) & 1);
            lookup[offset + 3] = (byte)((value >> 4) & 1);
            lookup[offset + 4] = (byte)((value >> 3) & 1);
            lookup[offset + 5] = (byte)((value >> 2) & 1);
            lookup[offset + 6] = (byte)((value >> 1) & 1);
            lookup[offset + 7] = (byte)(value & 1);
        }

        return lookup;
    }

    /// <summary>
    /// ビット列を MSB 先頭でバイト列へ詰めます（長さは 8 の倍数前提）。
    /// </summary>
    /// <param name="bits">ビット列。</param>
    /// <returns>パック後のバイト列。</returns>
    private static byte[] BitsToBytes(bool[] bits)
    {
        var bytes = new byte[bits.Length / 8];
        var bitBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(bits.AsSpan());
        for (var i = 0; i < bytes.Length; i++)
        {
            var srcOffset = i * 8;
            ref var src = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(bitBytes.Slice(srcOffset, 8));
            var laneBits = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>(ref src) & 0x0101010101010101UL;
            bytes[i] = (byte)((laneBits * 0x8040201008040201UL) >> 56);
        }

        return bytes;
    }

    /// <summary>
    /// ビット列を MSB 先頭でバイト列へパックします（端数ビットあり可）。
    /// </summary>
    /// <param name="bits">ビット列。</param>
    /// <returns>パック後のバイト列。</returns>
    private static byte[] PackBits(bool[] bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        var bitBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(bits.AsSpan());
        var fullByteCount = bits.Length / 8;
        for (var i = 0; i < fullByteCount; i++)
        {
            var srcOffset = i * 8;
            ref var src = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(bitBytes.Slice(srcOffset, 8));
            var laneBits = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>(ref src) & 0x0101010101010101UL;
            bytes[i] = (byte)((laneBits * 0x8040201008040201UL) >> 56);
        }

        var remaining = bits.Length - (fullByteCount * 8);
        if (remaining > 0)
        {
            byte value = 0;
            var baseBit = fullByteCount * 8;
            for (var b = 0; b < remaining; b++)
            {
                if (bits[baseBit + b])
                {
                    value |= (byte)(1 << (7 - b));
                }
            }

            bytes[fullByteCount] = value;
        }

        return bytes;
    }

    /// <summary>
    /// パック済みバイト列から指定ビット数を MSB 先頭で展開します。
    /// </summary>
    /// <param name="bytes">パック済みバイト列。</param>
    /// <param name="bitCount">展開するビット数。</param>
    /// <returns>展開後のビット列。</returns>
    private static bool[] UnpackBits(byte[] bytes, int bitCount)
    {
        var bits = new bool[bitCount];
        var bitBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(bits.AsSpan());
        var fullByteCount = bitCount / 8;
        for (var i = 0; i < fullByteCount; i++)
        {
            var srcOffset = bytes[i] * 8;
            var dstOffset = i * 8;
            ref var src = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(ByteToBitsLookup.AsSpan(srcOffset, 8));
            ref var dst = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(bitBytes.Slice(dstOffset, 8));
            System.Runtime.CompilerServices.Unsafe.WriteUnaligned(
                ref dst,
                System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>(ref src));
        }

        var remaining = bitCount - (fullByteCount * 8);
        if (remaining > 0)
        {
            var srcOffset = bytes[fullByteCount] * 8;
            var baseBit = fullByteCount * 8;
            for (var b = 0; b < remaining; b++)
            {
                bits[baseBit + b] = ByteToBitsLookup[srcOffset + b] != 0;
            }
        }

        return bits;
    }

    /// <summary>
    /// 2つのバイト配列の不一致バイト数を返します。
    /// </summary>
    /// <param name="left">比較元バイト配列。</param>
    /// <param name="right">比較先バイト配列。</param>
    /// <returns>不一致バイト数。</returns>
    private static int CountDifferentBytes(byte[] left, byte[] right)
    {
        return CountDifferentByteSpans(left, right);
    }

    /// <summary>
    /// 2つのビット配列の不一致ビット数を返します。
    /// </summary>
    /// <param name="left">比較元ビット配列。</param>
    /// <param name="right">比較先ビット配列。</param>
    /// <returns>不一致ビット数。</returns>
    private static int CountDifferentBits(bool[] left, bool[] right)
    {
        ReadOnlySpan<byte> leftBytes = MemoryMarshal.AsBytes(left.AsSpan());
        ReadOnlySpan<byte> rightBytes = MemoryMarshal.AsBytes(right.AsSpan());
        return CountDifferentByteSpans(leftBytes, rightBytes);
    }

    /// <summary>
    /// ビット配列とビットスパンの不一致ビット数を返します。
    /// </summary>
    /// <param name="left">比較元ビット配列。</param>
    /// <param name="right">比較先ビットスパン。</param>
    /// <returns>不一致ビット数。</returns>
    private static int CountDifferentBits(bool[] left, ReadOnlySpan<bool> right)
    {
        ReadOnlySpan<byte> leftBytes = MemoryMarshal.AsBytes(left.AsSpan());
        ReadOnlySpan<byte> rightBytes = MemoryMarshal.AsBytes(right);
        return CountDifferentByteSpans(leftBytes, rightBytes);
    }

    /// <summary>
    /// バイト列とビット列を MSB 先頭で突き合わせ、不一致ビット数を返します。
    /// </summary>
    /// <param name="bytes">比較元バイト列。</param>
    /// <param name="bits">比較先ビット列。</param>
    /// <returns>不一致ビット数。</returns>
    private static int CountDifferentBytesAgainstBits(ReadOnlySpan<byte> bytes, ReadOnlySpan<bool> bits)
    {
        var different = 0;
        var bitIndex = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            var value = bytes[i];
            for (var b = 0; b < 8; b++)
            {
                var bit = ((value >> (7 - b)) & 1) != 0;
                if (bit != bits[bitIndex++])
                {
                    different++;
                }
            }
        }

        return different;
    }

    /// <summary>
    /// パック済みバイト列から MSB 先頭で 1 ビットを読み取ります。
    /// </summary>
    /// <param name="packed">パック済みバイト列。</param>
    /// <param name="bitIndex">読み取るビット位置（0 始まり）。</param>
    /// <returns>ビット値（1 なら true）。</returns>
    private static bool ReadPackedBit(ReadOnlySpan<byte> packed, int bitIndex)
    {
        var b = packed[bitIndex >> 3];
        return ((b >> (7 - (bitIndex & 7))) & 1) != 0;
    }

    /// <summary>
    /// 2つのバイトスパンの不一致要素数を数えます。
    /// </summary>
    /// <param name="left">比較元バイトスパン。</param>
    /// <param name="right">比較先バイトスパン。</param>
    /// <returns>不一致要素数。</returns>
    private static int CountDifferentByteSpans(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var length = left.Length;
        var different = 0;
        ref var leftRef = ref MemoryMarshal.GetReference(left);
        ref var rightRef = ref MemoryMarshal.GetReference(right);

        if (Avx2.IsSupported)
        {
            var i = 0;
            const int width = 32;
            for (; i <= length - width; i += width)
            {
                var a = Vector256.LoadUnsafe(ref leftRef, (nuint)i);
                var b = Vector256.LoadUnsafe(ref rightRef, (nuint)i);
                var eq = Avx2.CompareEqual(a, b);
                var equalMask = (uint)Avx2.MoveMask(eq);
                different += width - BitOperations.PopCount(equalMask);
            }

            for (; i < length; i++)
            {
                if (left[i] != right[i])
                {
                    different++;
                }
            }

            return different;
        }
        else if (AdvSimd.IsSupported)
        {
            var i = 0;
            const int width = 16;
            Span<byte> equalLanes = stackalloc byte[width];
            for (; i <= length - width; i += width)
            {
                var a = Vector128.LoadUnsafe(ref leftRef, (nuint)i);
                var b = Vector128.LoadUnsafe(ref rightRef, (nuint)i);
                var eq = AdvSimd.CompareEqual(a, b);
                var normalizedEq = AdvSimd.ShiftRightLogical(eq, 7);
                normalizedEq.CopyTo(equalLanes);

                var equalCount = 0;
                for (var lane = 0; lane < width; lane++)
                {
                    equalCount += equalLanes[lane];
                }

                different += width - equalCount;
            }

            for (; i < length; i++)
            {
                if (left[i] != right[i])
                {
                    different++;
                }
            }

            return different;
        }

        for (var i = 0; i < length; i++)
        {
            if (left[i] != right[i])
            {
                different++;
            }
        }

        return different;
    }
}


