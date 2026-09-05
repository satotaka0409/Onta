namespace Onta.Core;

/// <summary>
/// 固定 1024 バイトのペイロードを扱うターボ符号エンコーダ／デコーダです。
/// </summary>
public static class TurboEcc1024
{
    /// <summary>
    /// 復号時の補正統計を表します。
    /// </summary>
    public readonly record struct DecodeMetrics(
        int CorrectedBitCount,
        int CorrectedByteCount,
        int PayloadBitLength,
        double CorrectionRate);

    /// <summary>
    /// <see cref="Encode(byte[])"/> が受け付けるペイロード長（バイト）です。
    /// </summary>
    public const int DataUnitBytes = 1024;

    /// <summary>
    /// ペイロード長（ビット）です。
    /// </summary>
    public const int DataUnitBits = DataUnitBytes * 8;

    /// <summary>
    /// 符号化後のビット長（符号化率 1/3）です。
    /// </summary>
    public const int EncodedBits = DataUnitBits * 3;

    /// <summary>
    /// 符号化後のバイト長です。
    /// </summary>
    public const int EncodedBytes = EncodedBits / 8;

    private const int StateCount = 8;
    private const double NegativeInfinity = -1e30;

    private static readonly int[] Interleaver = BuildInterleaver(DataUnitBits, seed: 20260903);
    private static readonly int[] Deinterleaver = BuildDeinterleaver(Interleaver);
    private static readonly int[,] NextState = BuildNextStateTable();
    private static readonly int[,] ParityBit = BuildParityTable();

    /// <summary>
    /// 1024 バイトのペイロードを符号化率 1/3 のターボ符号語へ変換します。
    /// </summary>
    /// <param name="data1024">ペイロード。長さは <see cref="DataUnitBytes"/> である必要があります。</param>
    /// <returns>長さ <see cref="EncodedBytes"/> の符号化バイト列。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data1024"/> が null の場合にスローされます。</exception>
    /// <exception cref="ArgumentException">入力長が不正な場合にスローされます。</exception>
    public static byte[] Encode(byte[] data1024)
    {
        ArgumentNullException.ThrowIfNull(data1024);
        if (data1024.Length != DataUnitBytes)
        {
            throw new ArgumentException($"Input must be exactly {DataUnitBytes} bytes.", nameof(data1024));
        }

        // 符号化率 1/3: 系統ビット + 2 本の RSC 構成符号器のパリティで構成する。
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
    /// 反復 Max-Log-MAP 構成復号により、ターボ符号化ブロックを復号します。
    /// </summary>
    /// <param name="encoded">符号化データ。長さは <see cref="EncodedBytes"/> である必要があります。</param>
    /// <param name="iterations">反復回数。0 より大きい必要があります。</param>
    /// <param name="channelReliability">初期 LLR 変換に用いるチャネル信頼度。0 より大きい必要があります。</param>
    /// <returns>長さ <see cref="DataUnitBytes"/> の復号ペイロード。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoded"/> が null の場合にスローされます。</exception>
    /// <exception cref="ArgumentException">いずれかの引数が有効範囲外の場合にスローされます。</exception>
    public static byte[] Decode(byte[] encoded, int iterations = 6, double channelReliability = 2.0)
    {
        return Decode(encoded, out _, iterations, channelReliability);
    }

    /// <summary>
    /// 反復 Max-Log-MAP 構成復号により、ターボ符号化ブロックを復号し補正統計も返します。
    /// </summary>
    /// <param name="encoded">符号化データ。長さは <see cref="EncodedBytes"/> である必要があります。</param>
    /// <param name="metrics">補正統計。<see cref="DecodeMetrics.CorrectionRate"/> は 8192 ビットに対する補正率です。</param>
    /// <param name="iterations">反復回数。0 より大きい必要があります。</param>
    /// <param name="channelReliability">初期 LLR 変換に用いるチャネル信頼度。0 より大きい必要があります。</param>
    /// <returns>長さ <see cref="DataUnitBytes"/> の復号ペイロード。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoded"/> が null の場合にスローされます。</exception>
    /// <exception cref="ArgumentException">いずれかの引数が有効範囲外の場合にスローされます。</exception>
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

        // 直列化された 3 要素組を、系統ビット・パリティ1・パリティ2へ分離する。
        var triples = UnpackBits(encoded, EncodedBits);
        var sysBits = new bool[DataUnitBits];
        var p1Bits = new bool[DataUnitBits];
        var p2Bits = new bool[DataUnitBits];

        for (var i = 0; i < DataUnitBits; i++)
        {
            var baseIndex = i * 3;
            sysBits[i] = triples[baseIndex];
            p1Bits[i] = triples[baseIndex + 1];
            p2Bits[i] = triples[baseIndex + 2];
        }

        // ハード判定ビットを簡易チャネル LLR に変換する。正は 0 優勢、負は 1 優勢。
        var systematic = BitsToLlr(sysBits, channelReliability);
        var parity1 = BitsToLlr(p1Bits, channelReliability);
        var parity2 = BitsToLlr(p2Bits, channelReliability);
        return DecodeFromComponentLlrs(systematic, parity1, parity2, sysBits, iterations, out metrics);
    }

    /// <summary>
    /// チャネル LLR（長さ <see cref="EncodedBits"/>、正=ビット0・負=ビット1）からターボ復号します。
    /// </summary>
    public static byte[] DecodeFromChannelLlrs(
        ReadOnlySpan<double> encodedBitLlrs,
        int iterations = 12)
    {
        return DecodeFromChannelLlrs(encodedBitLlrs, out _, iterations);
    }

    /// <summary>
    /// チャネル LLR からターボ復号し、補正統計も返します。
    /// </summary>
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

        var systematic = new double[DataUnitBits];
        var parity1 = new double[DataUnitBits];
        var parity2 = new double[DataUnitBits];
        var sysBits = new bool[DataUnitBits];
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

    private static byte[] DecodeFromComponentLlrs(
        double[] systematic,
        double[] parity1,
        double[] parity2,
        bool[] sysBits,
        int iterations,
        out DecodeMetrics metrics)
    {
        var systematicInterleaved = InterleaveDoubles(systematic, Interleaver);
        var apriori1 = new double[DataUnitBits];

        // 2 つの構成復号器間で外部情報を反復交換する。
        for (var iter = 0; iter < iterations; iter++)
        {
            var extrinsic1 = DecodeSisoMaxLogMap(systematic, parity1, apriori1);
            var apriori2 = InterleaveDoubles(extrinsic1, Interleaver);
            var extrinsic2 = DecodeSisoMaxLogMap(systematicInterleaved, parity2, apriori2);
            apriori1 = DeinterleaveDoubles(extrinsic2, Deinterleaver);
        }

        var posterior = new double[DataUnitBits];
        for (var i = 0; i < DataUnitBits; i++)
        {
            posterior[i] = systematic[i] + apriori1[i];
        }

        var decodedBits = new bool[DataUnitBits];
        for (var i = 0; i < DataUnitBits; i++)
        {
            decodedBits[i] = posterior[i] < 0.0;
        }

        var correctedBitCount = CountDifferentBits(decodedBits, sysBits);
        var decodedBytes = BitsToBytes(decodedBits);
        var systematicBytes = BitsToBytes(sysBits);
        var correctedByteCount = CountDifferentBytes(decodedBytes, systematicBytes);

        metrics = new DecodeMetrics(
            CorrectedBitCount: correctedBitCount,
            CorrectedByteCount: correctedByteCount,
            PayloadBitLength: DataUnitBits,
            CorrectionRate: (double)correctedBitCount / DataUnitBits);

        return decodedBytes;
    }

    /// <summary>
    /// ターボ符号化ブロックの復号を試みます。
    /// </summary>
    /// <param name="encoded">符号化データ。長さは <see cref="EncodedBytes"/> を想定します。</param>
    /// <param name="decoded1024">成功時は復号ペイロード、失敗時は空配列。</param>
    /// <param name="iterations">反復回数。</param>
    /// <param name="channelReliability">初期 LLR 変換に用いるチャネル信頼度。</param>
    /// <returns>復号成功時は <see langword="true"/>、それ以外は <see langword="false"/>。</returns>
    public static bool TryDecode(byte[] encoded, out byte[] decoded1024, int iterations = 6, double channelReliability = 2.0)
    {
        return TryDecode(encoded, out decoded1024, out _, iterations, channelReliability);
    }

    /// <summary>
    /// ターボ符号化ブロックの復号を試み、補正統計も返します。
    /// </summary>
    /// <param name="encoded">符号化データ。長さは <see cref="EncodedBytes"/> を想定します。</param>
    /// <param name="decoded1024">成功時は復号ペイロード、失敗時は空配列。</param>
    /// <param name="metrics">成功時は補正統計、失敗時は既定値。</param>
    /// <param name="iterations">反復回数。</param>
    /// <param name="channelReliability">初期 LLR 変換に用いるチャネル信頼度。</param>
    /// <returns>復号成功時は <see langword="true"/>、それ以外は <see langword="false"/>。</returns>
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

    private static bool[] EncodeRscParity(bool[] inputBits)
    {
        // 再帰的系統畳み込み符号器を 1 本実行し、パリティのみを出力する。
        var parity = new bool[inputBits.Length];
        var state = 0;

        for (var i = 0; i < inputBits.Length; i++)
        {
            var u = inputBits[i] ? 1 : 0;
            parity[i] = ParityBit[state, u] == 1;
            state = NextState[state, u];
        }

        return parity;
    }

    private static double[] DecodeSisoMaxLogMap(double[] systematic, double[] parity, double[] apriori)
    {
        var n = systematic.Length;
        var alpha = new double[n + 1, StateCount];
        var beta = new double[n + 1, StateCount];

        // 前向き（alpha）および後ろ向き（beta）の状態メトリクス。
        for (var s = 0; s < StateCount; s++)
        {
            alpha[0, s] = s == 0 ? 0.0 : NegativeInfinity;
            beta[n, s] = 0.0;
        }

        for (var k = 0; k < n; k++)
        {
            for (var ns = 0; ns < StateCount; ns++)
            {
                alpha[k + 1, ns] = NegativeInfinity;
            }

            for (var s = 0; s < StateCount; s++)
            {
                var a = alpha[k, s];
                if (a <= NegativeInfinity / 2)
                {
                    continue;
                }

                for (var u = 0; u <= 1; u++)
                {
                    var ns = NextState[s, u];
                    var branch = BranchMetric(systematic[k], parity[k], apriori[k], u, ParityBit[s, u]);
                    var candidate = a + branch;
                    if (candidate > alpha[k + 1, ns])
                    {
                        alpha[k + 1, ns] = candidate;
                    }
                }
            }
        }

        for (var k = n - 1; k >= 0; k--)
        {
            for (var s = 0; s < StateCount; s++)
            {
                var best = NegativeInfinity;
                for (var u = 0; u <= 1; u++)
                {
                    var ns = NextState[s, u];
                    var branch = BranchMetric(systematic[k], parity[k], apriori[k], u, ParityBit[s, u]);
                    var candidate = branch + beta[k + 1, ns];
                    if (candidate > best)
                    {
                        best = candidate;
                    }
                }

                beta[k, s] = best;
            }
        }

        // 各ビット位置の Max-Log-MAP LLR と外部情報を算出する。
        var extrinsic = new double[n];
        for (var k = 0; k < n; k++)
        {
            var maxOne = NegativeInfinity;
            var maxZero = NegativeInfinity;

            for (var s = 0; s < StateCount; s++)
            {
                for (var u = 0; u <= 1; u++)
                {
                    var ns = NextState[s, u];
                    var branch = BranchMetric(systematic[k], parity[k], apriori[k], u, ParityBit[s, u]);
                    var metric = alpha[k, s] + branch + beta[k + 1, ns];

                    if (u == 1)
                    {
                        if (metric > maxOne)
                        {
                            maxOne = metric;
                        }
                    }
                    else
                    {
                        if (metric > maxZero)
                        {
                            maxZero = metric;
                        }
                    }
                }
            }

            var llr = maxOne - maxZero;
            extrinsic[k] = llr - systematic[k] - apriori[k];
        }

        return extrinsic;
    }

    private static double BranchMetric(double systematic, double parity, double apriori, int informationBit, int parityBit)
    {
        var uSign = informationBit == 0 ? 1.0 : -1.0;
        var pSign = parityBit == 0 ? 1.0 : -1.0;
        return 0.5 * ((systematic + apriori) * uSign + parity * pSign);
    }

    private static int[,] BuildNextStateTable()
    {
        // 記憶素子 3 段の RSC 符号器に対するトレリス遷移表。
        var table = new int[StateCount, 2];
        for (var state = 0; state < StateCount; state++)
        {
            var d1 = state & 1;
            var d2 = (state >> 1) & 1;
            var d3 = (state >> 2) & 1;

            for (var u = 0; u <= 1; u++)
            {
                var feedback = u ^ d1 ^ d3;
                var next = feedback | (d1 << 1) | (d2 << 2);
                table[state, u] = next;
            }
        }

        return table;
    }

    private static int[,] BuildParityTable()
    {
        // 各（状態, 入力）ペアに対するトレリスのパリティ出力表。
        var table = new int[StateCount, 2];
        for (var state = 0; state < StateCount; state++)
        {
            var d1 = state & 1;
            var d2 = (state >> 1) & 1;
            var d3 = (state >> 2) & 1;

            for (var u = 0; u <= 1; u++)
            {
                var feedback = u ^ d1 ^ d3;
                var parity = feedback ^ d2 ^ d3;
                table[state, u] = parity;
            }
        }

        return table;
    }

    private static int[] BuildInterleaver(int length, int seed)
    {
        // 固定シードの Fisher-Yates で、再現可能なインタリーバ順列を生成する。
        var permutation = new int[length];
        for (var i = 0; i < length; i++)
        {
            permutation[i] = i;
        }

        var random = new Random(seed);
        for (var i = length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (permutation[i], permutation[j]) = (permutation[j], permutation[i]);
        }

        return permutation;
    }

    private static int[] BuildDeinterleaver(int[] interleaver)
    {
        var deinterleaver = new int[interleaver.Length];
        for (var i = 0; i < interleaver.Length; i++)
        {
            deinterleaver[interleaver[i]] = i;
        }

        return deinterleaver;
    }

    private static bool[] InterleaveBits(bool[] input, int[] permutation)
    {
        var output = new bool[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            output[i] = input[permutation[i]];
        }

        return output;
    }

    private static double[] InterleaveDoubles(double[] input, int[] permutation)
    {
        var output = new double[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            output[i] = input[permutation[i]];
        }

        return output;
    }

    private static double[] DeinterleaveDoubles(double[] input, int[] deinterleaver)
    {
        var output = new double[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            output[i] = input[deinterleaver[i]];
        }

        return output;
    }

    private static double[] BitsToLlr(bool[] bits, double reliability)
    {
        // 簡易復号器で使う、ハード判定から疑似 LLR への写像。
        var llr = new double[bits.Length];
        for (var i = 0; i < bits.Length; i++)
        {
            llr[i] = bits[i] ? -reliability : reliability;
        }

        return llr;
    }

    private static bool[] BytesToBits(byte[] bytes)
    {
        var bits = new bool[bytes.Length * 8];
        for (var i = 0; i < bytes.Length; i++)
        {
            for (var b = 0; b < 8; b++)
            {
                bits[(i * 8) + b] = ((bytes[i] >> (7 - b)) & 1) == 1;
            }
        }

        return bits;
    }

    private static byte[] BitsToBytes(bool[] bits)
    {
        var bytes = new byte[bits.Length / 8];
        for (var i = 0; i < bytes.Length; i++)
        {
            byte value = 0;
            for (var b = 0; b < 8; b++)
            {
                if (bits[(i * 8) + b])
                {
                    value |= (byte)(1 << (7 - b));
                }
            }

            bytes[i] = value;
        }

        return bytes;
    }

    private static byte[] PackBits(bool[] bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
        {
            if (!bits[i])
            {
                continue;
            }

            var byteIndex = i / 8;
            var bitIndex = 7 - (i % 8);
            bytes[byteIndex] |= (byte)(1 << bitIndex);
        }

        return bytes;
    }

    private static bool[] UnpackBits(byte[] bytes, int bitCount)
    {
        var bits = new bool[bitCount];
        for (var i = 0; i < bitCount; i++)
        {
            var byteIndex = i / 8;
            var bitIndex = 7 - (i % 8);
            bits[i] = ((bytes[byteIndex] >> bitIndex) & 1) == 1;
        }

        return bits;
    }

    private static int CountDifferentBytes(byte[] left, byte[] right)
    {
        var different = 0;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                different++;
            }
        }

        return different;
    }

    private static int CountDifferentBits(bool[] left, bool[] right)
    {
        var different = 0;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                different++;
            }
        }

        return different;
    }
}
