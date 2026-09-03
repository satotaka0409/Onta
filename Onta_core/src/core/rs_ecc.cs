using System;
using System.Collections.Generic;

namespace Otofa.Core;

public static class RsEcc256
{
    public const int DataUnitSize = 128;
    public const int ParitySymbols = 32;
    public const int PayloadBytesPerBlock = 128;
    public const int CodewordBytesPerBlock = PayloadBytesPerBlock + ParitySymbols;
    public const int EncodedUnitSize = CodewordBytesPerBlock;

    public static byte[] Encode(byte[] data128)
    {
        ArgumentNullException.ThrowIfNull(data128);
        if (data128.Length != DataUnitSize)
        {
            throw new ArgumentException($"Input must be exactly {DataUnitSize} bytes.", nameof(data128));
        }

        return ReedSolomonCodec.EncodeBlock(data128, ParitySymbols);
    }

    public static byte[] Decode(byte[] encoded160)
    {
        ArgumentNullException.ThrowIfNull(encoded160);
        if (encoded160.Length != EncodedUnitSize)
        {
            throw new ArgumentException($"Input must be exactly {EncodedUnitSize} bytes.", nameof(encoded160));
        }

        return ReedSolomonCodec.DecodeBlock(encoded160, ParitySymbols);
    }

    public static bool TryDecode(byte[] encoded160, out byte[] decoded128)
    {
        decoded128 = Array.Empty<byte>();
        try
        {
            decoded128 = Decode(encoded160);
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
    private const int FieldSize = 256;
    private const int PrimitivePolynomial = 0x11D;

    private static readonly int[] ExpTable = new int[FieldSize * 2];
    private static readonly int[] LogTable = new int[FieldSize];

    static ReedSolomonCodec()
    {
        InitializeTables();
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
        ArgumentNullException.ThrowIfNull(received);
        if (received.Length <= paritySymbols)
        {
            throw new ArgumentException("Received block is too short.", nameof(received));
        }

        var syndromes = CalculateSyndromes(received, paritySymbols);
        if (IsAllZero(syndromes))
        {
            var noParity = new byte[received.Length - paritySymbols];
            Array.Copy(received, 0, noParity, 0, noParity.Length);
            return noParity;
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

        var decoded = new byte[corrected.Length - paritySymbols];
        Array.Copy(corrected, 0, decoded, 0, decoded.Length);
        return decoded;
    }

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

    private static int[] BuildGeneratorPolynomial(int paritySymbols)
    {
        var gen = new[] { 1 };
        for (var i = 0; i < paritySymbols; i++)
        {
            gen = MultiplyPolynomialsHighDegree(gen, new[] { 1, GfPower(2, i) });
        }

        return gen;
    }

    private static int[] CalculateSyndromes(byte[] data, int paritySymbols)
    {
        var syndromes = new int[paritySymbols];
        for (var i = 0; i < paritySymbols; i++)
        {
            syndromes[i] = EvaluatePolynomialHighDegree(data, GfPower(2, i));
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

        var positions = new List<int>(degree);
        for (var i = 0; i < messageLength; i++)
        {
            var x = GfPower(2, 255 - i);
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

        var matrix = new int[count, count + 1];
        for (var row = 0; row < count; row++)
        {
            matrix[row, count] = syndromes[row];
            for (var col = 0; col < count; col++)
            {
                var locatorExponent = messageLength - 1 - errorPositions[col];
                var exponent = (locatorExponent * row) % 255;
                matrix[row, col] = GfPower(2, exponent);
            }
        }

        return SolveLinearSystemGf256(matrix, count);
    }

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
        if (a == 0 || b == 0)
        {
            return 0;
        }

        return ExpTable[LogTable[a] + LogTable[b]];
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

        var logValue = LogTable[value];
        var exponent = (logValue * power) % 255;
        if (exponent < 0)
        {
            exponent += 255;
        }

        return ExpTable[exponent];
    }
}
