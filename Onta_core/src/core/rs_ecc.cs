using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Onta.Core;

/// <summary>
/// 固定長 GF(256) ブロック向けの Reed-Solomon 誤り訂正 API です。
/// </summary>
/// <remarks>
/// 128 バイトのペイロードに 32 シンボルのパリティを付加して処理します。
/// </remarks>
public static class RsEcc256
{
    /// <summary>
    /// 復号時の補正統計を表します。
    /// </summary>
    public readonly record struct DecodeMetrics(
        int PayloadCorrectedBitCount,
        int PayloadCorrectedByteCount,
        int CodewordCorrectedSymbolCount,
        int CodewordCorrectedBitCount,
        int PayloadBitLength,
        double PayloadCorrectionRate);

    /// <summary>
    /// <see cref="Encode(byte[])"/> が受け付けるペイロード長（バイト）です。
    /// </summary>
    public const int DataUnitSize = 128;

    /// <summary>
    /// 各ブロックに付加するパリティシンボル数です。
    /// </summary>
    public const int ParitySymbols = 32;

    /// <summary>
    /// 1 コードブロックあたりのペイロード長（バイト）です。
    /// </summary>
    public const int PayloadBytesPerBlock = 128;

    /// <summary>
    /// 1 ブロックあたりの符号語長（バイト）です。
    /// </summary>
    public const int CodewordBytesPerBlock = PayloadBytesPerBlock + ParitySymbols;

    /// <summary>
    /// <see cref="Decode(byte[])"/> が受け付ける符号化データ長（バイト）です。
    /// </summary>
    public const int EncodedUnitSize = CodewordBytesPerBlock;

    /// <summary>
    /// ペイロード 1 ブロックを符号化し、パリティシンボルを付加します。
    /// </summary>
    /// <param name="data128">ペイロード。長さは <see cref="DataUnitSize"/> である必要があります。</param>
    /// <returns>符号化後の符号語バイト列。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data128"/> が null の場合にスローされます。</exception>
    /// <exception cref="ArgumentException">入力長が不正な場合にスローされます。</exception>
    public static byte[] Encode(byte[] data128)
    {
        ArgumentNullException.ThrowIfNull(data128);
        if (data128.Length != DataUnitSize)
        {
            throw new ArgumentException($"Input must be exactly {DataUnitSize} bytes.", nameof(data128));
        }

        return ReedSolomonCodec.EncodeBlock(data128, ParitySymbols);
    }

    /// <summary>
    /// 符号語 1 ブロックを復号し、訂正可能な誤りを補正します。
    /// </summary>
    /// <param name="encoded160">符号語。長さは <see cref="EncodedUnitSize"/> である必要があります。</param>
    /// <returns>復号後のペイロードバイト列。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoded160"/> が null の場合にスローされます。</exception>
    /// <exception cref="ArgumentException">入力長が不正な場合にスローされます。</exception>
    /// <exception cref="InvalidOperationException">誤り位置の特定または訂正に失敗した場合にスローされます。</exception>
    public static byte[] Decode(byte[] encoded160)
    {
        return Decode(encoded160, out _);
    }

    /// <summary>
    /// 符号語 1 ブロックを復号し、補正統計とともに返します。
    /// </summary>
    /// <param name="encoded160">符号語。長さは <see cref="EncodedUnitSize"/> である必要があります。</param>
    /// <param name="metrics">補正統計。<see cref="DecodeMetrics.PayloadCorrectionRate"/> は 1024 ビットに対する補正率です。</param>
    /// <returns>復号後のペイロードバイト列。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoded160"/> が null の場合にスローされます。</exception>
    /// <exception cref="ArgumentException">入力長が不正な場合にスローされます。</exception>
    /// <exception cref="InvalidOperationException">誤り位置の特定または訂正に失敗した場合にスローされます。</exception>
    public static byte[] Decode(byte[] encoded160, out DecodeMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(encoded160);
        if (encoded160.Length != EncodedUnitSize)
        {
            throw new ArgumentException($"Input must be exactly {EncodedUnitSize} bytes.", nameof(encoded160));
        }

        var result = ReedSolomonCodec.DecodeBlockWithMetrics(encoded160, ParitySymbols, PayloadBytesPerBlock);
        metrics = result.Metrics;
        return result.Decoded;
    }

    /// <summary>
    /// 符号語 1 ブロックの復号と誤り訂正を試みます。
    /// </summary>
    /// <param name="encoded160">符号語。長さは <see cref="EncodedUnitSize"/> を想定します。</param>
    /// <param name="decoded128">成功時は復号ペイロード、失敗時は空配列。</param>
    /// <returns>復号成功時は <see langword="true"/>、それ以外は <see langword="false"/>。</returns>
    public static bool TryDecode(byte[] encoded160, out byte[] decoded128)
    {
        return TryDecode(encoded160, out decoded128, out _);
    }

    /// <summary>
    /// 符号語 1 ブロックの復号と誤り訂正を試み、補正統計も返します。
    /// </summary>
    /// <param name="encoded160">符号語。長さは <see cref="EncodedUnitSize"/> を想定します。</param>
    /// <param name="decoded128">成功時は復号ペイロード、失敗時は空配列。</param>
    /// <param name="metrics">成功時は補正統計、失敗時は既定値。</param>
    /// <returns>復号成功時は <see langword="true"/>、それ以外は <see langword="false"/>。</returns>
    public static bool TryDecode(byte[] encoded160, out byte[] decoded128, out DecodeMetrics metrics)
    {
        decoded128 = Array.Empty<byte>();
        metrics = default;
        try
        {
            decoded128 = Decode(encoded160, out metrics);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal static class ReedSolomonCodec
{
    internal readonly record struct DecodeResult(byte[] Decoded, RsEcc256.DecodeMetrics Metrics);

    private const int FieldSize = 256;
    private const int PrimitivePolynomial = 0x11D;

    private static readonly int[] ExpTable = new int[FieldSize * 2];
    private static readonly int[] LogTable = new int[FieldSize];
    private static readonly byte[] MulTable = new byte[FieldSize * FieldSize];

    static ReedSolomonCodec()
    {
        InitializeTables();
        InitializeMultiplicationTable();
    }

    public static byte[] EncodeBlock(byte[] data, int paritySymbols)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (paritySymbols <= 0 || paritySymbols >= FieldSize)
        {
            throw new ArgumentException("Parity symbols must be in range 1..255.", nameof(paritySymbols));
        }

        if (data.Length + paritySymbols > 255)
        {
            throw new ArgumentException("Block length + parity must be <= 255 for GF(256) RS.", nameof(data));
        }

        // バッファ先頭にメッセージを置き、多項式除算の余りを末尾パリティとして生成する。
        var generator = BuildGeneratorPolynomial(paritySymbols);
        var buffer = new int[data.Length + paritySymbols];
        for (var i = 0; i < data.Length; i++)
        {
            buffer[i] = data[i];
        }

        for (var i = 0; i < data.Length; i++)
        {
            var coef = buffer[i];
            if (coef == 0)
            {
                continue;
            }

            for (var j = 0; j < generator.Length; j++)
            {
                buffer[i + j] ^= GfMultiply(generator[j], coef);
            }
        }

        var codeword = new byte[data.Length + paritySymbols];
        Array.Copy(data, codeword, data.Length);
        for (var i = 0; i < paritySymbols; i++)
        {
            codeword[data.Length + i] = (byte)buffer[data.Length + i];
        }

        return codeword;
    }

    public static byte[] DecodeBlock(byte[] received, int paritySymbols)
    {
        return DecodeBlockWithMetrics(received, paritySymbols, received.Length - paritySymbols).Decoded;
    }

    public static DecodeResult DecodeBlockWithMetrics(byte[] received, int paritySymbols, int payloadBytes)
    {
        ArgumentNullException.ThrowIfNull(received);
        if (received.Length <= paritySymbols)
        {
            throw new ArgumentException("Received block is too short.", nameof(received));
        }

        if (payloadBytes <= 0 || payloadBytes > received.Length - paritySymbols)
        {
            throw new ArgumentException("Payload byte length is out of range.", nameof(payloadBytes));
        }

        // シンドロームが全ゼロでなければ誤りあり、全ゼロなら訂正不要。
        var syndromes = CalculateSyndromes(received, paritySymbols);
        if (IsAllZero(syndromes))
        {
            var noParity = new byte[received.Length - paritySymbols];
            Array.Copy(received, 0, noParity, 0, noParity.Length);
            var noCorrectionMetrics = new RsEcc256.DecodeMetrics(
                PayloadCorrectedBitCount: 0,
                PayloadCorrectedByteCount: 0,
                CodewordCorrectedSymbolCount: 0,
                CodewordCorrectedBitCount: 0,
                PayloadBitLength: payloadBytes * 8,
                PayloadCorrectionRate: 0.0);
            return new DecodeResult(noParity, noCorrectionMetrics);
        }

        // Berlekamp-Massey でシンドローム列から誤り位置多項式を求める。
        var errorLocator = FindErrorLocatorBerlekampMassey(syndromes, paritySymbols);
        var errorPositions = FindErrorPositions(errorLocator, received.Length);
        if (errorPositions.Count == 0)
        {
            throw new InvalidOperationException("Failed to locate errors.");
        }

        var corrected = (byte[])received.Clone();
        // 誤り値を解いて、特定したシンボル位置へ補正を適用する。
        var magnitudes = SolveErrorMagnitudes(syndromes, errorPositions, corrected.Length);
        for (var i = 0; i < errorPositions.Count; i++)
        {
            corrected[errorPositions[i]] ^= (byte)magnitudes[i];
        }

        var check = CalculateSyndromes(corrected, paritySymbols);
        if (!IsAllZero(check))
        {
            throw new InvalidOperationException("Too many errors to correct.");
        }

        var payloadCorrectedBits = CountDifferentBits(received, corrected, payloadBytes);
        var payloadCorrectedBytes = CountDifferentBytes(received, corrected, payloadBytes);
        var codewordCorrectedBits = CountDifferentBits(received, corrected, corrected.Length);
        var codewordCorrectedSymbols = CountDifferentBytes(received, corrected, corrected.Length);

        var decoded = new byte[corrected.Length - paritySymbols];
        Array.Copy(corrected, 0, decoded, 0, decoded.Length);
        var metrics = new RsEcc256.DecodeMetrics(
            PayloadCorrectedBitCount: payloadCorrectedBits,
            PayloadCorrectedByteCount: payloadCorrectedBytes,
            CodewordCorrectedSymbolCount: codewordCorrectedSymbols,
            CodewordCorrectedBitCount: codewordCorrectedBits,
            PayloadBitLength: payloadBytes * 8,
            PayloadCorrectionRate: payloadBytes == 0 ? 0.0 : (double)payloadCorrectedBits / (payloadBytes * 8));

        return new DecodeResult(decoded, metrics);
    }

    private static int CountDifferentBytes(byte[] left, byte[] right, int count)
    {
        var different = 0;
        var i = 0;
        ref var leftRef = ref MemoryMarshal.GetReference(left.AsSpan());
        ref var rightRef = ref MemoryMarshal.GetReference(right.AsSpan());

        if (Avx2.IsSupported)
        {
            const int width = 32;
            for (; i <= count - width; i += width)
            {
                var a = Vector256.LoadUnsafe(ref leftRef, (nuint)i);
                var b = Vector256.LoadUnsafe(ref rightRef, (nuint)i);

                var eq = Avx2.CompareEqual(a, b);
                var equalMask = (uint)Avx2.MoveMask(eq);
                different += width - BitOperations.PopCount(equalMask);
            }
        }
        else if (AdvSimd.IsSupported)
        {
            const int width = 16;
            Span<byte> equalLanes = stackalloc byte[width];
            for (; i <= count - width; i += width)
            {
                var a = Vector128.LoadUnsafe(ref leftRef, (nuint)i);
                var b = Vector128.LoadUnsafe(ref rightRef, (nuint)i);
                var eq = AdvSimd.CompareEqual(a, b);
                // 0xFF/0x00 を 1/0 に正規化して lane 合計で一致数を得る。
                var normalizedEq = AdvSimd.ShiftRightLogical(eq, 7);
                normalizedEq.CopyTo(equalLanes);

                var equalCount = 0;
                for (var lane = 0; lane < width; lane++)
                {
                    equalCount += equalLanes[lane];
                }

                different += width - equalCount;
            }
        }

        for (; i <= count - 8; i += 8)
        {
            ulong x = Unsafe.ReadUnaligned<ulong>(
                ref MemoryMarshal.GetReference(left.AsSpan(i, 8)));
            ulong y = Unsafe.ReadUnaligned<ulong>(
                ref MemoryMarshal.GetReference(right.AsSpan(i, 8)));

            // 各バイトの一致判定をビット並列化し、不一致バイト数を一括加算する。
            ulong neq = x ^ y;
            ulong flags = ((neq | (0UL - neq)) >> 7) & 0x0101010101010101UL;
            different += BitOperations.PopCount(flags);
        }

        for (; i < count; i++)
        {
            if (left[i] != right[i])
            {
                different++;
            }
        }

        return different;
    }

    private static int CountDifferentBits(byte[] left, byte[] right, int count)
    {
        var bitCount = 0;
        var i = 0;

        for (; i <= count - 8; i += 8)
        {
            ulong x = Unsafe.ReadUnaligned<ulong>(
                ref MemoryMarshal.GetReference(left.AsSpan(i, 8)));
            ulong y = Unsafe.ReadUnaligned<ulong>(
                ref MemoryMarshal.GetReference(right.AsSpan(i, 8)));
            bitCount += BitOperations.PopCount(x ^ y);
        }

        for (; i < count; i++)
        {
            bitCount += BitOperations.PopCount((uint)(left[i] ^ right[i]));
        }

        return bitCount;
    }

    private static void InitializeTables()
    {
        // 乗除算を高速化するため、GF(256) の log/antilog テーブルを構築する。
        var x = 1;
        for (var i = 0; i < FieldSize - 1; i++)
        {
            ExpTable[i] = x;
            LogTable[x] = i;
            x <<= 1;
            if (x >= FieldSize)
            {
                x ^= PrimitivePolynomial;
            }
        }

        for (var i = FieldSize - 1; i < ExpTable.Length; i++)
        {
            ExpTable[i] = ExpTable[i - (FieldSize - 1)];
        }
    }

    private static void InitializeMultiplicationTable()
    {
        for (var a = 0; a < FieldSize; a++)
        {
            for (var b = 0; b < FieldSize; b++)
            {
                if (a == 0 || b == 0)
                {
                    MulTable[(a << 8) | b] = 0;
                    continue;
                }

                MulTable[(a << 8) | b] = (byte)ExpTable[LogTable[a] + LogTable[b]];
            }
        }
    }

    private static int[] BuildGeneratorPolynomial(int paritySymbols)
    {
        // 生成多項式 g(x) = Π_{i=0..parity-1}(x - a^i) を構築する。
        var gen = new[] { 1 };
        for (var i = 0; i < paritySymbols; i++)
        {
            gen = MultiplyPolynomialsHighDegree(gen, new[] { 1, GfPowAlpha(i) });
        }

        return gen;
    }

    private static int[] CalculateSyndromes(byte[] data, int paritySymbols)
    {
        var syndromes = new int[paritySymbols];
        for (var i = 0; i < paritySymbols; i++)
        {
            syndromes[i] = EvaluatePolynomialHighDegree(data, GfPowAlpha(i));
        }

        return syndromes;
    }

    private static bool IsAllZero(int[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int[] FindErrorLocatorBerlekampMassey(int[] syndromes, int paritySymbols)
    {
        // 低次係数先頭の表現で Berlekamp-Massey の漸化式を適用する。
        var c = new List<int> { 1 };
        var b = new List<int> { 1 };

        var l = 0;
        var m = 1;
        var bDiscrepancy = 1;

        for (var n = 0; n < paritySymbols; n++)
        {
            var discrepancy = syndromes[n];
            for (var i = 1; i <= l; i++)
            {
                discrepancy ^= GfMultiply(c[i], syndromes[n - i]);
            }

            if (discrepancy == 0)
            {
                m++;
                continue;
            }

            var t = new List<int>(c);
            var scale = GfDivide(discrepancy, bDiscrepancy);
            var shiftedB = ShiftPolynomialLowDegree(b, m);
            var scaledShiftedB = ScalePolynomialLowDegree(shiftedB, scale);
            c = AddPolynomialsLowDegree(c, scaledShiftedB);

            if (2 * l <= n)
            {
                l = n + 1 - l;
                b = t;
                bDiscrepancy = discrepancy;
                m = 1;
            }
            else
            {
                m++;
            }
        }

        TrimTrailingZerosLowDegree(c);
        return c.ToArray();
    }

    private static List<int> FindErrorPositions(int[] errorLocatorLowDegree, int messageLength)
    {
        var degree = errorLocatorLowDegree.Length - 1;
        if (degree <= 0)
        {
            return new List<int>();
        }

        // Chien 探索で誤り位置多項式の根を探索し、誤りシンボル位置へ対応付ける。
        var positions = new List<int>(degree);
        for (var i = 0; i < messageLength; i++)
        {
            var x = GfPowAlpha(255 - i);
            if (EvaluatePolynomialLowDegree(errorLocatorLowDegree, x) == 0)
            {
                positions.Add(messageLength - 1 - i);
            }
        }

        if (positions.Count != degree)
        {
            throw new InvalidOperationException("Error location count mismatch.");
        }

        return positions;
    }

    private static int[] SolveErrorMagnitudes(int[] syndromes, List<int> errorPositions, int messageLength)
    {
        var count = errorPositions.Count;
        if (count == 0)
        {
            return Array.Empty<int>();
        }

        var basePowers = new int[count];
        var rowPowers = new int[count];
        for (var col = 0; col < count; col++)
        {
            var locatorExponent = messageLength - 1 - errorPositions[col];
            basePowers[col] = GfPowAlpha(locatorExponent);
            rowPowers[col] = 1; // row=0 のとき alpha^(locatorExponent*0)=1
        }

        var matrix = new int[count, count + 1];
        for (var row = 0; row < count; row++)
        {
            matrix[row, count] = syndromes[row];
            for (var col = 0; col < count; col++)
            {
                matrix[row, col] = rowPowers[col];
                rowPowers[col] = GfMultiply(rowPowers[col], basePowers[col]);
            }
        }

        return SolveLinearSystemGf256(matrix, count);
    }

    private static int[] SolveLinearSystemGf256(int[,] augmentedMatrix, int size)
    {
        // 誤り値算出のため、GF(256) 上でガウス消去を行う。
        var row = 0;
        for (var col = 0; col < size && row < size; col++)
        {
            var pivot = row;
            while (pivot < size && augmentedMatrix[pivot, col] == 0)
            {
                pivot++;
            }

            if (pivot == size)
            {
                continue;
            }

            if (pivot != row)
            {
                SwapRows(augmentedMatrix, pivot, row, size + 1);
            }

            var pivotValue = augmentedMatrix[row, col];
            var pivotInverse = GfInverse(pivotValue);
            for (var j = col; j <= size; j++)
            {
                augmentedMatrix[row, j] = GfMultiply(augmentedMatrix[row, j], pivotInverse);
            }

            for (var i = 0; i < size; i++)
            {
                if (i == row)
                {
                    continue;
                }

                var factor = augmentedMatrix[i, col];
                if (factor == 0)
                {
                    continue;
                }

                for (var j = col; j <= size; j++)
                {
                    augmentedMatrix[i, j] ^= GfMultiply(factor, augmentedMatrix[row, j]);
                }
            }

            row++;
        }

        if (row < size)
        {
            throw new InvalidOperationException("Singular matrix while solving error magnitudes.");
        }

        var solution = new int[size];
        for (var i = 0; i < size; i++)
        {
            solution[i] = augmentedMatrix[i, size];
        }

        return solution;
    }

    private static void SwapRows(int[,] matrix, int rowA, int rowB, int width)
    {
        for (var j = 0; j < width; j++)
        {
            (matrix[rowA, j], matrix[rowB, j]) = (matrix[rowB, j], matrix[rowA, j]);
        }
    }

    private static List<int> AddPolynomialsLowDegree(List<int> left, List<int> right)
    {
        var size = Math.Max(left.Count, right.Count);
        var result = new List<int>(new int[size]);

        for (var i = 0; i < size; i++)
        {
            var l = i < left.Count ? left[i] : 0;
            var r = i < right.Count ? right[i] : 0;
            result[i] = l ^ r;
        }

        TrimTrailingZerosLowDegree(result);
        return result;
    }

    private static List<int> ScalePolynomialLowDegree(List<int> poly, int scale)
    {
        var result = new List<int>(poly.Count);
        for (var i = 0; i < poly.Count; i++)
        {
            result.Add(GfMultiply(poly[i], scale));
        }

        TrimTrailingZerosLowDegree(result);
        return result;
    }

    private static List<int> ShiftPolynomialLowDegree(List<int> poly, int shift)
    {
        var result = new List<int>(new int[poly.Count + shift]);
        for (var i = 0; i < poly.Count; i++)
        {
            result[i + shift] = poly[i];
        }

        return result;
    }

    private static void TrimTrailingZerosLowDegree(List<int> poly)
    {
        while (poly.Count > 1 && poly[poly.Count - 1] == 0)
        {
            poly.RemoveAt(poly.Count - 1);
        }
    }

    private static int EvaluatePolynomialLowDegree(int[] polynomial, int x)
    {
        var result = 0;
        for (var i = polynomial.Length - 1; i >= 0; i--)
        {
            result = GfMultiply(result, x) ^ polynomial[i];
        }

        return result;
    }

    private static int EvaluatePolynomialHighDegree(byte[] polynomial, int x)
    {
        var result = 0;
        for (var i = 0; i < polynomial.Length; i++)
        {
            result = GfMultiply(result, x) ^ polynomial[i];
        }

        return result;
    }

    private static int[] MultiplyPolynomialsHighDegree(int[] left, int[] right)
    {
        var result = new int[left.Length + right.Length - 1];
        for (var i = 0; i < left.Length; i++)
        {
            for (var j = 0; j < right.Length; j++)
            {
                result[i + j] ^= GfMultiply(left[i], right[j]);
            }
        }

        return result;
    }

    private static int GfMultiply(int a, int b)
    {
        return MulTable[(a << 8) | b];
    }

    private static int GfPowAlpha(int power)
    {
        var exponent = power % 255;
        if (exponent < 0)
        {
            exponent += 255;
        }

        return ExpTable[exponent];
    }

    private static int GfDivide(int a, int b)
    {
        if (b == 0)
        {
            throw new DivideByZeroException("GF(256) division by zero.");
        }

        if (a == 0)
        {
            return 0;
        }

        var exponent = LogTable[a] - LogTable[b];
        if (exponent < 0)
        {
            exponent += 255;
        }

        return ExpTable[exponent];
    }

    private static int GfInverse(int value)
    {
        if (value == 0)
        {
            throw new DivideByZeroException("GF(256) inverse of zero is undefined.");
        }

        return ExpTable[255 - LogTable[value]];
    }

    private static int GfPower(int value, int power)
    {
        if (power == 0)
        {
            return 1;
        }

        if (value == 0)
        {
            return 0;
        }

        // log(value^power) = power * log(value) (mod 255) を利用する。
        var logValue = LogTable[value];
        var exponent = (logValue * power) % 255;
        if (exponent < 0)
        {
            exponent += 255;
        }

        return ExpTable[exponent];
    }
}
