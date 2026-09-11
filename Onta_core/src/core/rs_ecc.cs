using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Onta.Core;

/// <summary>
/// GF(256) 上の Reed-Solomon 符号化/復号 API を提供します。
/// </summary>
/// <remarks>
/// 128 バイトのペイロードに 32 シンボルのパリティを付与します。
/// </remarks>
public static class RsEcc256
{
    /// <summary>
    /// 復号時の訂正量メトリクスです。
    /// </summary>
    public readonly record struct DecodeMetrics(
        int PayloadCorrectedBitCount,
        int PayloadCorrectedByteCount,
        int CodewordCorrectedSymbolCount,
        int CodewordCorrectedBitCount,
        int PayloadBitLength,
        double PayloadCorrectionRate);

    /// <summary>
    /// ペイロード長（バイト）です。
    /// </summary>
    public const int DataUnitSize = 128;

    /// <summary>
    /// パリティシンボル数です。
    /// </summary>
    public const int ParitySymbols = 32;

    /// <summary>
    /// 1ブロックあたりのペイロードバイト数です。
    /// </summary>
    public const int PayloadBytesPerBlock = 128;

    /// <summary>
    /// 1ブロックあたりの符号語バイト数です。
    /// </summary>
    public const int CodewordBytesPerBlock = PayloadBytesPerBlock + ParitySymbols;

    /// <summary>
    /// エンコード後の固定ブロック長です。
    /// </summary>
    public const int EncodedUnitSize = CodewordBytesPerBlock;

    /// <summary>
    /// 128バイト入力を RS 符号語へエンコードします。
    /// </summary>
    /// <param name="data128">128バイトの入力ペイロード。</param>
    /// <returns>パリティを含む符号語バイト列。</returns>
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
    /// 160バイト符号語を復号します。
    /// </summary>
    /// <param name="encoded160">160バイトの受信符号語。</param>
    /// <returns>復号したペイロード。</returns>
    public static byte[] Decode(byte[] encoded160)
    {
        return Decode(encoded160, out _);
    }

    /// <summary>
    /// 復号結果と訂正メトリクスを返します。
    /// </summary>
    /// <param name="encoded160">160バイトの受信符号語。</param>
    /// <param name="metrics">訂正量メトリクス。</param>
    /// <returns>復号したペイロード。</returns>
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
    /// 例外を投げずに復号を試行します。
    /// </summary>
    /// <param name="encoded160">160バイトの受信符号語。</param>
    /// <param name="decoded128">成功時に復号したペイロード。</param>
    /// <returns>復号成功時 true。</returns>
    public static bool TryDecode(byte[] encoded160, out byte[] decoded128)
    {
        return TryDecode(encoded160, out decoded128, out _);
    }

    /// <summary>
    /// 例外を投げずに復号を試行し、メトリクスも返します。
    /// </summary>
    /// <param name="encoded160">160バイトの受信符号語。</param>
    /// <param name="decoded128">成功時に復号したペイロード。</param>
    /// <param name="metrics">復号時の訂正量メトリクス。</param>
    /// <returns>復号成功時 true。</returns>
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

/// <summary>
/// RS の内部演算（有限体演算と復号手順）を実装します。
/// </summary>
internal static class ReedSolomonCodec
{
    /// <summary>
    /// 復号バイト列とメトリクスの組です。
    /// </summary>
    internal readonly record struct DecodeResult(byte[] Decoded, RsEcc256.DecodeMetrics Metrics);

    private const int FieldSize = 256;
    private const int PrimitivePolynomial = 0x11D;

    private static readonly int[] ExpTable = new int[FieldSize * 2];
    private static readonly int[] LogTable = new int[FieldSize];
    private static readonly byte[] MulTable = new byte[FieldSize * FieldSize];

    /// <summary>
    /// 有限体テーブルを初期化します。
    /// </summary>
    static ReedSolomonCodec()
    {
        InitializeTables();
        InitializeMultiplicationTable();
    }

    /// <summary>
    /// 任意長ブロックへ RS パリティを付与します。
    /// </summary>
    /// <param name="data">入力ペイロード。</param>
    /// <param name="paritySymbols">付与するパリティシンボル数。</param>
    /// <returns>符号語バイト列。</returns>
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

    /// <summary>
    /// 受信符号語を復号し、ペイロードを返します。
    /// </summary>
    /// <param name="received">受信した符号語バイト列。</param>
    /// <param name="paritySymbols">パリティシンボル数。</param>
    /// <returns>復号したペイロード。</returns>
    public static byte[] DecodeBlock(byte[] received, int paritySymbols)
    {
        return DecodeBlockWithMetrics(received, paritySymbols, received.Length - paritySymbols).Decoded;
    }

    /// <summary>
    /// 受信符号語を復号し、訂正メトリクス付きで返します。
    /// </summary>
    /// <param name="received">受信した符号語バイト列。</param>
    /// <param name="paritySymbols">パリティシンボル数。</param>
    /// <param name="payloadBytes">復号対象ペイロード長。</param>
    /// <returns>復号結果と訂正メトリクス。</returns>
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

        var errorLocator = FindErrorLocatorBerlekampMassey(syndromes, paritySymbols);
        var errorPositions = FindErrorPositions(errorLocator, received.Length);
        if (errorPositions.Count == 0)
        {
            throw new InvalidOperationException("Failed to locate errors.");
        }

        var corrected = (byte[])received.Clone();
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

    /// <summary>
    /// CountDifferentBytes を実行します。
    /// </summary>
    /// <param name="left">left を指定します。</param>
    /// <param name="right">right を指定します。</param>
    /// <param name="count">count を指定します。</param>
    /// <returns>処理結果。</returns>
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

    /// <summary>
    /// CountDifferentBits を実行します。
    /// </summary>
    /// <param name="left">left を指定します。</param>
    /// <param name="right">right を指定します。</param>
    /// <param name="count">count を指定します。</param>
    /// <returns>処理結果。</returns>
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

    /// <summary>
    /// InitializeTables を実行します。
    /// </summary>
    private static void InitializeTables()
    {
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

    /// <summary>
    /// InitializeMultiplicationTable を実行します。
    /// </summary>
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

    /// <summary>
    /// BuildGeneratorPolynomial を構築します。
    /// </summary>
    /// <param name="paritySymbols">paritySymbols を指定します。</param>
    /// <returns>処理結果。</returns>
    private static int[] BuildGeneratorPolynomial(int paritySymbols)
    {
        var gen = new[] { 1 };
        for (var i = 0; i < paritySymbols; i++)
        {
            gen = MultiplyPolynomialsHighDegree(gen, new[] { 1, GfPowAlpha(i) });
        }

        return gen;
    }

    /// <summary>
    /// 受信ブロックのシンドロームを計算します。
    /// </summary>
    /// <param name="data">受信符号語。</param>
    /// <param name="paritySymbols">パリティシンボル数。</param>
    /// <returns>シンドローム配列。</returns>
    private static int[] CalculateSyndromes(byte[] data, int paritySymbols)
    {
        var syndromes = new int[paritySymbols];
        for (var i = 0; i < paritySymbols; i++)
        {
            syndromes[i] = EvaluatePolynomialHighDegree(data, GfPowAlpha(i));
        }

        return syndromes;
    }

    /// <summary>
    /// IsAllZero を判定します。
    /// </summary>
    /// <param name="values">values を指定します。</param>
    /// <returns>条件を満たす場合 true、それ以外は false。</returns>
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

    /// <summary>
    /// FindErrorLocatorBerlekampMassey を実行します。
    /// </summary>
    /// <param name="syndromes">syndromes を指定します。</param>
    /// <param name="paritySymbols">paritySymbols を指定します。</param>
    /// <returns>処理結果。</returns>
    private static int[] FindErrorLocatorBerlekampMassey(int[] syndromes, int paritySymbols)
    {
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

    /// <summary>
    /// 誤り位置を探索します。
    /// </summary>
    /// <param name="errorLocatorLowDegree">低次係数順の誤り位置多項式。</param>
    /// <param name="messageLength">符号語長。</param>
    /// <returns>誤り位置一覧。</returns>
    private static List<int> FindErrorPositions(int[] errorLocatorLowDegree, int messageLength)
    {
        var degree = errorLocatorLowDegree.Length - 1;
        if (degree <= 0)
        {
            return new List<int>();
        }

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

    /// <summary>
    /// 誤り値（マグニチュード）を連立方程式で解きます。
    /// </summary>
    /// <param name="syndromes">シンドローム配列。</param>
    /// <param name="errorPositions">誤り位置一覧。</param>
    /// <param name="messageLength">符号語長。</param>
    /// <returns>誤り値配列。</returns>
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
            rowPowers[col] = 1; // row=0 では alpha^(locatorExponent*0)=1
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

    /// <summary>
    /// SolveLinearSystemGf256 を実行します。
    /// </summary>
    /// <param name="augmentedMatrix">augmentedMatrix を指定します。</param>
    /// <param name="size">size を指定します。</param>
    /// <returns>処理結果。</returns>
    private static int[] SolveLinearSystemGf256(int[,] augmentedMatrix, int size)
    {
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

    /// <summary>
    /// 行列の2行を交換します。
    /// </summary>
    /// <param name="matrix">対象拡大行列。</param>
    /// <param name="rowA">交換元行。</param>
    /// <param name="rowB">交換先行。</param>
    /// <param name="width">列数。</param>
    private static void SwapRows(int[,] matrix, int rowA, int rowB, int width)
    {
        for (var j = 0; j < width; j++)
        {
            (matrix[rowA, j], matrix[rowB, j]) = (matrix[rowB, j], matrix[rowA, j]);
        }
    }

    /// <summary>
    /// 低次係数順多項式をGF(256)上で加算します。
    /// </summary>
    /// <param name="left">左多項式。</param>
    /// <param name="right">右多項式。</param>
    /// <returns>加算結果多項式。</returns>
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

    /// <summary>
    /// 多項式をGF(256)スカラーで乗算します。
    /// </summary>
    /// <param name="poly">対象多項式。</param>
    /// <param name="scale">乗算係数。</param>
    /// <returns>乗算結果多項式。</returns>
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

    /// <summary>
    /// 低次係数順多項式をシフトします。
    /// </summary>
    /// <param name="poly">対象多項式。</param>
    /// <param name="shift">シフト量。</param>
    /// <returns>シフト後多項式。</returns>
    private static List<int> ShiftPolynomialLowDegree(List<int> poly, int shift)
    {
        var result = new List<int>(new int[poly.Count + shift]);
        for (var i = 0; i < poly.Count; i++)
        {
            result[i + shift] = poly[i];
        }

        return result;
    }

    /// <summary>
    /// TrimTrailingZerosLowDegree を実行します。
    /// </summary>
    /// <param name="poly">poly を指定します。</param>
    private static void TrimTrailingZerosLowDegree(List<int> poly)
    {
        while (poly.Count > 1 && poly[poly.Count - 1] == 0)
        {
            poly.RemoveAt(poly.Count - 1);
        }
    }

    /// <summary>
    /// 低次係数順多項式を評価します。
    /// </summary>
    /// <param name="polynomial">評価対象多項式。</param>
    /// <param name="x">評価点。</param>
    /// <returns>評価値。</returns>
    private static int EvaluatePolynomialLowDegree(int[] polynomial, int x)
    {
        var result = 0;
        for (var i = polynomial.Length - 1; i >= 0; i--)
        {
            result = GfMultiply(result, x) ^ polynomial[i];
        }

        return result;
    }

    /// <summary>
    /// 高次係数順多項式を評価します。
    /// </summary>
    /// <param name="polynomial">評価対象多項式。</param>
    /// <param name="x">評価点。</param>
    /// <returns>評価値。</returns>
    private static int EvaluatePolynomialHighDegree(byte[] polynomial, int x)
    {
        var result = 0;
        for (var i = 0; i < polynomial.Length; i++)
        {
            result = GfMultiply(result, x) ^ polynomial[i];
        }

        return result;
    }

    /// <summary>
    /// 高次係数順多項式同士をGF(256)上で乗算します。
    /// </summary>
    /// <param name="left">左多項式。</param>
    /// <param name="right">右多項式。</param>
    /// <returns>乗算結果多項式。</returns>
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

    /// <summary>
    /// GF(256) 乗算を行います。
    /// </summary>
    /// <param name="a">被乗数。</param>
    /// <param name="b">乗数。</param>
    /// <returns>乗算結果。</returns>
    private static int GfMultiply(int a, int b)
    {
        return MulTable[(a << 8) | b];
    }

    /// <summary>
    /// 原始元 alpha のべき値を返します。
    /// </summary>
    /// <param name="power">指数。</param>
    /// <returns>alpha の power 乗。</returns>
    private static int GfPowAlpha(int power)
    {
        var exponent = power % 255;
        if (exponent < 0)
        {
            exponent += 255;
        }

        return ExpTable[exponent];
    }

    /// <summary>
    /// GF(256) 除算を行います。
    /// </summary>
    /// <param name="a">被除数。</param>
    /// <param name="b">除数（0不可）。</param>
    /// <returns>除算結果。</returns>
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

    /// <summary>
    /// GfInverse を実行します。
    /// </summary>
    /// <param name="value">value を指定します。</param>
    /// <returns>荵玲ｳ暮・・縲・/returns>
    private static int GfInverse(int value)
    {
        if (value == 0)
        {
            throw new DivideByZeroException("GF(256) inverse of zero is undefined.");
        }

        return ExpTable[255 - LogTable[value]];
    }

    /// <summary>
    /// GfPower を実行します。
    /// </summary>
    /// <param name="value">蠎輔・/param>
    /// <param name="power">謖・焚縲・/param>
    /// <returns>value^power縲・/returns>
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

        var logValue = LogTable[value];
        var exponent = (logValue * power) % 255;
        if (exponent < 0)
        {
            exponent += 255;
        }

        return ExpTable[exponent];
    }
}

