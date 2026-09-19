using System.Numerics;
using Onta.Core;

namespace Onta.View.Performance;

/// <summary>
/// 性能測定用の PCM（トーン／スイープ／OFDM）を生成します。
/// </summary>
internal static class PerformanceSignalGenerator
{
    /// <summary>PCM サンプリング周波数（Hz）。</summary>
    public const int SampleRate = PerformanceConstants.SampleRate;

    /// <summary>
    /// 位相連続の正弦波チャンクを生成します（ライブ周波数／振幅変更用）。
    /// </summary>
    /// <param name="destination">書き込み先（実部のみ使用）。</param>
    /// <param name="frequencyHz">周波数（Hz）。</param>
    /// <param name="amplitude">振幅（0〜1）。</param>
    /// <param name="phase">開始位相（ラジアン）。終了位相で更新されます。</param>
    public static void FillToneChunk(Span<Complex> destination, double frequencyHz, double amplitude, ref double phase)
    {
        var amp = Math.Clamp(amplitude, 0.0, 1.0);
        var omega = 2.0 * Math.PI * Math.Clamp(frequencyHz, 1.0, SampleRate * 0.49) / SampleRate;
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = new Complex(amp * Math.Sin(phase), 0.0);
            phase += omega;
            if (phase > Math.PI * 2.0)
            {
                phase -= Math.PI * 2.0;
            }
        }
    }

    /// <summary>
    /// 対数スイープの連続チャンクを生成します（ライブ振幅変更用）。
    /// </summary>
    /// <param name="destination">書き込み先。</param>
    /// <param name="startHz">開始周波数。</param>
    /// <param name="endHz">終了周波数。</param>
    /// <param name="totalSamples">全体サンプル数。</param>
    /// <param name="sampleIndex">このチャンク先頭の絶対サンプル番号。</param>
    /// <param name="amplitude">振幅。</param>
    /// <param name="phase">位相（継続更新）。</param>
    public static void FillLogSweepChunk(
        Span<Complex> destination,
        double startHz,
        double endHz,
        int totalSamples,
        int sampleIndex,
        double amplitude,
        ref double phase)
    {
        var amp = Math.Clamp(amplitude, 0.0, 1.0);
        var f0 = Math.Clamp(startHz, 1.0, SampleRate * 0.49);
        var f1 = Math.Clamp(endHz, f0 + 1.0, SampleRate * 0.49);
        var lnRatio = Math.Log(f1 / f0);
        var denom = Math.Max(1, totalSamples - 1);
        var twoPiOverFs = 2.0 * Math.PI / SampleRate;
        for (var i = 0; i < destination.Length; i++)
        {
            var abs = sampleIndex + i;
            var u = Math.Clamp(abs / (double)denom, 0.0, 1.0);
            var v = (1.5 * u) - (0.5 * u * u);
            var freq = f0 * Math.Exp(lnRatio * v);
            phase += twoPiOverFs * freq;
            destination[i] = new Complex(amp * Math.Sin(phase), 0.0);
        }
    }

    /// <summary>
    /// 正弦波トーンを生成します。
    /// </summary>
    public static (Complex[] Left, Complex[] Right) GenerateTone(
        double frequencyHz,
        int sampleCount,
        ChannelMode channelMode,
        double amplitude = 0.7)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        var amp = Math.Clamp(amplitude, 0.0, 1.0);
        var omega = 2.0 * Math.PI * Math.Clamp(frequencyHz, 1.0, SampleRate * 0.49) / SampleRate;
        var left = new Complex[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            left[i] = new Complex(amp * Math.Sin(omega * i), 0.0);
        }

        if (channelMode == ChannelMode.Mono)
        {
            return (left, Array.Empty<Complex>());
        }

        var right = new Complex[sampleCount];
        Array.Copy(left, right, sampleCount);
        return (left, right);
    }

    /// <summary>
    /// 対数周波数スイープ（startHz → endHz）を生成します。
    /// 終端の対数進行速度は純対数スイープの半分（v=1.5u−0.5u²）にし、高域のピーク移動を緩やかにします。
    /// </summary>
    public static (Complex[] Left, Complex[] Right) GenerateLogSweep(
        double startHz,
        double endHz,
        int sampleCount,
        ChannelMode channelMode,
        double amplitude = 0.7)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        var amp = Math.Clamp(amplitude, 0.0, 1.0);
        var f0 = Math.Clamp(startHz, 1.0, SampleRate * 0.49);
        var f1 = Math.Clamp(endHz, f0 + 1.0, SampleRate * 0.49);
        var lnRatio = Math.Log(f1 / f0);
        var denom = Math.Max(1, sampleCount - 1);
        var twoPiOverFs = 2.0 * Math.PI / SampleRate;
        var left = new Complex[sampleCount];
        var phase = 0.0;
        for (var i = 0; i < sampleCount; i++)
        {
            var u = i / (double)denom;
            // v(0)=0, v(1)=1, v'(1)=1/2（高域の df/dt を純対数の半分）
            var v = (1.5 * u) - (0.5 * u * u);
            var freq = f0 * Math.Exp(lnRatio * v);
            phase += twoPiOverFs * freq;
            left[i] = new Complex(amp * Math.Sin(phase), 0.0);
        }

        if (channelMode == ChannelMode.Mono)
        {
            return (left, Array.Empty<Complex>());
        }

        var right = new Complex[sampleCount];
        Array.Copy(left, right, sampleCount);
        return (left, right);
    }

    /// <summary>
    /// 測定用 OFDM ジェネレータを構築します。
    /// </summary>
    public static OfdmGenerator CreateOfdmGenerator(
        int activeSubcarriers,
        ModulationScheme modulation,
        ChannelMode channelMode,
        int ofdmSymbolCount = 8)
    {
        var sc = ClampSubcarriers(activeSubcarriers);
        var mod = ClampModulation(modulation);
        var grid = OfdmConfig.ResolveCarrierGrid(sc);
        var fftSize = OfdmConfig.ResolveFftSize(sc, channelMode);
        var config = new OfdmConfig(
            fftSize: fftSize,
            activeSubcarriers: sc,
            cyclicPrefixLength: 16,
            ofdmSymbolCount: Math.Max(1, ofdmSymbolCount),
            modulationScheme: mod,
            channelMode: channelMode,
            pilotSpacing: 8,
            stereoFrequencyShiftBins: 1,
            sampleRate: SampleRate,
            randomSeed: Random.Shared.Next(),
            carrierGrid: grid);
        return new OfdmGenerator(config);
    }

    /// <summary>
    /// OFDM フレームを指定秒数ぶん連結して生成します。
    /// データキャリアのペイロードは乱数です（ファイルや固定パターンは使いません）。
    /// </summary>
    public static (Complex[] Left, Complex[] Right) GenerateModulated(
        int activeSubcarriers,
        ModulationScheme modulation,
        ChannelMode channelMode,
        double durationSeconds,
        double amplitude = 0.7)
    {
        var totalSamples = Math.Max(1, (int)Math.Round(durationSeconds * SampleRate));
        var ofdm = CreateOfdmGenerator(activeSubcarriers, modulation, channelMode, ofdmSymbolCount: 8);
        var leftChunks = new List<Complex[]>(capacity: 64);
        var rightChunks = new List<Complex[]>(capacity: 64);
        var produced = 0;
        while (produced < totalSamples)
        {
            if (channelMode == ChannelMode.Stereo)
            {
                var (l, r) = ofdm.GenerateStereoFrame();
                leftChunks.Add(l);
                rightChunks.Add(r);
                produced += l.Length;
            }
            else
            {
                var l = ofdm.GenerateFrame();
                leftChunks.Add(l);
                produced += l.Length;
            }
        }

        var left = ConcatAndTrim(leftChunks, totalSamples);
        ScaleInPlace(left, amplitude);
        if (channelMode == ChannelMode.Mono)
        {
            return (left, Array.Empty<Complex>());
        }

        var right = ConcatAndTrim(rightChunks, totalSamples);
        ScaleInPlace(right, amplitude);
        return (left, right);
    }

    /// <summary>
    /// キャリア周波数一覧（L）を返します。
    /// </summary>
    public static double[] ResolveLeftCarrierHz(int activeSubcarriers)
    {
        var sc = ClampSubcarriers(activeSubcarriers);
        var bins = OfdmConfig.ResolveConceptualLeftBins(sc);
        var grid = OfdmConfig.ResolveCarrierGrid(sc);
        var hz = new double[bins.Length];
        for (var i = 0; i < bins.Length; i++)
        {
            hz[i] = grid == OfdmCarrierGrid.Sc8Family
                ? OfdmConfig.LeftCarrierHzSc8(bins[i] - 1)
                : OfdmConfig.LeftCarrierHzSc24(bins[i]);
        }

        return hz;
    }

    /// <summary>
    /// キャリア周波数一覧（R）を返します。
    /// </summary>
    public static double[] ResolveRightCarrierHz(int activeSubcarriers)
    {
        var sc = ClampSubcarriers(activeSubcarriers);
        var bins = OfdmConfig.ResolveConceptualLeftBins(sc);
        var grid = OfdmConfig.ResolveCarrierGrid(sc);
        var hz = new double[bins.Length];
        for (var i = 0; i < bins.Length; i++)
        {
            hz[i] = grid == OfdmCarrierGrid.Sc8Family
                ? OfdmConfig.RightCarrierHzSc8(bins[i] - 1)
                : OfdmConfig.RightCarrierHzSc24(bins[i]);
        }

        return hz;
    }

    /// <summary>
    /// UI の SC 値をコア対応範囲へ丸めます（性能測定は 56/64 可）。
    /// </summary>
    public static int ClampSubcarriers(int value) =>
        value switch
        {
            <= 8 => 8,
            <= 16 => 16,
            <= 24 => 24,
            <= 32 => 32,
            <= 40 => 40,
            <= 48 => 48,
            <= 56 => 56,
            _ => 64
        };

    /// <summary>
    /// UI の変調をコア対応範囲へ丸めます。
    /// </summary>
    public static ModulationScheme ClampModulation(ModulationScheme value) =>
        value is ModulationScheme.Bpsk
            or ModulationScheme.Qpsk
            or ModulationScheme.Qam16
            or ModulationScheme.Qam64
            or ModulationScheme.Qam256
            ? value
            : ModulationScheme.Qam64;

    private static Complex[] ConcatAndTrim(List<Complex[]> chunks, int sampleCount)
    {
        var result = new Complex[sampleCount];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            var copy = Math.Min(chunk.Length, sampleCount - offset);
            if (copy <= 0)
            {
                break;
            }

            Array.Copy(chunk, 0, result, offset, copy);
            offset += copy;
            if (offset >= sampleCount)
            {
                break;
            }
        }

        return result;
    }

    private static void ScaleInPlace(Complex[] samples, double amplitude)
    {
        var peak = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            peak = Math.Max(peak, Math.Abs(samples[i].Real));
        }

        if (peak <= 1e-12)
        {
            return;
        }

        var scale = Math.Clamp(amplitude, 0.0, 1.0) / peak;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = new Complex(samples[i].Real * scale, 0.0);
        }
    }
}
