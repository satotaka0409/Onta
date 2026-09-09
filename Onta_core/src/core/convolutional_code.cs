using System.Buffers;

namespace Onta.Core;

/// <summary>
/// レート 1/2 の畳み込み符号（拘束長 7, 生成多項式 171/133 octal）を提供します。
/// </summary>
public static class ConvolutionalCode
{
    public enum PunctureRate
    {
        Rate1_2,
        Rate2_3,
        Rate3_4
    }

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
    private static readonly bool[] PuncturePatternRate1_2 = [true, true];
    private static readonly bool[] PuncturePatternRate2_3 = [true, true, true, false];
    private static readonly bool[] PuncturePatternRate3_4 = [true, true, true, false, false, true];
    private static readonly byte[] NextStateWhenInput0 = BuildNextStateTable(inputBit: 0);
    private static readonly byte[] NextStateWhenInput1 = BuildNextStateTable(inputBit: 1);
    private static readonly byte[] Output0WhenInput0 = BuildOutputBitTable(inputBit: 0, generatorIndex: 0);
    private static readonly byte[] Output1WhenInput0 = BuildOutputBitTable(inputBit: 0, generatorIndex: 1);
    private static readonly byte[] Output0WhenInput1 = BuildOutputBitTable(inputBit: 1, generatorIndex: 0);
    private static readonly byte[] Output1WhenInput1 = BuildOutputBitTable(inputBit: 1, generatorIndex: 1);
    private static readonly byte[] ByteToBitsLookup = BuildByteToBitsLookup();
    private static readonly int PunctureRate1_2Ones = CountTrue(PuncturePatternRate1_2);
    private static readonly int PunctureRate2_3Ones = CountTrue(PuncturePatternRate2_3);
    private static readonly int PunctureRate3_4Ones = CountTrue(PuncturePatternRate3_4);

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
    /// <param name="punctureRate">パンクチャレート。</param>
    /// <returns>符号化ビット列（MSB-first）をパックしたバイト列。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> が null の場合にスローされます。</exception>
    public static byte[] Encode(byte[] data, bool terminate = true, PunctureRate punctureRate = PunctureRate.Rate1_2)
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

        var puncturedBits = Puncture(encodedBits, punctureRate);
        return PackBits(puncturedBits);
    }

    /// <summary>
    /// 符号化ビット列を Viterbi（ハード判定）で復号し、元のバイト列を返します。
    /// </summary>
    /// <param name="encoded">符号化データ（ビットパック済み）。</param>
    /// <param name="originalByteLength">復号後に期待する元データ長（バイト）。</param>
    /// <param name="terminated">符号化時にテール終端を付与したかどうか。</param>
    /// <param name="punctureRate">符号化時のパンクチャレート。</param>
    /// <returns>復号データ。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoded"/> が null の場合にスローされます。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="originalByteLength"/> が負値の場合にスローされます。</exception>
    /// <exception cref="ArgumentException">入力ビット長が期待フォーマットと一致しない場合にスローされます。</exception>
    public static byte[] Decode(
        byte[] encoded,
        int originalByteLength,
        bool terminated = true,
        PunctureRate punctureRate = PunctureRate.Rate1_2)
    {
        return Decode(encoded, originalByteLength, out _, terminated, punctureRate);
    }

    /// <summary>
    /// 符号化ビット列を Viterbi（ハード判定）で復号し、復号統計とともに返します。
    /// </summary>
    /// <param name="encoded">符号化データ（ビットパック済み）。</param>
    /// <param name="originalByteLength">復号後に期待する元データ長（バイト）。</param>
    /// <param name="metrics">復号統計。訂正率は符号語ビットに対する差分率です。</param>
    /// <param name="terminated">符号化時にテール終端を付与したかどうか。</param>
    /// <param name="punctureRate">符号化時のパンクチャレート。</param>
    /// <returns>復号データ。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoded"/> が null の場合にスローされます。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="originalByteLength"/> が負値の場合にスローされます。</exception>
    /// <exception cref="ArgumentException">入力ビット長が期待フォーマットと一致しない場合にスローされます。</exception>
    public static byte[] Decode(
        byte[] encoded,
        int originalByteLength,
        out DecodeMetrics metrics,
        bool terminated = true,
        PunctureRate punctureRate = PunctureRate.Rate1_2)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (originalByteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(originalByteLength));
        }

        var originalBitLength = originalByteLength * 8;
        var expectedInputBits = originalBitLength + (terminated ? TailBits : 0);
        var expectedCodeBitLength = expectedInputBits * OutputBitsPerInputBit;
        var expectedPuncturedBitLength = GetPuncturedBitLength(expectedCodeBitLength, punctureRate);
        var encodedBits = ArrayPool<bool>.Shared.Rent(expectedCodeBitLength);
        var presentMask = ArrayPool<bool>.Shared.Rent(expectedCodeBitLength);
        DepunctureHardFromPacked(
            encoded,
            expectedPuncturedBitLength,
            expectedCodeBitLength,
            punctureRate,
            encodedBits.AsSpan(0, expectedCodeBitLength),
            presentMask.AsSpan(0, expectedCodeBitLength));

        var inputSymbolCount = expectedCodeBitLength / OutputBitsPerInputBit;
        var prevMetric = new int[StateCount];
        var nextMetric = new int[StateCount];
        var traceLength = inputSymbolCount * StateCount;
        var predecessorState = ArrayPool<int>.Shared.Rent(traceLength);
        var decidedInputBit = ArrayPool<byte>.Shared.Rent(traceLength);
        var decidedBits = ArrayPool<bool>.Shared.Rent(inputSymbolCount);

        try
        {

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
                var hasRx0 = presentMask[t * 2];
                var hasRx1 = presentMask[(t * 2) + 1];
                var traceRowBase = t * StateCount;

                for (var state = 0; state < StateCount; state++)
                {
                    var baseMetric = prevMetric[state];
                    if (baseMetric >= LargeMetric)
                    {
                        continue;
                    }

                    var nextState0 = NextStateWhenInput0[state];
                    var branchDistance0 = 0;
                    if (hasRx0)
                    {
                        branchDistance0 += Output0WhenInput0[state] ^ rx0;
                    }

                    if (hasRx1)
                    {
                        branchDistance0 += Output1WhenInput0[state] ^ rx1;
                    }

                    var candidate0 = baseMetric + branchDistance0;
                    if (candidate0 < nextMetric[nextState0])
                    {
                        nextMetric[nextState0] = candidate0;
                        predecessorState[traceRowBase + nextState0] = state;
                        decidedInputBit[traceRowBase + nextState0] = 0;
                    }

                    var nextState1 = NextStateWhenInput1[state];
                    var branchDistance1 = 0;
                    if (hasRx0)
                    {
                        branchDistance1 += Output0WhenInput1[state] ^ rx0;
                    }

                    if (hasRx1)
                    {
                        branchDistance1 += Output1WhenInput1[state] ^ rx1;
                    }

                    var candidate1 = baseMetric + branchDistance1;
                    if (candidate1 < nextMetric[nextState1])
                    {
                        nextMetric[nextState1] = candidate1;
                        predecessorState[traceRowBase + nextState1] = state;
                        decidedInputBit[traceRowBase + nextState1] = 1;
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

            var traceState = finalState;
            for (var t = inputSymbolCount - 1; t >= 0; t--)
            {
                var rowBase = t * StateCount;
                decidedBits[t] = decidedInputBit[rowBase + traceState] == 1;
                traceState = predecessorState[rowBase + traceState];
            }

            var decoded = BitsToBytes(decidedBits.AsSpan(0, originalBitLength));
            var correctedCodeBits = bestMetric;
            metrics = new DecodeMetrics(
                PathHammingDistance: bestMetric,
                ComparedCodeBitCount: expectedPuncturedBitLength,
                CorrectedCodeBitCount: correctedCodeBits,
                CorrectionRate: expectedPuncturedBitLength == 0 ? 0.0 : (double)correctedCodeBits / expectedPuncturedBitLength);

            return decoded;
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(encodedBits, clearArray: false);
            ArrayPool<bool>.Shared.Return(presentMask, clearArray: false);
            ArrayPool<int>.Shared.Return(predecessorState, clearArray: false);
            ArrayPool<byte>.Shared.Return(decidedInputBit, clearArray: false);
            ArrayPool<bool>.Shared.Return(decidedBits, clearArray: false);
        }
    }

    /// <summary>
    /// ソフト LLR 入力の復号です。LLR&gt;0 をビット 1 寄りと解釈します。
    /// </summary>
    /// <param name="codeLlrs">パンクチャ済み符号ビットのソフト LLR。</param>
    /// <param name="originalByteLength">復号後に期待する元データ長（バイト）。</param>
    /// <param name="terminated">符号化時にテール終端を付与したかどうか。</param>
    /// <param name="punctureRate">符号化時のパンクチャレート。</param>
    /// <returns>復号データ。</returns>
    public static byte[] DecodeSoft(
        ReadOnlySpan<double> codeLlrs,
        int originalByteLength,
        bool terminated = true,
        PunctureRate punctureRate = PunctureRate.Rate1_2)
    {
        return DecodeSoftToInfoLlrs(codeLlrs, originalByteLength, out _, terminated, punctureRate);
    }

    /// <summary>
    /// Max-Log BCJR でソフト入力ソフト出力復号し、情報ビット LLR を返します。
    /// 情報ビット LLR はターボと同じ極性（正=ビット0優勢、負=ビット1優勢）です。
    /// QAM チャネル LLR の柔らかさをターボへ直接渡せます。
    /// </summary>
    /// <param name="codeLlrs">パンクチャ済み符号ビットのソフト LLR。</param>
    /// <param name="originalByteLength">復号後に期待する元データ長（バイト）。</param>
    /// <param name="infoLlrs">情報ビット LLR（正=ビット0優勢）。</param>
    /// <param name="terminated">符号化時にテール終端を付与したかどうか。</param>
    /// <param name="punctureRate">符号化時のパンクチャレート。</param>
    /// <returns>復号データ（ハード判定）。</returns>
    public static byte[] DecodeSoftToInfoLlrs(
        ReadOnlySpan<double> codeLlrs,
        int originalByteLength,
        out double[] infoLlrs,
        bool terminated = true,
        PunctureRate punctureRate = PunctureRate.Rate1_2)
    {
        if (originalByteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(originalByteLength));
        }

        var originalBitLength = originalByteLength * 8;
        var expectedInputBits = originalBitLength + (terminated ? TailBits : 0);
        var expectedCodeBitLength = expectedInputBits * OutputBitsPerInputBit;
        var expectedPuncturedBitLength = GetPuncturedBitLength(expectedCodeBitLength, punctureRate);
        if (codeLlrs.Length < expectedPuncturedBitLength)
        {
            throw new ArgumentException(
            $"Soft LLR length {codeLlrs.Length} is shorter than required {expectedPuncturedBitLength}.",
                nameof(codeLlrs));
        }

        var fullCodeLlrsBuffer = ArrayPool<double>.Shared.Rent(expectedCodeBitLength);
        var fullCodeLlrs = fullCodeLlrsBuffer.AsSpan(0, expectedCodeBitLength);
        DepunctureSoftInto(codeLlrs, expectedCodeBitLength, punctureRate, fullCodeLlrs);

        var tCount = expectedCodeBitLength / OutputBitsPerInputBit;
        const double negInf = -1e300;

        // alpha[t, s]: 時刻 t で状態 s にいる前向きメトリック（t=0..tCount）
        var alphaLength = (tCount + 1) * StateCount;
        var alpha = ArrayPool<double>.Shared.Rent(alphaLength);
        var payloadBitsBuffer = ArrayPool<bool>.Shared.Rent(originalBitLength);
        try
        {
            Array.Fill(alpha, negInf, 0, alphaLength);

            alpha[0] = 0.0;
            for (var t = 0; t < tCount; t++)
            {
                var llr0 = fullCodeLlrs[t * 2];
                var llr1 = fullCodeLlrs[(t * 2) + 1];
                var rowBase = t * StateCount;
                var nextRowBase = (t + 1) * StateCount;
                for (var state = 0; state < StateCount; state++)
                {
                    var a = alpha[rowBase + state];
                    if (a <= negInf / 2)
                    {
                        continue;
                    }

                    var nextState0 = NextStateWhenInput0[state];
                    var gamma0 = (Output0WhenInput0[state] == 1 ? llr0 : 0.0)
                        + (Output1WhenInput0[state] == 1 ? llr1 : 0.0);
                    var candidate0 = a + gamma0;
                    var index0 = nextRowBase + nextState0;
                    if (candidate0 > alpha[index0])
                    {
                        alpha[index0] = candidate0;
                    }

                    var nextState1 = NextStateWhenInput1[state];
                    var gamma1 = (Output0WhenInput1[state] == 1 ? llr0 : 0.0)
                        + (Output1WhenInput1[state] == 1 ? llr1 : 0.0);
                    var candidate1 = a + gamma1;
                    var index1 = nextRowBase + nextState1;
                    if (candidate1 > alpha[index1])
                    {
                        alpha[index1] = candidate1;
                    }
                }
            }

            var payloadBits = payloadBitsBuffer.AsSpan(0, originalBitLength);
            infoLlrs = new double[originalBitLength];
            Span<double> betaNext = stackalloc double[StateCount];
            Span<double> betaCurr = stackalloc double[StateCount];
            if (terminated)
            {
                for (var state = 0; state < StateCount; state++)
                {
                    betaNext[state] = negInf;
                }
                betaNext[0] = 0.0;
            }
            else
            {
                for (var state = 0; state < StateCount; state++)
                {
                    betaNext[state] = 0.0;
                }
            }

            for (var t = tCount - 1; t >= 0; t--)
            {
                var llr0 = fullCodeLlrs[t * 2];
                var llr1 = fullCodeLlrs[(t * 2) + 1];
                var best0 = negInf;
                var best1 = negInf;
                var rowBase = t * StateCount;

                for (var state = 0; state < StateCount; state++)
                {
                    var gamma0 = (Output0WhenInput0[state] == 1 ? llr0 : 0.0)
                        + (Output1WhenInput0[state] == 1 ? llr1 : 0.0);
                    var gamma1 = (Output0WhenInput1[state] == 1 ? llr0 : 0.0)
                        + (Output1WhenInput1[state] == 1 ? llr1 : 0.0);

                    var nextState0 = NextStateWhenInput0[state];
                    var b0 = betaNext[nextState0];
                    var candidate0 = b0 > negInf / 2 ? b0 + gamma0 : negInf;

                    var nextState1 = NextStateWhenInput1[state];
                    var b1 = betaNext[nextState1];
                    var candidate1 = b1 > negInf / 2 ? b1 + gamma1 : negInf;

                    betaCurr[state] = candidate0 > candidate1 ? candidate0 : candidate1;

                    var a = alpha[rowBase + state];
                    if (a <= negInf / 2)
                    {
                        continue;
                    }

                    if (candidate0 > negInf / 2)
                    {
                        var metric0 = a + candidate0;
                        if (metric0 > best0)
                        {
                            best0 = metric0;
                        }
                    }

                    if (candidate1 > negInf / 2)
                    {
                        var metric1 = a + candidate1;
                        if (metric1 > best1)
                        {
                            best1 = metric1;
                        }
                    }
                }

                // ターボ極性: L = log P(u=0)/P(u=1) ≈ best0 - best1
                var app = best0 - best1;
                if (double.IsNaN(app) || double.IsInfinity(app))
                {
                    app = 0.0;
                }

                if (t < originalBitLength)
                {
                    infoLlrs[t] = app;
                    payloadBits[t] = app < 0.0;
                }

                var betaSwap = betaNext;
                betaNext = betaCurr;
                betaCurr = betaSwap;
            }

            return BitsToBytes(payloadBits);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(alpha, clearArray: false);
            ArrayPool<double>.Shared.Return(fullCodeLlrsBuffer, clearArray: false);
            ArrayPool<bool>.Shared.Return(payloadBitsBuffer, clearArray: false);
        }
    }

    /// <summary>
    /// 符号ビット LLR（正=bit1）に対する枝の対数尤度です。
    /// </summary>
    /// <param name="state">現在状態。</param>
    /// <param name="inputBit">入力ビット（0/1）。</param>
    /// <param name="llr0">第1出力ビットの LLR。</param>
    /// <param name="llr1">第2出力ビットの LLR。</param>
    /// <returns>枝の対数尤度。</returns>
    private static double BranchLogLikelihood(int state, int inputBit, double llr0, double llr1)
    {
        var branch0 = GetOutputBit(state, inputBit, GeneratorPolynomialsOctal[0]);
        var branch1 = GetOutputBit(state, inputBit, GeneratorPolynomialsOctal[1]);
        // L>0 が bit1 優勢 → 期待ビットが 1 なら +L、0 なら 0（定数差は APP で相殺）
        return ((branch0 == 1) ? llr0 : 0.0) + ((branch1 == 1) ? llr1 : 0.0);
    }

    /// <summary>
    /// 最小メトリクスを持つ状態インデックスを返します。
    /// </summary>
    /// <param name="metrics">各状態のメトリクス。</param>
    /// <returns>最小メトリクスの状態インデックス。</returns>
    private static int FindMinMetricState(double[] metrics)
    {
        var best = 0;
        var bestMetric = metrics[0];
        for (var s = 1; s < metrics.Length; s++)
        {
            if (metrics[s] < bestMetric)
            {
                bestMetric = metrics[s];
                best = s;
            }
        }

        return best;
    }

    /// <summary>
    /// 例外を投げずに復号を試みます。
    /// </summary>
    /// <param name="encoded">符号化データ（ビットパック済み）。</param>
    /// <param name="originalByteLength">復号後に期待する元データ長（バイト）。</param>
    /// <param name="decoded">成功時は復号結果、失敗時は空配列。</param>
    /// <param name="terminated">符号化時にテール終端を付与したかどうか。</param>
    /// <param name="punctureRate">符号化時のパンクチャレート。</param>
    /// <returns>成功時 true、失敗時 false。</returns>
    public static bool TryDecode(
        byte[] encoded,
        int originalByteLength,
        out byte[] decoded,
        bool terminated = true,
        PunctureRate punctureRate = PunctureRate.Rate1_2)
    {
        return TryDecode(encoded, originalByteLength, out decoded, out _, terminated, punctureRate);
    }

    /// <summary>
    /// 例外を投げずに復号を試み、復号統計も返します。
    /// </summary>
    /// <param name="encoded">符号化データ（ビットパック済み）。</param>
    /// <param name="originalByteLength">復号後に期待する元データ長（バイト）。</param>
    /// <param name="decoded">成功時は復号結果、失敗時は空配列。</param>
    /// <param name="metrics">成功時は復号統計、失敗時は既定値。</param>
    /// <param name="terminated">符号化時にテール終端を付与したかどうか。</param>
    /// <param name="punctureRate">符号化時のパンクチャレート。</param>
    /// <returns>成功時 true、失敗時 false。</returns>
    public static bool TryDecode(
        byte[] encoded,
        int originalByteLength,
        out byte[] decoded,
        out DecodeMetrics metrics,
        bool terminated = true,
        PunctureRate punctureRate = PunctureRate.Rate1_2)
    {
        decoded = Array.Empty<byte>();
        metrics = default;

        try
        {
            decoded = Decode(encoded, originalByteLength, out metrics, terminated, punctureRate);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 符号化後のパンクチャ済みビット長を計算します。
    /// </summary>
    /// <param name="originalBitLength">元データのビット長。</param>
    /// <param name="terminated">テール終端を付与するかどうか。</param>
    /// <param name="punctureRate">パンクチャレート。</param>
    /// <returns>パンクチャ済み符号化ビット長。</returns>
    public static int GetEncodedBitLength(int originalBitLength, bool terminated, PunctureRate punctureRate)
    {
        if (originalBitLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(originalBitLength));
        }

        var inputBits = originalBitLength + (terminated ? TailBits : 0);
        var motherBits = inputBits * OutputBitsPerInputBit;
        return GetPuncturedBitLength(motherBits, punctureRate);
    }

    /// <summary>
    /// 母符号ビット長からパンクチャ後のビット長を求めます。
    /// </summary>
    /// <param name="motherBitLength">母符号のビット長。</param>
    /// <param name="punctureRate">パンクチャレート。</param>
    /// <returns>パンクチャ後のビット長。</returns>
    private static int GetPuncturedBitLength(int motherBitLength, PunctureRate punctureRate)
    {
        var pattern = GetPuncturePattern(punctureRate);
        var onesPerPattern = punctureRate switch
        {
            PunctureRate.Rate1_2 => PunctureRate1_2Ones,
            PunctureRate.Rate2_3 => PunctureRate2_3Ones,
            PunctureRate.Rate3_4 => PunctureRate3_4Ones,
            _ => throw new ArgumentOutOfRangeException(nameof(punctureRate), punctureRate, "Unsupported puncture rate.")
        };

        var fullCycles = motherBitLength / pattern.Length;
        var remainder = motherBitLength - (fullCycles * pattern.Length);
        var count = fullCycles * onesPerPattern;
        for (var i = 0; i < remainder; i++)
        {
            if (pattern[i])
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 母符号ビット列にパンクチャリングを適用します。
    /// </summary>
    /// <param name="motherBits">母符号ビット列。</param>
    /// <param name="punctureRate">パンクチャレート。</param>
    /// <returns>パンクチャ済みビット列。</returns>
    private static bool[] Puncture(bool[] motherBits, PunctureRate punctureRate)
    {
        var pattern = GetPuncturePattern(punctureRate);
        var output = new bool[GetPuncturedBitLength(motherBits.Length, punctureRate)];
        var write = 0;
        var p = 0;
        var pLen = pattern.Length;
        for (var i = 0; i < motherBits.Length; i++)
        {
            if (!pattern[p])
            {
                p++;
                if (p == pLen)
                {
                    p = 0;
                }
                continue;
            }

            output[write++] = motherBits[i];
            p++;
            if (p == pLen)
            {
                p = 0;
            }
        }

        return output;
    }

    /// <summary>
    /// ハード判定ビット列を母符号長へデパンクチャします。
    /// </summary>
    /// <param name="puncturedBits">パンクチャ済みビット列。</param>
    /// <param name="motherBitLength">母符号ビット長。</param>
    /// <param name="punctureRate">パンクチャレート。</param>
    /// <param name="fullBits">デパンクチャ後のビット列（欠落位置は false）。</param>
    /// <param name="presentMask">各ビットが受信済みかどうかのマスク。</param>
    private static void DepunctureHard(
        bool[] puncturedBits,
        int motherBitLength,
        PunctureRate punctureRate,
        out bool[] fullBits,
        out bool[] presentMask)
    {
        var pattern = GetPuncturePattern(punctureRate);
        fullBits = new bool[motherBitLength];
        presentMask = new bool[motherBitLength];
        var read = 0;
        var p = 0;
        var pLen = pattern.Length;
        for (var i = 0; i < motherBitLength; i++)
        {
            if (!pattern[p])
            {
                fullBits[i] = false;
                presentMask[i] = false;
                p++;
                if (p == pLen)
                {
                    p = 0;
                }
                continue;
            }

            if (read >= puncturedBits.Length)
            {
                throw new ArgumentException("Punctured bit stream is shorter than expected.", nameof(puncturedBits));
            }

            fullBits[i] = puncturedBits[read++];
            presentMask[i] = true;
            p++;
            if (p == pLen)
            {
                p = 0;
            }
        }
    }

    /// <summary>
    /// パック済みパンクチャビットを母符号長へデパンクチャします。
    /// </summary>
    /// <param name="packedBits">パック済みパンクチャビット。</param>
    /// <param name="puncturedBitLength">パンクチャ済みビット長。</param>
    /// <param name="motherBitLength">母符号ビット長。</param>
    /// <param name="punctureRate">パンクチャレート。</param>
    /// <param name="fullBits">デパンクチャ後ビットの出力先。</param>
    /// <param name="presentMask">受信済みマスクの出力先。</param>
    private static void DepunctureHardFromPacked(
        ReadOnlySpan<byte> packedBits,
        int puncturedBitLength,
        int motherBitLength,
        PunctureRate punctureRate,
        Span<bool> fullBits,
        Span<bool> presentMask)
    {
        var pattern = GetPuncturePattern(punctureRate);
        var read = 0;
        var p = 0;
        var pLen = pattern.Length;
        for (var i = 0; i < motherBitLength; i++)
        {
            if (!pattern[p])
            {
                fullBits[i] = false;
                presentMask[i] = false;
                p++;
                if (p == pLen)
                {
                    p = 0;
                }
                continue;
            }

            if (read >= puncturedBitLength)
            {
                throw new ArgumentException("Punctured bit stream is shorter than expected.", nameof(packedBits));
            }

            fullBits[i] = ReadPackedBit(packedBits, read++);
            presentMask[i] = true;
            p++;
            if (p == pLen)
            {
                p = 0;
            }
        }
    }

    /// <summary>
    /// ソフト LLR を母符号長へデパンクチャした配列を返します。
    /// </summary>
    /// <param name="puncturedLlrs">パンクチャ済み LLR。</param>
    /// <param name="motherBitLength">母符号ビット長。</param>
    /// <param name="punctureRate">パンクチャレート。</param>
    /// <returns>デパンクチャ後の LLR（欠落位置は 0）。</returns>
    private static double[] DepunctureSoft(ReadOnlySpan<double> puncturedLlrs, int motherBitLength, PunctureRate punctureRate)
    {
        var pattern = GetPuncturePattern(punctureRate);
        var full = new double[motherBitLength];
        var read = 0;
        var p = 0;
        var pLen = pattern.Length;
        for (var i = 0; i < motherBitLength; i++)
        {
            if (!pattern[p])
            {
                full[i] = 0.0;
                p++;
                if (p == pLen)
                {
                    p = 0;
                }
                continue;
            }

            if (read >= puncturedLlrs.Length)
            {
                throw new ArgumentException("Punctured LLR stream is shorter than expected.", nameof(puncturedLlrs));
            }

            full[i] = puncturedLlrs[read++];
            p++;
            if (p == pLen)
            {
                p = 0;
            }
        }

        return full;
    }

    /// <summary>
    /// ソフト LLR を母符号長バッファへデパンクチャします。
    /// </summary>
    /// <param name="puncturedLlrs">パンクチャ済み LLR。</param>
    /// <param name="motherBitLength">母符号ビット長。</param>
    /// <param name="punctureRate">パンクチャレート。</param>
    /// <param name="full">デパンクチャ先バッファ。</param>
    private static void DepunctureSoftInto(
        ReadOnlySpan<double> puncturedLlrs,
        int motherBitLength,
        PunctureRate punctureRate,
        Span<double> full)
    {
        var pattern = GetPuncturePattern(punctureRate);
        var read = 0;
        var p = 0;
        var pLen = pattern.Length;
        for (var i = 0; i < motherBitLength; i++)
        {
            if (!pattern[p])
            {
                full[i] = 0.0;
                p++;
                if (p == pLen)
                {
                    p = 0;
                }
                continue;
            }

            if (read >= puncturedLlrs.Length)
            {
                throw new ArgumentException("Punctured LLR stream is shorter than expected.", nameof(puncturedLlrs));
            }

            full[i] = puncturedLlrs[read++];
            p++;
            if (p == pLen)
            {
                p = 0;
            }
        }
    }

    /// <summary>
    /// 指定レートのパンクチャパターンを返します。
    /// </summary>
    /// <param name="punctureRate">パンクチャレート。</param>
    /// <returns>パンクチャパターン（true=送信）。</returns>
    private static bool[] GetPuncturePattern(PunctureRate punctureRate)
    {
        return punctureRate switch
        {
            PunctureRate.Rate1_2 => PuncturePatternRate1_2,
            PunctureRate.Rate2_3 => PuncturePatternRate2_3,
            PunctureRate.Rate3_4 => PuncturePatternRate3_4,
            _ => throw new ArgumentOutOfRangeException(nameof(punctureRate), punctureRate, "Unsupported puncture rate.")
        };
    }

    /// <summary>
    /// 1 入力ビットを符号化して状態を更新します。
    /// </summary>
    /// <param name="inputBit">入力ビット（0/1）。</param>
    /// <param name="state">符号器状態（入出力）。</param>
    /// <param name="encodedBits">符号化ビットの出力先。</param>
    /// <param name="writeIndex">書き込み位置（入出力）。</param>
    private static void EncodeOneBit(int inputBit, ref int state, bool[] encodedBits, ref int writeIndex)
    {
        if (inputBit == 0)
        {
            encodedBits[writeIndex++] = Output0WhenInput0[state] == 1;
            encodedBits[writeIndex++] = Output1WhenInput0[state] == 1;
            state = NextStateWhenInput0[state];
            return;
        }

        encodedBits[writeIndex++] = Output0WhenInput1[state] == 1;
        encodedBits[writeIndex++] = Output1WhenInput1[state] == 1;
        state = NextStateWhenInput1[state];
    }

    /// <summary>
    /// 真の要素数を数えます。
    /// </summary>
    /// <param name="values">真偽値配列。</param>
    /// <returns>true の個数。</returns>
    private static int CountTrue(bool[] values)
    {
        var count = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i])
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 入力ビットに対する次状態を計算します。
    /// </summary>
    /// <param name="state">現在状態。</param>
    /// <param name="inputBit">入力ビット（0/1）。</param>
    /// <returns>次状態。</returns>
    private static int GetNextState(int state, int inputBit)
    {
        var register = (inputBit << MemoryBits) | state;
        return (register >> 1) & StateMask;
    }

    /// <summary>
    /// 指定入力ビットの次状態テーブルを構築します。
    /// </summary>
    /// <param name="inputBit">入力ビット（0/1）。</param>
    /// <returns>状態→次状態のテーブル。</returns>
    private static byte[] BuildNextStateTable(int inputBit)
    {
        var table = new byte[StateCount];
        for (var state = 0; state < StateCount; state++)
        {
            table[state] = (byte)GetNextState(state, inputBit);
        }

        return table;
    }

    /// <summary>
    /// 指定入力・生成多項式の出力ビットテーブルを構築します。
    /// </summary>
    /// <param name="inputBit">入力ビット（0/1）。</param>
    /// <param name="generatorIndex">生成多項式インデックス（0/1）。</param>
    /// <returns>状態→出力ビットのテーブル。</returns>
    private static byte[] BuildOutputBitTable(int inputBit, int generatorIndex)
    {
        var table = new byte[StateCount];
        for (var state = 0; state < StateCount; state++)
        {
            table[state] = (byte)GetOutputBit(state, inputBit, GeneratorPolynomialsOctal[generatorIndex]);
        }

        return table;
    }

    /// <summary>
    /// 生成多項式に基づく出力ビットを計算します。
    /// </summary>
    /// <param name="state">現在状態。</param>
    /// <param name="inputBit">入力ビット（0/1）。</param>
    /// <param name="generator">生成多項式（ビットマスク）。</param>
    /// <returns>出力ビット（0/1）。</returns>
    private static int GetOutputBit(int state, int inputBit, int generator)
    {
        var register = (inputBit << MemoryBits) | state;
        var masked = register & generator;
        return Parity(masked);
    }

    /// <summary>
    /// 整数のパリティ（1 の個数の偶奇）を返します。
    /// </summary>
    /// <param name="value">対象整数。</param>
    /// <returns>パリティ（0=偶数、1=奇数）。</returns>
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

    /// <summary>
    /// 最小メトリクスを持つ状態インデックスを返します。
    /// </summary>
    /// <param name="metrics">各状態のメトリクス。</param>
    /// <returns>最小メトリクスの状態インデックス。</returns>
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

    /// <summary>
    /// バイト列を MSB-first のビット列へ展開します。
    /// </summary>
    /// <param name="bytes">入力バイト列。</param>
    /// <returns>MSB-first のビット列。</returns>
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
    /// バイト→8 ビット展開用のルックアップ表を構築します。
    /// </summary>
    /// <returns>256×8 のビット展開ルックアップ。</returns>
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
    /// ビット配列をバイト列へパックします。
    /// </summary>
    /// <param name="bits">入力ビット列（長さは 8 の倍数）。</param>
    /// <returns>パック済みバイト列。</returns>
    private static byte[] BitsToBytes(bool[] bits)
    {
        return BitsToBytes(bits.AsSpan());
    }

    /// <summary>
    /// ビットスパンをバイト列へパックします。
    /// </summary>
    /// <param name="bits">入力ビットスパン（長さは 8 の倍数）。</param>
    /// <returns>パック済みバイト列。</returns>
    private static byte[] BitsToBytes(ReadOnlySpan<bool> bits)
    {
        if (bits.Length % 8 != 0)
        {
            throw new ArgumentException("Bit length must be a multiple of 8.", nameof(bits));
        }

        var bytes = new byte[bits.Length / 8];
        var bitBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(bits);
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
    /// パック済みバイト列から指定ビットを読み出します。
    /// </summary>
    /// <param name="packed">パック済みバイト列。</param>
    /// <param name="bitIndex">読み出すビット位置（0 起点）。</param>
    /// <returns>ビット値。</returns>
    private static bool ReadPackedBit(ReadOnlySpan<byte> packed, int bitIndex)
    {
        var b = packed[bitIndex >> 3];
        return ((b >> (7 - (bitIndex & 7))) & 1) != 0;
    }

    /// <summary>
    /// ビット列をバイト列へパックします（端数ビット対応）。
    /// </summary>
    /// <param name="bits">入力ビット列。</param>
    /// <returns>パック済みバイト列。</returns>
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
    /// パック済みバイト列から指定ビット数を展開します。
    /// </summary>
    /// <param name="bytes">パック済みバイト列。</param>
    /// <param name="bitCount">展開するビット数。</param>
    /// <returns>展開後のビット列。</returns>
    private static bool[] UnpackBits(byte[] bytes, int bitCount)
    {
        var maxBits = bytes.Length * 8;
        if (bitCount < 0 || bitCount > maxBits)
        {
            throw new ArgumentException("Input bit length is inconsistent with byte length.", nameof(bitCount));
        }

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
}
