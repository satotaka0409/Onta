using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

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
    private static readonly byte[] NextState = BuildNextStateTable();
    private static readonly byte[] ParityBit = BuildParityTable();
    private static readonly byte[] NextStateWhenInput0 = BuildNextStateByInput(inputBit: 0);
    private static readonly byte[] NextStateWhenInput1 = BuildNextStateByInput(inputBit: 1);
    private static readonly byte[] ParityWhenInput0 = BuildParityByInput(inputBit: 0);
    private static readonly byte[] ParityWhenInput1 = BuildParityByInput(inputBit: 1);
    private static readonly byte[] ByteToBitsLookup = BuildByteToBitsLookup();

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

            var su = systematic[k] + apriori[k];
            var p = parity[k];
            var u0p0 = 0.5 * (su + p);
            var u0p1 = 0.5 * (su - p);
            var u1p0 = 0.5 * (-su + p);
            var u1p1 = 0.5 * (-su - p);

            for (var s = 0; s < StateCount; s++)
            {
                var a = alpha[k, s];
                if (a <= NegativeInfinity / 2)
                {
                    continue;
                }

                var ns0 = NextStateWhenInput0[s];
                var branch0 = ParityWhenInput0[s] == 0 ? u0p0 : u0p1;
                var candidate0 = a + branch0;
                if (candidate0 > alpha[k + 1, ns0])
                {
                    alpha[k + 1, ns0] = candidate0;
                }

                var ns1 = NextStateWhenInput1[s];
                var branch1 = ParityWhenInput1[s] == 0 ? u1p0 : u1p1;
                var candidate1 = a + branch1;
                if (candidate1 > alpha[k + 1, ns1])
                {
                    alpha[k + 1, ns1] = candidate1;
                }
            }
        }

        for (var k = n - 1; k >= 0; k--)
        {
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
                var candidate0 = branch0 + beta[k + 1, ns0];

                var ns1 = NextStateWhenInput1[s];
                var branch1 = ParityWhenInput1[s] == 0 ? u1p0 : u1p1;
                var candidate1 = branch1 + beta[k + 1, ns1];

                beta[k, s] = candidate0 > candidate1 ? candidate0 : candidate1;
            }
        }

        // 各ビット位置の Max-Log-MAP LLR と外部情報を算出する。
        var extrinsic = new double[n];
        for (var k = 0; k < n; k++)
        {
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
                var a = alpha[k, s];

                var ns0 = NextStateWhenInput0[s];
                var branch0 = ParityWhenInput0[s] == 0 ? u0p0 : u0p1;
                var metric0 = a + branch0 + beta[k + 1, ns0];
                if (metric0 > maxZero)
                {
                    maxZero = metric0;
                }

                var ns1 = NextStateWhenInput1[s];
                var branch1 = ParityWhenInput1[s] == 0 ? u1p0 : u1p1;
                var metric1 = a + branch1 + beta[k + 1, ns1];
                if (metric1 > maxOne)
                {
                    maxOne = metric1;
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

    private static int TrellisIndex(int state, int inputBit) => (state << 1) | inputBit;

    private static byte[] BuildNextStateByInput(int inputBit)
    {
        var table = new byte[StateCount];
        for (var state = 0; state < StateCount; state++)
        {
            table[state] = NextState[TrellisIndex(state, inputBit)];
        }

        return table;
    }

    private static byte[] BuildParityByInput(int inputBit)
    {
        var table = new byte[StateCount];
        for (var state = 0; state < StateCount; state++)
        {
            table[state] = ParityBit[TrellisIndex(state, inputBit)];
        }

        return table;
    }

    private static byte[] BuildNextStateTable()
    {
        // 記憶素子 3 段の RSC 符号器に対するトレリス遷移表。
        return [0, 1, 3, 2, 4, 5, 7, 6, 1, 0, 2, 3, 5, 4, 6, 7];
    }

    private static byte[] BuildParityTable()
    {
        // 各（状態, 入力）ペアに対するトレリスのパリティ出力表。
        return [0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1];
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

    private static int CountDifferentBytes(byte[] left, byte[] right)
    {
        return CountDifferentByteSpans(left, right);
    }

    private static int CountDifferentBits(bool[] left, bool[] right)
    {
        ReadOnlySpan<byte> leftBytes = MemoryMarshal.AsBytes(left.AsSpan());
        ReadOnlySpan<byte> rightBytes = MemoryMarshal.AsBytes(right.AsSpan());
        return CountDifferentByteSpans(leftBytes, rightBytes);
    }

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
                // 0xFF/0x00 を 1/0 に正規化してから lane 合計で一致数を得る。
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
