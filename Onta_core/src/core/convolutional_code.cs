using System;

namespace Otofa.Core;

/// <summary>
/// レート 1/2 の畳み込み符号（拘束長 7, 生成多項式 171/133 octal）を提供します。
/// </summary>
public static class ConvolutionalCode
{
    /// <summary>
    /// 拘束長 K（シフトレジスタ全体の段数）です。
    /// </summary>
    public const int ConstraintLength = 7;

    /// <summary>
    /// 1 入力ビットあたりの出力ビット数です（レート 1/2 なので 2）。
    /// </summary>
    public const int OutputBitsPerInputBit = 2;

    /// <summary>
    /// 生成多項式（八進）です。標準的な 171/133 を使用します。
    /// </summary>
    public static readonly int[] GeneratorPolynomialsOctal = { 0b1111001, 0b1011011 };

    private const int MemoryBits = ConstraintLength - 1;
    private const int TailBits = ConstraintLength - 1;
    private const int StateCount = 1 << MemoryBits;
    private const int StateMask = StateCount - 1;
    private const int LargeMetric = 1_000_000_000;

    /// <summary>
    /// 復号時の統計情報を表します。
    /// </summary>
    public readonly record struct DecodeMetrics(
        int PathHammingDistance,
        int ComparedCodeBitCount,
        int CorrectedCodeBitCount,
        double CorrectionRate);

    /// <summary>
    /// 入力バイト列を畳み込み符号化し、ビットをパックしたバイト列で返します。
    /// </summary>
    /// <param name="data">符号化対象の入力データ。</param>
    /// <param name="terminate">true の場合、末尾にテールビット（0）を追加して終端状態へ収束させます。</param>
    /// <returns>符号化ビット列（MSB-first）をパックしたバイト列。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> が null の場合にスローされます。</exception>
    public static byte[] Encode(byte[] data, bool terminate = true)
    {
        ArgumentNullException.ThrowIfNull(data);

        var inputBits = BytesToBits(data);
        var totalInputBits = inputBits.Length + (terminate ? TailBits : 0);
        var encodedBits = new bool[totalInputBits * OutputBitsPerInputBit];

        var state = 0;
        var writeIndex = 0;

        for (var i = 0; i < inputBits.Length; i++)
        {
            var inputBit = inputBits[i] ? 1 : 0;
            EncodeOneBit(inputBit, ref state, encodedBits, ref writeIndex);
        }

        // 終端付きの場合はゼロ入力を TailBits 回流し、状態を 0 へ寄せる。
        if (terminate)
        {
            for (var i = 0; i < TailBits; i++)
            {
                EncodeOneBit(0, ref state, encodedBits, ref writeIndex);
            }
        }

        return PackBits(encodedBits);
    }

    /// <summary>
    /// 符号化ビット列を Viterbi（ハード判定）で復号し、元のバイト列を返します。
    /// </summary>
    /// <param name="encoded">符号化データ（ビットパック済み）。</param>
    /// <param name="originalByteLength">復号後に期待する元データ長（バイト）。</param>
    /// <param name="terminated">符号化時にテール終端を付与したかどうか。</param>
    /// <returns>復号データ。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoded"/> が null の場合にスローされます。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="originalByteLength"/> が負値の場合にスローされます。</exception>
    /// <exception cref="ArgumentException">入力ビット長が期待フォーマットと一致しない場合にスローされます。</exception>
    public static byte[] Decode(byte[] encoded, int originalByteLength, bool terminated = true)
    {
        return Decode(encoded, originalByteLength, out _, terminated);
    }

    /// <summary>
    /// 符号化ビット列を Viterbi（ハード判定）で復号し、復号統計とともに返します。
    /// </summary>
    /// <param name="encoded">符号化データ（ビットパック済み）。</param>
    /// <param name="originalByteLength">復号後に期待する元データ長（バイト）。</param>
    /// <param name="metrics">復号統計。訂正率は符号語ビットに対する差分率です。</param>
    /// <param name="terminated">符号化時にテール終端を付与したかどうか。</param>
    /// <returns>復号データ。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoded"/> が null の場合にスローされます。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="originalByteLength"/> が負値の場合にスローされます。</exception>
    /// <exception cref="ArgumentException">入力ビット長が期待フォーマットと一致しない場合にスローされます。</exception>
    public static byte[] Decode(byte[] encoded, int originalByteLength, out DecodeMetrics metrics, bool terminated = true)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (originalByteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(originalByteLength));
        }

        var originalBitLength = originalByteLength * 8;
        var expectedInputBits = originalBitLength + (terminated ? TailBits : 0);
        var expectedCodeBitLength = expectedInputBits * OutputBitsPerInputBit;
        var encodedBits = UnpackBits(encoded, expectedCodeBitLength);

        var inputSymbolCount = expectedCodeBitLength / OutputBitsPerInputBit;
        var prevMetric = new int[StateCount];
        var nextMetric = new int[StateCount];
        var predecessorState = new int[inputSymbolCount, StateCount];
        var decidedInputBit = new byte[inputSymbolCount, StateCount];

        for (var s = 0; s < StateCount; s++)
        {
            prevMetric[s] = LargeMetric;
            nextMetric[s] = LargeMetric;
        }

        prevMetric[0] = 0;

        // 各時刻ごとに、全遷移の中から最小ハミング距離となる経路を更新する。
        for (var t = 0; t < inputSymbolCount; t++)
        {
            for (var s = 0; s < StateCount; s++)
            {
                nextMetric[s] = LargeMetric;
            }

            var rx0 = encodedBits[t * 2] ? 1 : 0;
            var rx1 = encodedBits[(t * 2) + 1] ? 1 : 0;

            for (var state = 0; state < StateCount; state++)
            {
                var baseMetric = prevMetric[state];
                if (baseMetric >= LargeMetric)
                {
                    continue;
                }

                for (var inputBit = 0; inputBit <= 1; inputBit++)
                {
                    var nextState = GetNextState(state, inputBit);
                    var branch0 = GetOutputBit(state, inputBit, GeneratorPolynomialsOctal[0]);
                    var branch1 = GetOutputBit(state, inputBit, GeneratorPolynomialsOctal[1]);
                    var branchDistance = (branch0 ^ rx0) + (branch1 ^ rx1);
                    var candidate = baseMetric + branchDistance;

                    if (candidate < nextMetric[nextState])
                    {
                        nextMetric[nextState] = candidate;
                        predecessorState[t, nextState] = state;
                        decidedInputBit[t, nextState] = (byte)inputBit;
                    }
                }
            }

            (prevMetric, nextMetric) = (nextMetric, prevMetric);
        }

        var finalState = terminated ? 0 : FindMinMetricState(prevMetric);
        var bestMetric = prevMetric[finalState];
        if (bestMetric >= LargeMetric)
        {
            throw new ArgumentException("Failed to decode: no valid Viterbi path.", nameof(encoded));
        }

        var decidedBits = new bool[inputSymbolCount];
        var traceState = finalState;
        for (var t = inputSymbolCount - 1; t >= 0; t--)
        {
            decidedBits[t] = decidedInputBit[t, traceState] == 1;
            traceState = predecessorState[t, traceState];
        }

        var payloadBits = new bool[originalBitLength];
        Array.Copy(decidedBits, 0, payloadBits, 0, originalBitLength);

        var decoded = BitsToBytes(payloadBits);
        var correctedCodeBits = bestMetric;
        metrics = new DecodeMetrics(
            PathHammingDistance: bestMetric,
            ComparedCodeBitCount: expectedCodeBitLength,
            CorrectedCodeBitCount: correctedCodeBits,
            CorrectionRate: expectedCodeBitLength == 0 ? 0.0 : (double)correctedCodeBits / expectedCodeBitLength);

        return decoded;
    }

    /// <summary>
    /// 例外を投げずに復号を試みます。
    /// </summary>
    /// <param name="encoded">符号化データ（ビットパック済み）。</param>
    /// <param name="originalByteLength">復号後に期待する元データ長（バイト）。</param>
    /// <param name="decoded">成功時は復号結果、失敗時は空配列。</param>
    /// <param name="terminated">符号化時にテール終端を付与したかどうか。</param>
    /// <returns>成功時 true、失敗時 false。</returns>
    public static bool TryDecode(byte[] encoded, int originalByteLength, out byte[] decoded, bool terminated = true)
    {
        return TryDecode(encoded, originalByteLength, out decoded, out _, terminated);
    }

    /// <summary>
    /// 例外を投げずに復号を試み、復号統計も返します。
    /// </summary>
    /// <param name="encoded">符号化データ（ビットパック済み）。</param>
    /// <param name="originalByteLength">復号後に期待する元データ長（バイト）。</param>
    /// <param name="decoded">成功時は復号結果、失敗時は空配列。</param>
    /// <param name="metrics">成功時は復号統計、失敗時は既定値。</param>
    /// <param name="terminated">符号化時にテール終端を付与したかどうか。</param>
    /// <returns>成功時 true、失敗時 false。</returns>
    public static bool TryDecode(byte[] encoded, int originalByteLength, out byte[] decoded, out DecodeMetrics metrics, bool terminated = true)
    {
        decoded = Array.Empty<byte>();
        metrics = default;

        try
        {
            decoded = Decode(encoded, originalByteLength, out metrics, terminated);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EncodeOneBit(int inputBit, ref int state, bool[] encodedBits, ref int writeIndex)
    {
        encodedBits[writeIndex++] = GetOutputBit(state, inputBit, GeneratorPolynomialsOctal[0]) == 1;
        encodedBits[writeIndex++] = GetOutputBit(state, inputBit, GeneratorPolynomialsOctal[1]) == 1;
        state = GetNextState(state, inputBit);
    }

    private static int GetNextState(int state, int inputBit)
    {
        var register = (inputBit << MemoryBits) | state;
        return (register >> 1) & StateMask;
    }

    private static int GetOutputBit(int state, int inputBit, int generator)
    {
        var register = (inputBit << MemoryBits) | state;
        var masked = register & generator;
        return Parity(masked);
    }

    private static int Parity(int value)
    {
        var parity = 0;
        while (value != 0)
        {
            parity ^= 1;
            value &= value - 1;
        }

        return parity;
    }

    private static int FindMinMetricState(int[] metrics)
    {
        var minState = 0;
        var minMetric = metrics[0];
        for (var s = 1; s < metrics.Length; s++)
        {
            if (metrics[s] < minMetric)
            {
                minMetric = metrics[s];
                minState = s;
            }
        }

        return minState;
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
        if (bits.Length % 8 != 0)
        {
            throw new ArgumentException("Bit length must be a multiple of 8.", nameof(bits));
        }

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
        var maxBits = bytes.Length * 8;
        if (bitCount < 0 || bitCount > maxBits)
        {
            throw new ArgumentException("Input bit length is inconsistent with byte length.", nameof(bitCount));
        }

        var bits = new bool[bitCount];
        for (var i = 0; i < bitCount; i++)
        {
            var byteIndex = i / 8;
            var bitIndex = 7 - (i % 8);
            bits[i] = ((bytes[byteIndex] >> bitIndex) & 1) == 1;
        }

        return bits;
    }
}
