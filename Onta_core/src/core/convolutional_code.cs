using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Onta.Core;

/// <summary>
/// 畳み込み符号のエンコード/デコードを提供します。
/// </summary>
public static class ConvolutionalCode
{
    /// <summary>
    /// パンクチャ率です。
    /// </summary>
    public enum PunctureRate
    {
        Rate1_2,
        Rate2_3,
        Rate3_4
    }

    /// <summary>
    /// 拘束長です。
    /// </summary>
    public const int ConstraintLength = 7;

    /// <summary>
    /// 入力1ビットあたりの母符号出力ビット数です。
    /// </summary>
    public const int OutputBitsPerInputBit = 2;

    /// <summary>
    /// 生成多項式です。
    /// </summary>
    public static readonly int[] GeneratorPolynomialsOctal = { 0b1111001, 0b1011011 };

    private const int MemoryBits = ConstraintLength - 1;
    private const int TailBits = ConstraintLength - 1;
    private const int StateCount = 1 << MemoryBits;
    private const int StateMask = StateCount - 1;
    private const int HalfStateCount = StateCount / 2;
    private const double NegInfMetric = -1e300;
    private const int TrellisBlockSteps = 128;
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
    private static readonly byte[] OutputMaskWhenInput0 = BuildOutputMaskTable(inputBit: 0);
    private static readonly byte[] OutputMaskWhenInput1 = BuildOutputMaskTable(inputBit: 1);
    private static readonly double[] BranchBitMasks = BuildBranchBitMasks();
    private static readonly int[] HardBranchFromEvenState = BuildHardBranchTable(fromOddState: false);
    private static readonly int[] HardBranchFromOddState = BuildHardBranchTable(fromOddState: true);
    private static readonly byte[] ByteToBitsLookup = BuildByteToBitsLookup();
    private static readonly byte[] ReverseBitsLut = BuildReverseBitsLut();
    private static readonly int PunctureRate1_2Ones = CountTrue(PuncturePatternRate1_2);
    private static readonly int PunctureRate2_3Ones = CountTrue(PuncturePatternRate2_3);
    private static readonly int PunctureRate3_4Ones = CountTrue(PuncturePatternRate3_4);

    /// <summary>
    /// 復号時の訂正メトリクスです。
    /// </summary>
    /// <param name="PathHammingDistance">最良パスのハミング距離（硬判定時はパスメトリック）。</param>
    /// <param name="ComparedCodeBitCount">比較対象となった符号ビット数（パンクチャ後）。</param>
    /// <param name="CorrectedCodeBitCount">チャネル硬判定と再符号化結果が不一致だったビット数。</param>
    /// <param name="CorrectionRate">CorrectedCodeBitCount / ComparedCodeBitCount（0 除算時は 0）。</param>
    public readonly record struct DecodeMetrics(
        int PathHammingDistance,
        int ComparedCodeBitCount,
        int CorrectedCodeBitCount,
        double CorrectionRate);

    /// <summary>
    /// 入力バイト列を畳み込み符号化します。
    /// </summary>
    /// <param name="data">入力バイト列。</param>
    /// <param name="terminate">終端ビットを付与する場合 true。</param>
    /// <param name="punctureRate">パンクチャ率。</param>
    /// <returns>符号化済みバイト列。</returns>
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
    /// ハード判定ビット列を復号します。
    /// </summary>
    /// <param name="encoded">符号化済みバイト列。</param>
    /// <param name="originalByteLength">復号後の元バイト長。</param>
    /// <param name="terminated">終端ビット付きとして扱う場合 true。</param>
    /// <param name="punctureRate">パンクチャ率。</param>
    /// <returns>復号したバイト列。</returns>
    public static byte[] Decode(
        byte[] encoded,
        int originalByteLength,
        bool terminated = true,
        PunctureRate punctureRate = PunctureRate.Rate1_2)
    {
        return Decode(encoded, originalByteLength, out _, terminated, punctureRate);
    }

    /// <summary>
    /// ハード判定ビット列を復号し、メトリクスを返します。
    /// </summary>
    /// <param name="encoded">符号化済みバイト列。</param>
    /// <param name="originalByteLength">復号後の元バイト長。</param>
    /// <param name="metrics">復号時のメトリクス。</param>
    /// <param name="terminated">終端ビット付きとして扱う場合 true。</param>
    /// <param name="punctureRate">パンクチャ率。</param>
    /// <returns>復号したバイト列。</returns>
    public static byte[] Decode(
        byte[] encoded,
        int originalByteLength,
        out DecodeMetrics metrics,
        bool terminated = true,
        PunctureRate punctureRate = PunctureRate.Rate1_2)
    {
        return DecodeHard(encoded, originalByteLength, out metrics, terminated, punctureRate, TrellisKernel.Auto);
    }

    /// <summary>
    /// ハード判定ビット列を、指定のカーネルでビタビ復号します。
    /// </summary>
    /// <param name="encoded">符号化済みバイト列。</param>
    /// <param name="originalByteLength">復号後の元バイト長。</param>
    /// <param name="metrics">復号時のメトリクス。</param>
    /// <param name="terminated">終端ビット付きとして扱う場合 true。</param>
    /// <param name="punctureRate">パンクチャ率。</param>
    /// <param name="kernel">Scalar ならスカラ、それ以外は AVX2 が使えれば AVX2。</param>
    /// <returns>復号したバイト列。</returns>
    internal static byte[] DecodeHard(
        byte[] encoded,
        int originalByteLength,
        out DecodeMetrics metrics,
        bool terminated,
        PunctureRate punctureRate,
        TrellisKernel kernel)
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
        if (kernel != TrellisKernel.Scalar && Avx2.IsSupported)
        {
            try
            {
                return DecodeHardAvx2(
                    encodedBits.AsSpan(0, expectedCodeBitLength),
                    presentMask.AsSpan(0, expectedCodeBitLength),
                    originalBitLength,
                    expectedPuncturedBitLength,
                    terminated,
                    out metrics);
            }
            finally
            {
                ArrayPool<bool>.Shared.Return(encodedBits, clearArray: false);
                ArrayPool<bool>.Shared.Return(presentMask, clearArray: false);
            }
        }

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
    /// ハード判定ビタビ復号を AVX2 で行います（スカラ版と同じ経路・メトリクス）。
    /// 状態 2j / 2j+1 が次状態 j（入力 0）と j+32（入力 1）へ遷移するので、次状態ごとに 2 候補を 8 レーンずつ比べ、
    /// 選んだ側（奇数状態なら 1）を 1 段 64 ビットのマスクに記録します。同点は偶数状態（スカラ版で先に評価される側）を残します。
    /// </summary>
    /// <param name="encodedBits">デパンクチャ後のコードビット。</param>
    /// <param name="presentMask">各コードビットが受信済み（パンクチャで抜けていない）か。</param>
    /// <param name="originalBitLength">復号後の元ビット長。</param>
    /// <param name="puncturedBitLength">パンクチャ後のコードビット数（メトリクス用）。</param>
    /// <param name="terminated">終端ビット付きとして扱う場合 true。</param>
    /// <param name="metrics">復号時のメトリクス。</param>
    /// <returns>復号したバイト列。</returns>
    private static byte[] DecodeHardAvx2(
        ReadOnlySpan<bool> encodedBits,
        ReadOnlySpan<bool> presentMask,
        int originalBitLength,
        int puncturedBitLength,
        bool terminated,
        out DecodeMetrics metrics)
    {
        var inputSymbolCount = encodedBits.Length / OutputBitsPerInputBit;
        var decisions = ArrayPool<ulong>.Shared.Rent(Math.Max(1, inputSymbolCount));
        var decidedBits = ArrayPool<bool>.Shared.Rent(Math.Max(1, inputSymbolCount));
        try
        {
            Span<int> metricA = stackalloc int[StateCount];
            Span<int> metricB = stackalloc int[StateCount];
            metricA.Fill(LargeMetric);
            metricA[0] = 0;
            ref var prev = ref MemoryMarshal.GetReference(metricA);
            ref var next = ref MemoryMarshal.GetReference(metricB);
            ref var branchEven = ref MemoryMarshal.GetArrayDataReference(HardBranchFromEvenState);
            ref var branchOdd = ref MemoryMarshal.GetArrayDataReference(HardBranchFromOddState);
            var evenOddOrder = Vector256.Create(0, 2, 4, 6, 1, 3, 5, 7);
            var large = Vector256.Create(LargeMetric);
            for (var t = 0; t < inputSymbolCount; t++)
            {
                var combo = (encodedBits[t * 2] ? 1 : 0)
                    | (encodedBits[(t * 2) + 1] ? 2 : 0)
                    | (presentMask[t * 2] ? 4 : 0)
                    | (presentMask[(t * 2) + 1] ? 8 : 0);
                var tableBase = (nuint)(combo * StateCount);
                ulong decision = 0;
                for (var k = 0; k < 4; k++)
                {
                    var v0 = Avx2.PermuteVar8x32(Vector256.LoadUnsafe(ref prev, (nuint)(16 * k)), evenOddOrder);
                    var v1 = Avx2.PermuteVar8x32(Vector256.LoadUnsafe(ref prev, (nuint)((16 * k) + 8)), evenOddOrder);
                    var evenStates = Avx2.Permute2x128(v0, v1, 0x20);
                    var oddStates = Avx2.Permute2x128(v0, v1, 0x31);
                    for (var input = 0; input < 2; input++)
                    {
                        var ns = (32 * input) + (8 * k);
                        var fromEven = Avx2.Add(evenStates, Vector256.LoadUnsafe(ref branchEven, tableBase + (nuint)ns));
                        var fromOdd = Avx2.Add(oddStates, Vector256.LoadUnsafe(ref branchOdd, tableBase + (nuint)ns));
                        var takeOdd = Avx2.CompareGreaterThan(fromEven, fromOdd);
                        var best = Avx2.Min(Avx2.BlendVariable(fromEven, fromOdd, takeOdd), large);
                        best.StoreUnsafe(ref next, (nuint)ns);
                        decision |= (ulong)(uint)Avx.MoveMask(takeOdd.AsSingle()) << ns;
                    }
                }

                decisions[t] = decision;
                ref var swap = ref prev;
                prev = ref next;
                next = ref swap;
            }

            var finalMetric = new int[StateCount];
            for (var s = 0; s < StateCount; s++)
            {
                finalMetric[s] = Unsafe.Add(ref prev, s);
            }

            var finalState = terminated ? 0 : FindMinMetricState(finalMetric);
            var bestMetric = finalMetric[finalState];
            if (bestMetric >= LargeMetric)
            {
                throw new ArgumentException("Failed to decode: no valid Viterbi path.", nameof(encodedBits));
            }

            var traceState = finalState;
            for (var t = inputSymbolCount - 1; t >= 0; t--)
            {
                decidedBits[t] = traceState >= HalfStateCount;
                var fromOdd = (int)((decisions[t] >> traceState) & 1UL);
                traceState = ((traceState & (HalfStateCount - 1)) << 1) | fromOdd;
            }

            var decoded = BitsToBytes(decidedBits.AsSpan(0, originalBitLength));
            metrics = new DecodeMetrics(
                PathHammingDistance: bestMetric,
                ComparedCodeBitCount: puncturedBitLength,
                CorrectedCodeBitCount: bestMetric,
                CorrectionRate: puncturedBitLength == 0 ? 0.0 : (double)bestMetric / puncturedBitLength);
            return decoded;
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(decisions, clearArray: false);
            ArrayPool<bool>.Shared.Return(decidedBits, clearArray: false);
        }
    }

    /// <summary>
    /// ハード判定の枝距離表を作ります。添字は [受信パターン × 64 + 次状態]。
    /// 受信パターンは bit0=rx0, bit1=rx1, bit2=rx0 受信済み, bit3=rx1 受信済み。
    /// </summary>
    /// <param name="fromOddState">次状態 j / j+32 の前状態として 2j+1 側を使う場合 true（false なら 2j）。</param>
    /// <returns>枝距離表（16 × 64）。</returns>
    private static int[] BuildHardBranchTable(bool fromOddState)
    {
        var table = new int[16 * StateCount];
        for (var combo = 0; combo < 16; combo++)
        {
            var rx0 = combo & 1;
            var rx1 = (combo >> 1) & 1;
            var hasRx0 = (combo & 4) != 0;
            var hasRx1 = (combo & 8) != 0;
            for (var ns = 0; ns < StateCount; ns++)
            {
                var input = ns >= HalfStateCount ? 1 : 0;
                var state = ((ns & (HalfStateCount - 1)) << 1) | (fromOddState ? 1 : 0);
                if ((input == 0 ? NextStateWhenInput0[state] : NextStateWhenInput1[state]) != ns)
                {
                    throw new InvalidOperationException("Trellis is not a radix-2 butterfly.");
                }

                var out0 = input == 0 ? Output0WhenInput0[state] : Output0WhenInput1[state];
                var out1 = input == 0 ? Output1WhenInput0[state] : Output1WhenInput1[state];
                var distance = 0;
                if (hasRx0)
                {
                    distance += out0 ^ rx0;
                }

                if (hasRx1)
                {
                    distance += out1 ^ rx1;
                }

                table[(combo * StateCount) + ns] = distance;
            }
        }

        return table;
    }

    /// <summary>
    /// ソフト判定LLRを用いて復号します。
    /// </summary>
    /// <param name="codeLlrs">受信コードビットのLLR列。</param>
    /// <param name="originalByteLength">復号後の元バイト長。</param>
    /// <param name="terminated">終端ビット付きとして扱う場合 true。</param>
    /// <param name="punctureRate">パンクチャ率。</param>
    /// <returns>復号したバイト列。</returns>
    public static byte[] DecodeSoft(
        ReadOnlySpan<double> codeLlrs,
        int originalByteLength,
        bool terminated = true,
        PunctureRate punctureRate = PunctureRate.Rate1_2)
    {
        return DecodeSoftToInfoLlrs(codeLlrs, originalByteLength, out _, terminated, punctureRate);
    }

    /// <summary>
    /// ソフト判定LLRで復号し、情報ビットLLRも返します。
    /// </summary>
    /// <param name="codeLlrs">受信コードビットのLLR列。</param>
    /// <param name="originalByteLength">復号後の元バイト長。</param>
    /// <param name="infoLlrs">情報ビットの事後LLR。</param>
    /// <param name="terminated">終端ビット付きとして扱う場合 true。</param>
    /// <param name="punctureRate">パンクチャ率。</param>
    /// <returns>復号したバイト列。</returns>
    public static byte[] DecodeSoftToInfoLlrs(
        ReadOnlySpan<double> codeLlrs,
        int originalByteLength,
        out double[] infoLlrs,
        bool terminated = true,
        PunctureRate punctureRate = PunctureRate.Rate1_2)
    {
        return DecodeSoftToInfoLlrs(
            codeLlrs,
            originalByteLength,
            out infoLlrs,
            out _,
            terminated,
            punctureRate);
    }

    /// <summary>
    /// ソフト判定LLRを復号し、チャネル硬判定との差分から訂正率メトリクスも返します。
    /// </summary>
    /// <param name="codeLlrs">パンクチャ後の符号語 LLR。</param>
    /// <param name="originalByteLength">復号後の情報バイト長。</param>
    /// <param name="infoLlrs">情報ビットのソフト LLR（出力）。</param>
    /// <param name="metrics">訂正率メトリクス（出力）。</param>
    /// <param name="terminated">終端ビット付きとして扱う場合 true。</param>
    /// <param name="punctureRate">パンクチャ率。</param>
    /// <returns>復号した情報バイト列。</returns>
    public static byte[] DecodeSoftToInfoLlrs(
        ReadOnlySpan<double> codeLlrs,
        int originalByteLength,
        out double[] infoLlrs,
        out DecodeMetrics metrics,
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
        var payloadBitsBuffer = ArrayPool<bool>.Shared.Rent(originalBitLength);
        try
        {
            var fullCodeLlrs = fullCodeLlrsBuffer.AsSpan(0, expectedCodeBitLength);
            DepunctureSoftInto(codeLlrs, expectedCodeBitLength, punctureRate, fullCodeLlrs);

            infoLlrs = new double[originalBitLength];
            ComputeInfoLlrs(fullCodeLlrs, terminated, infoLlrs);

            var payloadBits = payloadBitsBuffer.AsSpan(0, originalBitLength);
            for (var t = 0; t < originalBitLength; t++)
            {
                payloadBits[t] = infoLlrs[t] < 0.0;
            }

            var decoded = BitsToBytes(payloadBits);
            metrics = MeasureSoftCorrectionRate(
                codeLlrs[..expectedPuncturedBitLength],
                decoded,
                terminated,
                punctureRate);
            return decoded;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(fullCodeLlrsBuffer, clearArray: false);
            ArrayPool<bool>.Shared.Return(payloadBitsBuffer, clearArray: false);
        }
    }

    /// <summary>
    /// Max-Log BCJR の計算経路です（テストで経路ごとの一致を確かめるために指定できる）。
    /// </summary>
    internal enum TrellisKernel
    {
        /// <summary>CPU が対応する最速の経路。</summary>
        Auto,

        /// <summary>スカラ。</summary>
        Scalar,

        /// <summary>128 bit SIMD（SSE2 / AdvSimd）。</summary>
        Vector128,

        /// <summary>256 bit SIMD（AVX2）。</summary>
        Vector256
    }

    /// <summary>
    /// 指定した経路が現在の CPU で使えるかを返します。
    /// </summary>
    /// <param name="kernel">計算経路。</param>
    /// <returns>使える場合 true。</returns>
    internal static bool IsTrellisKernelSupported(TrellisKernel kernel) => kernel switch
    {
        TrellisKernel.Auto or TrellisKernel.Scalar => true,
        TrellisKernel.Vector128 => Sse2.IsSupported || AdvSimd.Arm64.IsSupported,
        TrellisKernel.Vector256 => Avx2.IsSupported,
        _ => false
    };

    /// <summary>
    /// 母符号長の LLR から Max-Log BCJR で情報ビットの事後 LLR を求めます（正がビット 0）。
    /// </summary>
    /// <param name="fullCodeLlrs">デパンクチャ済みの母符号 LLR（入力 1 ビットあたり 2 個）。</param>
    /// <param name="terminated">終端ビット付き（最終状態 0）として扱う場合 true。</param>
    /// <param name="infoLlrs">情報ビット LLR の出力先（先頭から入力ビット数以下の長さ）。</param>
    /// <param name="kernel">計算経路。</param>
    internal static void ComputeInfoLlrs(
        ReadOnlySpan<double> fullCodeLlrs,
        bool terminated,
        Span<double> infoLlrs,
        TrellisKernel kernel = TrellisKernel.Auto)
    {
        var tCount = fullCodeLlrs.Length / OutputBitsPerInputBit;
        if (infoLlrs.Length > tCount)
        {
            throw new ArgumentException("Info LLR length exceeds the trellis length.", nameof(infoLlrs));
        }

        if (kernel == TrellisKernel.Auto)
        {
            kernel = IsTrellisKernelSupported(TrellisKernel.Vector256) ? TrellisKernel.Vector256
                : IsTrellisKernelSupported(TrellisKernel.Vector128) ? TrellisKernel.Vector128
                : TrellisKernel.Scalar;
        }
        else if (!IsTrellisKernelSupported(kernel))
        {
            throw new PlatformNotSupportedException($"Trellis kernel {kernel} is not supported on this CPU.");
        }

        // α を全段保持するとキャッシュに乗らないため、区間先頭の α だけ残し、後ろ向きの直前に区間ごと再計算する
        var blockCount = (tCount + TrellisBlockSteps - 1) / TrellisBlockSteps;
        var checkpoints = ArrayPool<double>.Shared.Rent(Math.Max(1, blockCount) * StateCount);
        var alpha = ArrayPool<double>.Shared.Rent((TrellisBlockSteps + 1) * StateCount);
        try
        {
            Array.Fill(alpha, NegInfMetric, 0, StateCount);
            alpha[0] = 0.0;
            for (var b = 0; b < blockCount; b++)
            {
                Array.Copy(alpha, 0, checkpoints, b * StateCount, StateCount);
                if (b == blockCount - 1)
                {
                    break;
                }

                Forward(kernel, fullCodeLlrs, b * TrellisBlockSteps, TrellisBlockSteps, alpha);
                Array.Copy(alpha, TrellisBlockSteps * StateCount, alpha, 0, StateCount);
            }

            Span<double> betaNext = stackalloc double[StateCount];
            Span<double> betaCurr = stackalloc double[StateCount];
            betaNext.Fill(terminated ? NegInfMetric : 0.0);
            betaNext[0] = 0.0;
            for (var b = blockCount - 1; b >= 0; b--)
            {
                var tStart = b * TrellisBlockSteps;
                var steps = Math.Min(TrellisBlockSteps, tCount - tStart);
                Array.Copy(checkpoints, b * StateCount, alpha, 0, StateCount);
                Forward(kernel, fullCodeLlrs, tStart, steps, alpha);
                Backward(kernel, fullCodeLlrs, tStart, steps, alpha, betaNext, betaCurr, infoLlrs);
                if ((steps & 1) != 0)
                {
                    var swap = betaNext;
                    betaNext = betaCurr;
                    betaCurr = swap;
                }
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(checkpoints, clearArray: false);
            ArrayPool<double>.Shared.Return(alpha, clearArray: false);
        }
    }

    /// <summary>
    /// 指定経路で 1 区間の前向き（α）を求めます。
    /// </summary>
    /// <param name="kernel">計算経路。</param>
    /// <param name="llrs">母符号 LLR（全段）。</param>
    /// <param name="tStart">区間先頭の段。</param>
    /// <param name="steps">区間の段数。</param>
    /// <param name="alpha">区間の α（行 0 が段 tStart。行 1〜steps を書き込む）。</param>
    private static void Forward(TrellisKernel kernel, ReadOnlySpan<double> llrs, int tStart, int steps, double[] alpha)
    {
        switch (kernel)
        {
            case TrellisKernel.Vector256:
                ForwardVector256(llrs, tStart, steps, alpha);
                break;
            case TrellisKernel.Vector128:
                ForwardVector128(llrs, tStart, steps, alpha);
                break;
            default:
                ForwardScalar(llrs, tStart, steps, alpha);
                break;
        }
    }

    /// <summary>
    /// 指定経路で 1 区間の後ろ向き（β）と情報ビット LLR を求めます。
    /// </summary>
    /// <param name="kernel">計算経路。</param>
    /// <param name="llrs">母符号 LLR（全段）。</param>
    /// <param name="tStart">区間先頭の段。</param>
    /// <param name="steps">区間の段数。</param>
    /// <param name="alpha">区間の α（行 0 が段 tStart）。</param>
    /// <param name="betaNext">区間末尾の β（作業領域として書き換える）。</param>
    /// <param name="betaCurr">β の作業領域。steps が奇数なら区間先頭の β はこちらに残る。</param>
    /// <param name="infoLlrs">情報ビット LLR の出力先（全段の添字）。</param>
    private static void Backward(
        TrellisKernel kernel,
        ReadOnlySpan<double> llrs,
        int tStart,
        int steps,
        double[] alpha,
        Span<double> betaNext,
        Span<double> betaCurr,
        Span<double> infoLlrs)
    {
        switch (kernel)
        {
            case TrellisKernel.Vector256:
                BackwardVector256(llrs, tStart, steps, alpha, betaNext, betaCurr, infoLlrs);
                break;
            case TrellisKernel.Vector128:
                BackwardVector128(llrs, tStart, steps, alpha, betaNext, betaCurr, infoLlrs);
                break;
            default:
                BackwardScalar(llrs, tStart, steps, alpha, betaNext, betaCurr, infoLlrs);
                break;
        }
    }

    /// <summary>
    /// 前向き（α）をスカラで求めます。alpha の先頭行は初期化済みであること。
    /// </summary>
    /// <param name="llrs">母符号 LLR。</param>
    /// <param name="tStart">区間先頭の段。</param>
    /// <param name="steps">区間の段数。</param>
    /// <param name="alpha">区間の α（行 0 が段 tStart。行 1〜steps を書き込む）。</param>
    private static void ForwardScalar(ReadOnlySpan<double> llrs, int tStart, int steps, double[] alpha)
    {
        for (var k = 0; k < steps; k++)
        {
            var t = tStart + k;
            var llr0 = llrs[t * 2];
            var llr1 = llrs[(t * 2) + 1];
            var metric01 = llr0;
            var metric10 = llr1;
            var metric11 = llr0 + llr1;
            var rowBase = k * StateCount;
            var nextRowBase = (k + 1) * StateCount;
            for (var ns = 0; ns < StateCount; ns++)
            {
                alpha[nextRowBase + ns] = NegInfMetric;
            }

            for (var state = 0; state < StateCount; state++)
            {
                var a = alpha[rowBase + state];
                if (a <= NegInfMetric / 2)
                {
                    continue;
                }

                var candidate0 = a + SelectBranchMetric(OutputMaskWhenInput0[state], metric01, metric10, metric11);
                var index0 = nextRowBase + NextStateWhenInput0[state];
                if (candidate0 > alpha[index0])
                {
                    alpha[index0] = candidate0;
                }

                var candidate1 = a + SelectBranchMetric(OutputMaskWhenInput1[state], metric01, metric10, metric11);
                var index1 = nextRowBase + NextStateWhenInput1[state];
                if (candidate1 > alpha[index1])
                {
                    alpha[index1] = candidate1;
                }
            }
        }
    }

    /// <summary>
    /// 後ろ向き（β）と情報ビット LLR をスカラで求めます。
    /// </summary>
    /// <param name="llrs">母符号 LLR。</param>
    /// <param name="tStart">区間先頭の段。</param>
    /// <param name="steps">区間の段数。</param>
    /// <param name="alpha">区間の α（行 0 が段 tStart）。</param>
    /// <param name="betaNext">区間末尾の β（作業領域として書き換える）。</param>
    /// <param name="betaCurr">β の作業領域。</param>
    /// <param name="infoLlrs">情報ビット LLR の出力先。</param>
    private static void BackwardScalar(
        ReadOnlySpan<double> llrs,
        int tStart,
        int steps,
        double[] alpha,
        Span<double> betaNext,
        Span<double> betaCurr,
        Span<double> infoLlrs)
    {
        for (var k = steps - 1; k >= 0; k--)
        {
            var t = tStart + k;
            var llr0 = llrs[t * 2];
            var llr1 = llrs[(t * 2) + 1];
            var metric01 = llr0;
            var metric10 = llr1;
            var metric11 = llr0 + llr1;
            var best0 = NegInfMetric;
            var best1 = NegInfMetric;
            var rowBase = k * StateCount;

            for (var state = 0; state < StateCount; state++)
            {
                var b0 = betaNext[NextStateWhenInput0[state]];
                var candidate0 = b0 > NegInfMetric / 2
                    ? b0 + SelectBranchMetric(OutputMaskWhenInput0[state], metric01, metric10, metric11)
                    : NegInfMetric;
                var b1 = betaNext[NextStateWhenInput1[state]];
                var candidate1 = b1 > NegInfMetric / 2
                    ? b1 + SelectBranchMetric(OutputMaskWhenInput1[state], metric01, metric10, metric11)
                    : NegInfMetric;
                betaCurr[state] = candidate0 > candidate1 ? candidate0 : candidate1;

                var a = alpha[rowBase + state];
                if (a <= NegInfMetric / 2)
                {
                    continue;
                }

                if (candidate0 > NegInfMetric / 2 && a + candidate0 > best0)
                {
                    best0 = a + candidate0;
                }

                if (candidate1 > NegInfMetric / 2 && a + candidate1 > best1)
                {
                    best1 = a + candidate1;
                }
            }

            if (t < infoLlrs.Length)
            {
                infoLlrs[t] = PosteriorLlr(best0, best1);
            }

            var betaSwap = betaNext;
            betaNext = betaCurr;
            betaCurr = betaSwap;
        }
    }

    /// <summary>
    /// 前向き（α）を AVX2 で求めます。状態 2j / 2j+1 から次状態 j（入力 0）/ j+32（入力 1）へのバタフライを 4 本ずつ処理します。
    /// </summary>
    /// <param name="llrs">母符号 LLR。</param>
    /// <param name="tStart">区間先頭の段。</param>
    /// <param name="steps">区間の段数。</param>
    /// <param name="alpha">区間の α（行 0 が段 tStart。行 1〜steps を書き込む）。</param>
    private static void ForwardVector256(ReadOnlySpan<double> llrs, int tStart, int steps, double[] alpha)
    {
        ref var masks = ref MemoryMarshal.GetArrayDataReference(BranchBitMasks);
        ref var llrRef = ref MemoryMarshal.GetReference(llrs);
        ref var prev = ref MemoryMarshal.GetArrayDataReference(alpha);
        for (var k = 0; k < steps; k++)
        {
            var t = tStart + k;
            var l0 = Vector256.Create(Unsafe.Add(ref llrRef, 2 * t));
            var l1 = Vector256.Create(Unsafe.Add(ref llrRef, (2 * t) + 1));
            ref var next = ref Unsafe.Add(ref prev, StateCount);
            for (var j = 0; j < HalfStateCount; j += 4)
            {
                Deinterleave256(
                    Vector256.LoadUnsafe(ref prev, (nuint)(2 * j)),
                    Vector256.LoadUnsafe(ref prev, (nuint)((2 * j) + 4)),
                    out var even,
                    out var odd);
                var g0 = Gamma256(ref masks, 0, j, l0, l1);
                var g1 = Gamma256(ref masks, 1, j, l0, l1);
                var to0 = Avx.Max(Avx.Add(even, g0), Avx.Add(odd, g1));
                var to1 = Avx.Max(Avx.Add(even, g1), Avx.Add(odd, g0));
                to0.StoreUnsafe(ref next, (nuint)j);
                to1.StoreUnsafe(ref next, (nuint)(HalfStateCount + j));
            }

            prev = ref next;
        }
    }

    /// <summary>
    /// 後ろ向き（β）と情報ビット LLR を AVX2 で求めます。
    /// </summary>
    /// <param name="llrs">母符号 LLR。</param>
    /// <param name="tStart">区間先頭の段。</param>
    /// <param name="steps">区間の段数。</param>
    /// <param name="alpha">区間の α（行 0 が段 tStart）。</param>
    /// <param name="betaNext">区間末尾の β（作業領域として書き換える）。</param>
    /// <param name="betaCurr">β の作業領域。</param>
    /// <param name="infoLlrs">情報ビット LLR の出力先。</param>
    private static void BackwardVector256(
        ReadOnlySpan<double> llrs,
        int tStart,
        int steps,
        double[] alpha,
        Span<double> betaNext,
        Span<double> betaCurr,
        Span<double> infoLlrs)
    {
        ref var masks = ref MemoryMarshal.GetArrayDataReference(BranchBitMasks);
        ref var llrRef = ref MemoryMarshal.GetReference(llrs);
        ref var alphaRef = ref MemoryMarshal.GetArrayDataReference(alpha);
        var negInf = Vector256.Create(NegInfMetric);
        for (var k = steps - 1; k >= 0; k--)
        {
            var t = tStart + k;
            var l0 = Vector256.Create(Unsafe.Add(ref llrRef, 2 * t));
            var l1 = Vector256.Create(Unsafe.Add(ref llrRef, (2 * t) + 1));
            ref var bn = ref MemoryMarshal.GetReference(betaNext);
            ref var bc = ref MemoryMarshal.GetReference(betaCurr);
            ref var a = ref Unsafe.Add(ref alphaRef, k * StateCount);
            var best0 = negInf;
            var best1 = negInf;
            for (var j = 0; j < HalfStateCount; j += 4)
            {
                var b0 = Vector256.LoadUnsafe(ref bn, (nuint)j);
                var b1 = Vector256.LoadUnsafe(ref bn, (nuint)(HalfStateCount + j));
                var g0 = Gamma256(ref masks, 0, j, l0, l1);
                var g1 = Gamma256(ref masks, 1, j, l0, l1);
                var c0Even = Avx.Add(b0, g0);
                var c1Even = Avx.Add(b1, g1);
                var c0Odd = Avx.Add(b0, g1);
                var c1Odd = Avx.Add(b1, g0);
                Interleave256(Avx.Max(c0Even, c1Even), Avx.Max(c0Odd, c1Odd), out var lo, out var hi);
                lo.StoreUnsafe(ref bc, (nuint)(2 * j));
                hi.StoreUnsafe(ref bc, (nuint)((2 * j) + 4));

                Deinterleave256(
                    Vector256.LoadUnsafe(ref a, (nuint)(2 * j)),
                    Vector256.LoadUnsafe(ref a, (nuint)((2 * j) + 4)),
                    out var aEven,
                    out var aOdd);
                best0 = Avx.Max(best0, Avx.Max(Avx.Add(aEven, c0Even), Avx.Add(aOdd, c0Odd)));
                best1 = Avx.Max(best1, Avx.Max(Avx.Add(aEven, c1Even), Avx.Add(aOdd, c1Odd)));
            }

            if (t < infoLlrs.Length)
            {
                infoLlrs[t] = PosteriorLlr(HorizontalMax(best0), HorizontalMax(best1));
            }

            var betaSwap = betaNext;
            betaNext = betaCurr;
            betaCurr = betaSwap;
        }
    }

    /// <summary>
    /// 前向き（α）を 128 bit SIMD で求めます（バタフライを 2 本ずつ処理）。
    /// </summary>
    /// <param name="llrs">母符号 LLR。</param>
    /// <param name="tStart">区間先頭の段。</param>
    /// <param name="steps">区間の段数。</param>
    /// <param name="alpha">区間の α（行 0 が段 tStart。行 1〜steps を書き込む）。</param>
    private static void ForwardVector128(ReadOnlySpan<double> llrs, int tStart, int steps, double[] alpha)
    {
        ref var masks = ref MemoryMarshal.GetArrayDataReference(BranchBitMasks);
        ref var llrRef = ref MemoryMarshal.GetReference(llrs);
        ref var prev = ref MemoryMarshal.GetArrayDataReference(alpha);
        for (var k = 0; k < steps; k++)
        {
            var t = tStart + k;
            var l0 = Vector128.Create(Unsafe.Add(ref llrRef, 2 * t));
            var l1 = Vector128.Create(Unsafe.Add(ref llrRef, (2 * t) + 1));
            ref var next = ref Unsafe.Add(ref prev, StateCount);
            for (var j = 0; j < HalfStateCount; j += 2)
            {
                var x = Vector128.LoadUnsafe(ref prev, (nuint)(2 * j));
                var y = Vector128.LoadUnsafe(ref prev, (nuint)((2 * j) + 2));
                var even = PairLow128(x, y);
                var odd = PairHigh128(x, y);
                var g0 = Gamma128(ref masks, 0, j, l0, l1);
                var g1 = Gamma128(ref masks, 1, j, l0, l1);
                var to0 = Vector128.Max(even + g0, odd + g1);
                var to1 = Vector128.Max(even + g1, odd + g0);
                to0.StoreUnsafe(ref next, (nuint)j);
                to1.StoreUnsafe(ref next, (nuint)(HalfStateCount + j));
            }

            prev = ref next;
        }
    }

    /// <summary>
    /// 後ろ向き（β）と情報ビット LLR を 128 bit SIMD で求めます。
    /// </summary>
    /// <param name="llrs">母符号 LLR。</param>
    /// <param name="tStart">区間先頭の段。</param>
    /// <param name="steps">区間の段数。</param>
    /// <param name="alpha">区間の α（行 0 が段 tStart）。</param>
    /// <param name="betaNext">区間末尾の β（作業領域として書き換える）。</param>
    /// <param name="betaCurr">β の作業領域。</param>
    /// <param name="infoLlrs">情報ビット LLR の出力先。</param>
    private static void BackwardVector128(
        ReadOnlySpan<double> llrs,
        int tStart,
        int steps,
        double[] alpha,
        Span<double> betaNext,
        Span<double> betaCurr,
        Span<double> infoLlrs)
    {
        ref var masks = ref MemoryMarshal.GetArrayDataReference(BranchBitMasks);
        ref var llrRef = ref MemoryMarshal.GetReference(llrs);
        ref var alphaRef = ref MemoryMarshal.GetArrayDataReference(alpha);
        var negInf = Vector128.Create(NegInfMetric);
        for (var k = steps - 1; k >= 0; k--)
        {
            var t = tStart + k;
            var l0 = Vector128.Create(Unsafe.Add(ref llrRef, 2 * t));
            var l1 = Vector128.Create(Unsafe.Add(ref llrRef, (2 * t) + 1));
            ref var bn = ref MemoryMarshal.GetReference(betaNext);
            ref var bc = ref MemoryMarshal.GetReference(betaCurr);
            ref var a = ref Unsafe.Add(ref alphaRef, k * StateCount);
            var best0 = negInf;
            var best1 = negInf;
            for (var j = 0; j < HalfStateCount; j += 2)
            {
                var b0 = Vector128.LoadUnsafe(ref bn, (nuint)j);
                var b1 = Vector128.LoadUnsafe(ref bn, (nuint)(HalfStateCount + j));
                var g0 = Gamma128(ref masks, 0, j, l0, l1);
                var g1 = Gamma128(ref masks, 1, j, l0, l1);
                var c0Even = b0 + g0;
                var c1Even = b1 + g1;
                var c0Odd = b0 + g1;
                var c1Odd = b1 + g0;
                var betaEven = Vector128.Max(c0Even, c1Even);
                var betaOdd = Vector128.Max(c0Odd, c1Odd);
                PairLow128(betaEven, betaOdd).StoreUnsafe(ref bc, (nuint)(2 * j));
                PairHigh128(betaEven, betaOdd).StoreUnsafe(ref bc, (nuint)((2 * j) + 2));

                var x = Vector128.LoadUnsafe(ref a, (nuint)(2 * j));
                var y = Vector128.LoadUnsafe(ref a, (nuint)((2 * j) + 2));
                var aEven = PairLow128(x, y);
                var aOdd = PairHigh128(x, y);
                best0 = Vector128.Max(best0, Vector128.Max(aEven + c0Even, aOdd + c0Odd));
                best1 = Vector128.Max(best1, Vector128.Max(aEven + c1Even, aOdd + c1Odd));
            }

            if (t < infoLlrs.Length)
            {
                infoLlrs[t] = PosteriorLlr(
                    Math.Max(best0.GetElement(0), best0.GetElement(1)),
                    Math.Max(best1.GetElement(0), best1.GetElement(1)));
            }

            var betaSwap = betaNext;
            betaNext = betaCurr;
            betaCurr = betaSwap;
        }
    }

    /// <summary>
    /// 入力 0 / 1 の最良パスメトリックから事後 LLR を求めます（到達不能側は NegInfMetric に揃える）。
    /// </summary>
    /// <param name="best0">入力 0 の最良メトリック。</param>
    /// <param name="best1">入力 1 の最良メトリック。</param>
    /// <returns>best0 − best1（非有限なら 0）。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double PosteriorLlr(double best0, double best1)
    {
        var app = Math.Max(best0, NegInfMetric) - Math.Max(best1, NegInfMetric);
        return double.IsFinite(app) ? app : 0.0;
    }

    /// <summary>
    /// 偶数状態 2j から入力 input で進む枝の枝メトリック（出力ビットが 1 の位置の LLR の和）を 4 状態分求めます。
    /// 奇数状態 2j+1 の枝は入力を反転した偶数状態の枝と同じ値になります（<see cref="BuildBranchBitMasks"/>）。
    /// </summary>
    /// <param name="masks">枝出力ビットのマスク表。</param>
    /// <param name="input">入力ビット。</param>
    /// <param name="j">先頭のバタフライ番号。</param>
    /// <param name="l0">出力ビット 0 の LLR（全レーン同値）。</param>
    /// <param name="l1">出力ビット 1 の LLR（全レーン同値）。</param>
    /// <returns>4 レーンの枝メトリック。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> Gamma256(ref double masks, int input, int j, Vector256<double> l0, Vector256<double> l1)
    {
        var offset = (input * StateCount) + j;
        return Avx.Add(
            Avx.And(l0, Vector256.LoadUnsafe(ref masks, (nuint)offset)),
            Avx.And(l1, Vector256.LoadUnsafe(ref masks, (nuint)(offset + HalfStateCount))));
    }

    /// <summary>
    /// 偶数状態 2j から入力 input で進む枝の枝メトリックを 2 状態分求めます。
    /// </summary>
    /// <param name="masks">枝出力ビットのマスク表。</param>
    /// <param name="input">入力ビット。</param>
    /// <param name="j">先頭のバタフライ番号。</param>
    /// <param name="l0">出力ビット 0 の LLR（全レーン同値）。</param>
    /// <param name="l1">出力ビット 1 の LLR（全レーン同値）。</param>
    /// <returns>2 レーンの枝メトリック。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<double> Gamma128(ref double masks, int input, int j, Vector128<double> l0, Vector128<double> l1)
    {
        var offset = (input * StateCount) + j;
        return (l0 & Vector128.LoadUnsafe(ref masks, (nuint)offset))
            + (l1 & Vector128.LoadUnsafe(ref masks, (nuint)(offset + HalfStateCount)));
    }

    /// <summary>
    /// 連続 8 状態 [s0..s7] を偶数状態 [s0,s2,s4,s6] と奇数状態 [s1,s3,s5,s7] に分けます。
    /// </summary>
    /// <param name="x">状態 s0..s3。</param>
    /// <param name="y">状態 s4..s7。</param>
    /// <param name="even">偶数状態。</param>
    /// <param name="odd">奇数状態。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Deinterleave256(Vector256<double> x, Vector256<double> y, out Vector256<double> even, out Vector256<double> odd)
    {
        even = Avx2.Permute4x64(Avx.UnpackLow(x, y), 0b11_01_10_00);
        odd = Avx2.Permute4x64(Avx.UnpackHigh(x, y), 0b11_01_10_00);
    }

    /// <summary>
    /// 偶数状態と奇数状態を交互に並べ直します（<see cref="Deinterleave256"/> の逆）。
    /// </summary>
    /// <param name="even">偶数状態 [e0..e3]。</param>
    /// <param name="odd">奇数状態 [o0..o3]。</param>
    /// <param name="lo">[e0,o0,e1,o1]。</param>
    /// <param name="hi">[e2,o2,e3,o3]。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Interleave256(Vector256<double> even, Vector256<double> odd, out Vector256<double> lo, out Vector256<double> hi)
    {
        var a = Avx.UnpackLow(even, odd);
        var b = Avx.UnpackHigh(even, odd);
        lo = Avx.Permute2x128(a, b, 0x20);
        hi = Avx.Permute2x128(a, b, 0x31);
    }

    /// <summary>
    /// 2 つのベクトルの先頭要素どうしを並べます（[x0, y0]）。
    /// </summary>
    /// <param name="x">1 つ目。</param>
    /// <param name="y">2 つ目。</param>
    /// <returns>[x0, y0]。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<double> PairLow128(Vector128<double> x, Vector128<double> y) =>
        Sse2.IsSupported ? Sse2.UnpackLow(x, y) : AdvSimd.Arm64.ZipLow(x, y);

    /// <summary>
    /// 2 つのベクトルの末尾要素どうしを並べます（[x1, y1]）。
    /// </summary>
    /// <param name="x">1 つ目。</param>
    /// <param name="y">2 つ目。</param>
    /// <returns>[x1, y1]。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<double> PairHigh128(Vector128<double> x, Vector128<double> y) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(x, y) : AdvSimd.Arm64.ZipHigh(x, y);

    /// <summary>
    /// 4 レーンの最大値を返します。
    /// </summary>
    /// <param name="value">対象。</param>
    /// <returns>最大値。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double HorizontalMax(Vector256<double> value)
    {
        var m = Sse2.Max(value.GetLower(), value.GetUpper());
        return Math.Max(m.ToScalar(), m.GetElement(1));
    }

    /// <summary>
    /// 偶数状態 2j・入力 input の枝出力ビットごとに、1 なら全ビット 1、0 なら 0 の double を並べた表を作ります。
    /// </summary>
    /// <returns>（入力, 出力ビット, j）順のマスク表（入力ごとに 64 個）。</returns>
    private static double[] BuildBranchBitMasks()
    {
        var ones = BitConverter.Int64BitsToDouble(-1L);
        var table = new double[2 * StateCount];
        for (var j = 0; j < HalfStateCount; j++)
        {
            // SIMD 経路は「状態 2j+1 の枝 = 入力を反転した状態 2j の枝」を前提にする（両生成多項式が入力ビットと最古ビットを含むこと）
            if (OutputMaskWhenInput0[(2 * j) + 1] != OutputMaskWhenInput1[2 * j]
                || OutputMaskWhenInput1[(2 * j) + 1] != OutputMaskWhenInput0[2 * j])
            {
                throw new InvalidOperationException("Generator polynomials must tap both the input bit and the oldest register bit.");
            }

            for (var input = 0; input < 2; input++)
            {
                var mask = (input == 0 ? OutputMaskWhenInput0 : OutputMaskWhenInput1)[2 * j];
                table[(input * StateCount) + j] = (mask & 1) != 0 ? ones : 0.0;
                table[(input * StateCount) + HalfStateCount + j] = (mask & 2) != 0 ? ones : 0.0;
            }
        }

        return table;
    }

    /// <summary>
    /// ソフト受信符号語と再符号化結果のハミング距離から訂正率を推定します。
    /// </summary>
    /// <param name="puncturedCodeLlrs">パンクチャ後の受信 LLR。</param>
    /// <param name="decodedInfo">復号済み情報バイト列。</param>
    /// <param name="terminated">終端ビット付きとして扱う場合 true。</param>
    /// <param name="punctureRate">パンクチャ率。</param>
    /// <returns>訂正ビット数・訂正率などのメトリクス。</returns>
    public static DecodeMetrics MeasureSoftCorrectionRate(
        ReadOnlySpan<double> puncturedCodeLlrs,
        byte[] decodedInfo,
        bool terminated = true,
        PunctureRate punctureRate = PunctureRate.Rate1_2)
    {
        ArgumentNullException.ThrowIfNull(decodedInfo);
        var reencoded = Encode(decodedInfo, terminate: terminated, punctureRate: punctureRate);
        var bitCount = Math.Min(
            puncturedCodeLlrs.Length,
            GetEncodedBitLength(decodedInfo.Length * 8, terminated, punctureRate));
        var corrected = CountHardDecisionMismatches(
            puncturedCodeLlrs[..bitCount],
            reencoded,
            bitCount);

        return new DecodeMetrics(
            PathHammingDistance: corrected,
            ComparedCodeBitCount: bitCount,
            CorrectedCodeBitCount: corrected,
            CorrectionRate: bitCount == 0 ? 0.0 : (double)corrected / bitCount);
    }

    /// <summary>
    /// 受信 LLR の硬判定と期待符号ビットの不一致数を数えます。
    /// </summary>
    /// <param name="llrs">受信ソフト LLR。正がビット 1。</param>
    /// <param name="expectedPacked">期待する符号ビット（MSB 先頭のパック）。</param>
    /// <param name="bitCount">比較するビット数。</param>
    /// <returns>不一致ビット数。</returns>
    private static int CountHardDecisionMismatches(
        ReadOnlySpan<double> llrs,
        ReadOnlySpan<byte> expectedPacked,
        int bitCount)
    {
        if (bitCount <= 0)
        {
            return 0;
        }

        var fullBytes = bitCount >> 3;
        var corrected = 0;
        if (Avx.IsSupported && fullBytes > 0)
        {
            corrected += CountHardDecisionMismatchesAvx(llrs, expectedPacked, fullBytes);
        }
        else if (AdvSimd.Arm64.IsSupported && fullBytes > 0)
        {
            corrected += CountHardDecisionMismatchesAdvSimd(llrs, expectedPacked, fullBytes);
        }
        else
        {
            corrected += CountHardDecisionMismatchesScalarPacked(llrs, expectedPacked, fullBytes);
        }

        var tailStart = fullBytes << 3;
        for (var i = tailStart; i < bitCount; i++)
        {
            // 本系のソフト LLR は正でビット1（変調: bit→+1、復調硬判定: Real>=0→1）。
            var expected = ((expectedPacked[i >> 3] >> (7 - (i & 7))) & 1) != 0;
            var hard = llrs[i] >= 0.0;
            if (expected != hard)
            {
                corrected++;
            }
        }

        return corrected;
    }

    /// <summary>
    /// 完全バイト分の硬判定不一致をスカラで数えます。
    /// </summary>
    /// <param name="llrs">受信ソフト LLR。正がビット 1。</param>
    /// <param name="expectedPacked">期待する符号ビット（MSB 先頭のパック）。</param>
    /// <param name="fullBytes">比較する完全バイト数。</param>
    /// <returns>不一致ビット数。</returns>
    private static int CountHardDecisionMismatchesScalarPacked(
        ReadOnlySpan<double> llrs,
        ReadOnlySpan<byte> expectedPacked,
        int fullBytes)
    {
        var corrected = 0;
        for (var i = 0; i < fullBytes; i++)
        {
            var baseBit = i << 3;
            byte hardPacked = 0;
            if (llrs[baseBit] >= 0.0) hardPacked |= 0x80;
            if (llrs[baseBit + 1] >= 0.0) hardPacked |= 0x40;
            if (llrs[baseBit + 2] >= 0.0) hardPacked |= 0x20;
            if (llrs[baseBit + 3] >= 0.0) hardPacked |= 0x10;
            if (llrs[baseBit + 4] >= 0.0) hardPacked |= 0x08;
            if (llrs[baseBit + 5] >= 0.0) hardPacked |= 0x04;
            if (llrs[baseBit + 6] >= 0.0) hardPacked |= 0x02;
            if (llrs[baseBit + 7] >= 0.0) hardPacked |= 0x01;
            corrected += BitOperations.PopCount((uint)(hardPacked ^ expectedPacked[i]));
        }

        return corrected;
    }

    /// <summary>
    /// 完全バイト分の硬判定不一致を AVX で数えます。
    /// </summary>
    /// <param name="llrs">受信ソフト LLR。正がビット 1。</param>
    /// <param name="expectedPacked">期待する符号ビット（MSB 先頭のパック）。</param>
    /// <param name="fullBytes">比較する完全バイト数。</param>
    /// <returns>不一致ビット数。</returns>
    private static int CountHardDecisionMismatchesAvx(
        ReadOnlySpan<double> llrs,
        ReadOnlySpan<byte> expectedPacked,
        int fullBytes)
    {
        var zero = Vector256<double>.Zero;
        ref var llrRef = ref MemoryMarshal.GetReference(llrs);
        var corrected = 0;

        for (var i = 0; i < fullBytes; i++)
        {
            var bitBase = i << 3;
            var cmp0 = Avx.CompareGreaterThanOrEqual(Vector256.LoadUnsafe(ref llrRef, (nuint)bitBase), zero);
            var cmp1 = Avx.CompareGreaterThanOrEqual(Vector256.LoadUnsafe(ref llrRef, (nuint)(bitBase + 4)), zero);
            var lsbPacked = (byte)(Avx.MoveMask(cmp0) | (Avx.MoveMask(cmp1) << 4));
            var hardPacked = ReverseBitsLut[lsbPacked];
            corrected += BitOperations.PopCount((uint)(hardPacked ^ expectedPacked[i]));
        }

        return corrected;
    }

    /// <summary>
    /// 完全バイト分の硬判定不一致を Arm64 AdvSIMD で数えます。
    /// </summary>
    /// <param name="llrs">受信ソフト LLR。正がビット 1。</param>
    /// <param name="expectedPacked">期待する符号ビット（MSB 先頭のパック）。</param>
    /// <param name="fullBytes">比較する完全バイト数。</param>
    /// <returns>不一致ビット数。</returns>
    private static int CountHardDecisionMismatchesAdvSimd(
        ReadOnlySpan<double> llrs,
        ReadOnlySpan<byte> expectedPacked,
        int fullBytes)
    {
        var zero = Vector128<double>.Zero;
        ref var llrRef = ref MemoryMarshal.GetReference(llrs);
        var corrected = 0;

        for (var i = 0; i < fullBytes; i++)
        {
            var bitBase = i << 3;
            var cmp0 = AdvSimd.Arm64.CompareGreaterThanOrEqual(Vector128.LoadUnsafe(ref llrRef, (nuint)bitBase), zero).AsUInt64();
            var cmp1 = AdvSimd.Arm64.CompareGreaterThanOrEqual(Vector128.LoadUnsafe(ref llrRef, (nuint)(bitBase + 2)), zero).AsUInt64();
            var cmp2 = AdvSimd.Arm64.CompareGreaterThanOrEqual(Vector128.LoadUnsafe(ref llrRef, (nuint)(bitBase + 4)), zero).AsUInt64();
            var cmp3 = AdvSimd.Arm64.CompareGreaterThanOrEqual(Vector128.LoadUnsafe(ref llrRef, (nuint)(bitBase + 6)), zero).AsUInt64();
            var lsbPacked = (byte)(
                ((((cmp0.GetElement(0) >> 63) & 1UL) << 0)
                | (((cmp0.GetElement(1) >> 63) & 1UL) << 1)
                | (((cmp1.GetElement(0) >> 63) & 1UL) << 2)
                | (((cmp1.GetElement(1) >> 63) & 1UL) << 3)
                | (((cmp2.GetElement(0) >> 63) & 1UL) << 4)
                | (((cmp2.GetElement(1) >> 63) & 1UL) << 5)
                | (((cmp3.GetElement(0) >> 63) & 1UL) << 6)
                | (((cmp3.GetElement(1) >> 63) & 1UL) << 7)));
            var hardPacked = ReverseBitsLut[lsbPacked];
            corrected += BitOperations.PopCount((uint)(hardPacked ^ expectedPacked[i]));
        }

        return corrected;
    }

    /// <summary>
    /// バイト値 0〜255 のビット順反転テーブルを構築します。
    /// </summary>
    /// <returns>添字のビット順を反転した 256 バイトの表。</returns>
    private static byte[] BuildReverseBitsLut()
    {
        var table = new byte[256];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = ReverseBits((byte)i);
        }

        return table;
    }

    /// <summary>
    /// バイト内のビット順を反転します。
    /// </summary>
    /// <param name="value">反転するバイト。</param>
    /// <returns>ビット順を反転したバイト。</returns>
    private static byte ReverseBits(byte value)
    {
        value = (byte)(((value & 0xAA) >> 1) | ((value & 0x55) << 1));
        value = (byte)(((value & 0xCC) >> 2) | ((value & 0x33) << 2));
        value = (byte)(((value & 0xF0) >> 4) | ((value & 0x0F) << 4));
        return value;
    }

    /// <summary>
    /// トレリス枝の出力ビットと受信 LLR から枝メトリック（対数尤度）を計算します。
    /// </summary>
    /// <param name="state">現在状態。</param>
    /// <param name="inputBit">入力ビット（0/1）。</param>
    /// <param name="llr0">出力ビット0の LLR。</param>
    /// <param name="llr1">出力ビット1の LLR。</param>
    /// <returns>枝の対数尤度。</returns>
    private static double BranchLogLikelihood(int state, int inputBit, double llr0, double llr1)
    {
        var branch0 = GetOutputBit(state, inputBit, GeneratorPolynomialsOctal[0]);
        var branch1 = GetOutputBit(state, inputBit, GeneratorPolynomialsOctal[1]);
        return ((branch0 == 1) ? llr0 : 0.0) + ((branch1 == 1) ? llr1 : 0.0);
    }

    /// <summary>
    /// パスメトリックが最小の状態インデックスを返します。
    /// </summary>
    /// <param name="metrics">状態ごとのパスメトリック。</param>
    /// <returns>最小メトリックの状態。</returns>
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
    /// 例外を投げずに復号を試行します。
    /// </summary>
    /// <param name="encoded">符号化済みバイト列。</param>
    /// <param name="originalByteLength">復号後の元バイト長。</param>
    /// <param name="decoded">成功時に復号したバイト列。</param>
    /// <param name="terminated">終端ビット付きとして扱う場合 true。</param>
    /// <param name="punctureRate">パンクチャ率。</param>
    /// <returns>復号成功時 true。</returns>
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
    /// 例外を投げずに復号を試行し、メトリクスも返します。
    /// </summary>
    /// <param name="encoded">符号化済みバイト列。</param>
    /// <param name="originalByteLength">復号後の元バイト長。</param>
    /// <param name="decoded">成功時に復号したバイト列。</param>
    /// <param name="metrics">復号時のメトリクス。</param>
    /// <param name="terminated">終端ビット付きとして扱う場合 true。</param>
    /// <param name="punctureRate">パンクチャ率。</param>
    /// <returns>復号成功時 true。</returns>
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
    /// 元ビット長から符号化後ビット長を見積もります。
    /// </summary>
    /// <param name="originalBitLength">元ビット長。</param>
    /// <param name="terminated">終端ビットを付加する場合 true。</param>
    /// <param name="punctureRate">パンクチャ率。</param>
    /// <returns>符号化後ビット長。</returns>
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
    /// 母符号ビット長とパンクチャ率からパンクチャ後ビット長を求めます。
    /// </summary>
    /// <param name="motherBitLength">母符号ビット長。</param>
    /// <param name="punctureRate">パンクチャ率。</param>
    /// <returns>パンクチャ後ビット長。</returns>
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
    /// 母符号ビット列をパンクチャして送信ビット列にします。
    /// </summary>
    /// <param name="motherBits">母符号ビット列。</param>
    /// <param name="punctureRate">パンクチャ率。</param>
    /// <returns>パンクチャ後ビット列。</returns>
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
    /// パンクチャ済みハードビットを母符号長へ戻し、存在マスクも返します。
    /// </summary>
    /// <param name="puncturedBits">パンクチャ後ビット。</param>
    /// <param name="motherBitLength">母符号ビット長。</param>
    /// <param name="punctureRate">パンクチャ率。</param>
    /// <param name="fullBits">母符号長のハードビット（欠落位置は false）。</param>
    /// <param name="presentMask">ビットが実際に受信された位置のマスク。</param>
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
    /// パック済みパンクチャビットを母符号長へ展開します（割当なし）。
    /// </summary>
    /// <param name="packedBits">MSB 先詰めのパックビット。</param>
    /// <param name="puncturedBitLength">パンクチャ後ビット長。</param>
    /// <param name="motherBitLength">母符号ビット長。</param>
    /// <param name="punctureRate">パンクチャ率。</param>
    /// <param name="fullBits">母符号長の出力バッファ。</param>
    /// <param name="presentMask">受信有無マスクの出力バッファ。</param>
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
    /// パンクチャ済みソフト LLR を母符号長へ戻します（欠落位置は 0）。
    /// </summary>
    /// <param name="puncturedLlrs">パンクチャ後 LLR。</param>
    /// <param name="motherBitLength">母符号ビット長。</param>
    /// <param name="punctureRate">パンクチャ率。</param>
    /// <returns>母符号長の LLR 配列。</returns>
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
    /// パンクチャ済みソフト LLR を既存バッファへ母符号長展開します。
    /// </summary>
    /// <param name="puncturedLlrs">パンクチャ後 LLR。</param>
    /// <param name="motherBitLength">母符号ビット長。</param>
    /// <param name="punctureRate">パンクチャ率。</param>
    /// <param name="full">母符号長の出力バッファ。</param>
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
    /// パンクチャ率に対応する繰り返しパターンを返します。
    /// </summary>
    /// <param name="punctureRate">パンクチャ率。</param>
    /// <returns>true=送信するビット位置のパターン。</returns>
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
    /// 1ビットを符号化して出力配列へ追記します。
    /// </summary>
    /// <param name="inputBit">入力ビット（0/1）。</param>
    /// <param name="state">現在状態（更新あり）。</param>
    /// <param name="encodedBits">符号化ビット出力先。</param>
    /// <param name="writeIndex">書き込み位置（更新あり）。</param>
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
    /// 真値要素数を数えます。
    /// </summary>
    /// <param name="values">判定対象配列。</param>
    /// <returns>true 要素数。</returns>
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
    /// 入力ビットと現在状態から次状態を計算します。
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
    /// 全状態について次状態テーブルを構築します。
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
    /// 全状態について生成多項式ごとの出力ビットテーブルを構築します。
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
    /// 全状態について2出力ビットを2bitマスク化したテーブルを構築します。
    /// bit0=generator0(bit0), bit1=generator1(bit1)
    /// </summary>
    /// <param name="inputBit">入力ビット（0/1）。</param>
    /// <returns>状態→出力2bitマスクのテーブル。</returns>
    private static byte[] BuildOutputMaskTable(int inputBit)
    {
        var table = new byte[StateCount];
        for (var state = 0; state < StateCount; state++)
        {
            var mask = 0;
            if (GetOutputBit(state, inputBit, GeneratorPolynomialsOctal[0]) == 1)
            {
                mask |= 1;
            }

            if (GetOutputBit(state, inputBit, GeneratorPolynomialsOctal[1]) == 1)
            {
                mask |= 2;
            }

            table[state] = (byte)mask;
        }

        return table;
    }

    /// <summary>
    /// 2bitマスクから枝メトリクスを引きます。
    /// </summary>
    /// <param name="outputMask">出力2bitマスク（0..3）。</param>
    /// <param name="metric01">出力01に対応する値。</param>
    /// <param name="metric10">出力10に対応する値。</param>
    /// <param name="metric11">出力11に対応する値。</param>
    /// <returns>枝メトリクス。</returns>
    private static double SelectBranchMetric(byte outputMask, double metric01, double metric10, double metric11)
    {
        return outputMask switch
        {
            0 => 0.0,
            1 => metric01,
            2 => metric10,
            _ => metric11,
        };
    }

    /// <summary>
    /// 状態・入力・生成多項式から畳み込み出力 1 ビットを計算します。
    /// </summary>
    /// <param name="state">現在状態。</param>
    /// <param name="inputBit">入力ビット（0/1）。</param>
    /// <param name="generator">生成多項式（オクタル表現の整数値）。</param>
    /// <returns>出力ビット（0/1）。</returns>
    private static int GetOutputBit(int state, int inputBit, int generator)
    {
        var register = (inputBit << MemoryBits) | state;
        var masked = register & generator;
        return Parity(masked);
    }

    /// <summary>
    /// 整数のビットパリティ（XOR 畳み込み）を返します。
    /// </summary>
    /// <param name="value">入力値。</param>
    /// <returns>パリティ（0 または 1）。</returns>
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
    /// 整数パスメトリックが最小の状態インデックスを返します。
    /// </summary>
    /// <param name="metrics">状態ごとのパスメトリック。</param>
    /// <returns>最小メトリックの状態。</returns>
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
    /// バイト列を MSB 先のビット列へ展開します。
    /// </summary>
    /// <param name="bytes">入力バイト列。</param>
    /// <returns>ビット列（長さ = bytes×8）。</returns>
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
    /// バイト値→8 ビット展開のルックアップテーブルを構築します。
    /// </summary>
    /// <returns>256×8 のビット展開テーブル。</returns>
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
    /// ビット列を MSB 先のバイト列へ詰めます。
    /// </summary>
    /// <param name="bits">入力ビット列（長さは 8 の倍数）。</param>
    /// <returns>パックしたバイト列。</returns>
    private static byte[] BitsToBytes(bool[] bits)
    {
        return BitsToBytes(bits.AsSpan());
    }

    /// <summary>
    /// ビット列を MSB 先のバイト列へ詰めます。
    /// </summary>
    /// <param name="bits">入力ビット列（長さは 8 の倍数）。</param>
    /// <returns>パックしたバイト列。</returns>
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
    /// パック済みバイト列から指定ビットを読み取ります（MSB 先）。
    /// </summary>
    /// <param name="packed">パック済みバイト列。</param>
    /// <param name="bitIndex">ビット位置（0 起点）。</param>
    /// <returns>ビット値。</returns>
    private static bool ReadPackedBit(ReadOnlySpan<byte> packed, int bitIndex)
    {
        var b = packed[bitIndex >> 3];
        return ((b >> (7 - (bitIndex & 7))) & 1) != 0;
    }

    /// <summary>
    /// ビット列を MSB 先でバイト列へパックします（端数ビットあり可）。
    /// </summary>
    /// <param name="bits">入力ビット列。</param>
    /// <returns>パックしたバイト列。</returns>
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
    /// <returns>展開したビット列。</returns>
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


