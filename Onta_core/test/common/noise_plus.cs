using System.Numerics;
using System.Text;

namespace Onta.Core;

/// <summary>
/// WAV（2ch / 16-bit PCM）に対するノイズ・ワウフラッター付与ユーティリティです。
/// </summary>
public static class NoisePlus
{
    /// <summary>
    /// 入力 WAV にホワイトノイズを加算して出力します。
    /// </summary>
    /// <param name="inputWavPath">入力 WAV パス（2ch / 16-bit PCM）。</param>
    /// <param name="outputWavPath">出力 WAV パス。</param>
    /// <param name="noiseLevel">
    /// フルスケール（±1.0）に対するノイズ振幅。
    /// 0 で無変化、1.0 でピーク付近の強いノイズ。推奨範囲は 0..1。
    /// </param>
    /// <param name="seed">乱数シード。0 の場合は時間依存シードを使用します。</param>
    /// <exception cref="ArgumentNullException">パスが null の場合にスローされます。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="noiseLevel"/> が負の場合にスローされます。</exception>
    public static void AddWhiteNoise(
        string inputWavPath,
        string outputWavPath,
        double noiseLevel,
        int seed = 0)
    {
        ArgumentNullException.ThrowIfNull(inputWavPath);
        ArgumentNullException.ThrowIfNull(outputWavPath);
        if (noiseLevel < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(noiseLevel), "Noise level must be >= 0.");
        }

        var (sampleRate, left, right) = ReadStereo16(inputWavPath);
        if (noiseLevel == 0.0)
        {
            WriteStereo16(outputWavPath, sampleRate, left, right);
            return;
        }

        var random = seed == 0 ? new Random() : new Random(seed);
        for (var i = 0; i < left.Length; i++)
        {
            // Box-Muller で正規乱数を生成し、振幅を noiseLevel でスケールする。
            left[i] = ClampToUnit(left[i] + (NextGaussian(random) * noiseLevel));
            right[i] = ClampToUnit(right[i] + (NextGaussian(random) * noiseLevel));
        }

        WriteStereo16(outputWavPath, sampleRate, left, right);
    }

    /// <summary>
    /// メモリ上のステレオ実信号へホワイトノイズを加算します。
    /// </summary>
    public static void AddWhiteNoiseInMemory(
        Span<float> left,
        Span<float> right,
        double noiseLevel,
        int seed = 0)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Left/right length mismatch.");
        }

        if (noiseLevel <= 0.0 || left.Length == 0)
        {
            return;
        }

        var random = seed == 0 ? new Random() : new Random(seed);
        for (var i = 0; i < left.Length; i++)
        {
            left[i] = ClampToUnit(left[i] + (NextGaussian(random) * noiseLevel));
            right[i] = ClampToUnit(right[i] + (NextGaussian(random) * noiseLevel));
        }
    }

    /// <summary>
    /// メモリ上の複素 OFDM 実信号へ可逆ワウを付与します。
    /// </summary>
    public static (Complex[] Left, Complex[] Right, double WowPhase, double FlutterPhase) ApplyWowFlutterInMemory(
        Complex[] left,
        Complex[] right,
        int sampleRate,
        double flutterAmount,
        int seed = 0)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Left/right length mismatch.");
        }

        if (flutterAmount == 0.0 || left.Length == 0)
        {
            return ((Complex[])left.Clone(), (Complex[])right.Clone(), 0.0, 0.0);
        }

        var (wowPhase, flutterPhase) = WowFlutterWarp.CreatePhases(seed);
        var outLeft = WowFlutterWarp.Apply(left, sampleRate, flutterAmount, wowPhase, flutterPhase);
        var outRight = right.Length == 0
            ? Array.Empty<Complex>()
            : WowFlutterWarp.Apply(right, sampleRate, flutterAmount, wowPhase, flutterPhase);
        return (outLeft, outRight, wowPhase, flutterPhase);
    }

    /// <summary>
    /// 入力 WAV にワウ・フラッター（再生速度の周期的変動）を付与して出力します。
    /// </summary>
    /// <param name="inputWavPath">入力 WAV パス（2ch / 16-bit PCM）。</param>
    /// <param name="outputWavPath">出力 WAV パス。</param>
    /// <param name="flutterAmount">
    /// 速度変調の深さ（ピーク相対値）。
    /// 例: 0.01 なら瞬間速度がおおよそ ±1% 変動します。0 で無変化。
    /// </param>
    /// <param name="seed">乱数シード（変調位相の初期化に使用）。0 の場合は時間依存シードを使用します。</param>
    /// <returns>適用した wow / flutter 位相（復号側の既知補正や診断用）。</returns>
    /// <exception cref="ArgumentNullException">パスが null の場合にスローされます。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="flutterAmount"/> が負、または 1 以上の場合にスローされます。</exception>
    public static (double WowPhase, double FlutterPhase) ApplyWowFlutter(
        string inputWavPath,
        string outputWavPath,
        double flutterAmount,
        int seed = 0)
    {
        ArgumentNullException.ThrowIfNull(inputWavPath);
        ArgumentNullException.ThrowIfNull(outputWavPath);
        if (flutterAmount < 0.0 || flutterAmount >= 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(flutterAmount), "Flutter amount must be in range [0, 1).");
        }

        var (sampleRate, left, right) = ReadStereo16(inputWavPath);
        if (flutterAmount == 0.0 || left.Length == 0)
        {
            WriteStereo16(outputWavPath, sampleRate, left, right);
            return (0.0, 0.0);
        }

        var (wowPhase, flutterPhase) = WowFlutterWarp.CreatePhases(seed);
        var leftD = new double[left.Length];
        var rightD = new double[right.Length];
        for (var i = 0; i < left.Length; i++)
        {
            leftD[i] = left[i];
            rightD[i] = right[i];
        }

        leftD = WowFlutterWarp.Apply(leftD, sampleRate, flutterAmount, wowPhase, flutterPhase);
        rightD = WowFlutterWarp.Apply(rightD, sampleRate, flutterAmount, wowPhase, flutterPhase);
        var outLeft = new float[left.Length];
        var outRight = new float[right.Length];
        for (var i = 0; i < left.Length; i++)
        {
            outLeft[i] = ClampToUnit(leftD[i]);
            outRight[i] = ClampToUnit(rightD[i]);
        }

        WriteStereo16(outputWavPath, sampleRate, outLeft, outRight);
        return (wowPhase, flutterPhase);
    }

    private static double NextGaussian(Random random)
    {
        // Box-Muller 変換。
        var u1 = 1.0 - random.NextDouble();
        var u2 = 1.0 - random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static float ClampToUnit(double value)
    {
        if (value > 1.0)
        {
            return 1f;
        }

        if (value < -1.0)
        {
            return -1f;
        }

        return (float)value;
    }

    private static (int SampleRate, float[] Left, float[] Right) ReadStereo16(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        var riff = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (riff != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        reader.ReadInt32();
        var wave = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (wave != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        var sampleRate = 0;
        short channels = 0;
        short bitsPerSample = 0;
        byte[]? data = null;

        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var chunkSize = reader.ReadInt32();
            if (chunkId == "fmt ")
            {
                var format = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bitsPerSample = reader.ReadInt16();
                var remaining = chunkSize - 16;
                if (remaining > 0)
                {
                    reader.ReadBytes(remaining);
                }

                if (format != 1 || channels != 2 || bitsPerSample != 16)
                {
                    throw new InvalidDataException("Expected 16-bit stereo (2ch) PCM WAV.");
                }
            }
            else if (chunkId == "data")
            {
                data = reader.ReadBytes(chunkSize);
                if ((chunkSize & 1) != 0 && stream.Position < stream.Length)
                {
                    reader.ReadByte();
                }
            }
            else
            {
                reader.ReadBytes(chunkSize);
                if ((chunkSize & 1) != 0 && stream.Position < stream.Length)
                {
                    reader.ReadByte();
                }
            }
        }

        if (data is null || sampleRate <= 0)
        {
            throw new InvalidDataException("WAV data/fmt chunk is invalid.");
        }

        var sampleCount = data.Length / 4;
        var left = new float[sampleCount];
        var right = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var o = i * 4;
            var l = (short)(data[o] | (data[o + 1] << 8));
            var r = (short)(data[o + 2] | (data[o + 3] << 8));
            left[i] = l / (float)short.MaxValue;
            right[i] = r / (float)short.MaxValue;
        }

        return (sampleRate, left, right);
    }

    private static void WriteStereo16(string path, int sampleRate, float[] left, float[] right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Left/Right lengths must match.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var dataBytes = left.Length * sizeof(short) * 2;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short) * 2);
        writer.Write((short)(sizeof(short) * 2));
        writer.Write((short)16);

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (var i = 0; i < left.Length; i++)
        {
            writer.Write(ToPcm16(left[i]));
            writer.Write(ToPcm16(right[i]));
        }
    }

    private static short ToPcm16(float value)
    {
        var clamped = Math.Clamp(value, -1f, 1f);
        return (short)Math.Round(clamped * short.MaxValue);
    }
}
