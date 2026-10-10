using System.Numerics;
using Onta.Core;

namespace Onta.Performance;

/// <summary>
/// 性能測定用の PCM（トーン／スイープ／ホワイトノイズ／OFDM）を生成します。
/// </summary>
internal static class PerformanceSignalGenerator
{
    /// <summary>PCM サンプリング周波数（Hz）。</summary>
    public const int SampleRate = PerformanceConstants.SampleRate;

    /// <summary>性能測定で選べる最大サブキャリア数（<see cref="ClampSubcarriers"/> の上限）。</summary>
    public const int MaxSubcarriers = 72;

    /// <summary>SC ごとのキャッシュの要素数（slot = sc/8 で 0..MaxSubcarriers/8）。</summary>
    internal const int SubcarrierCacheSlots = (MaxSubcarriers / 8) + 1;

    /// <summary>SC=8..72 用キャリア Hz キャッシュ（slot = sc/8）。</summary>
    private static readonly double[]?[] LeftCarrierHzCache = new double[SubcarrierCacheSlots][];

    private static readonly double[]?[] RightCarrierHzCache = new double[SubcarrierCacheSlots][];
    private static readonly object CarrierHzLock = new();

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
        var sinStep = Math.Sin(omega);
        var cosStep = Math.Cos(omega);
        var sinPhase = Math.Sin(phase);
        var cosPhase = Math.Cos(phase);
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = new Complex(amp * sinPhase, 0.0);
            var nextSin = (sinPhase * cosStep) + (cosPhase * sinStep);
            var nextCos = (cosPhase * cosStep) - (sinPhase * sinStep);
            sinPhase = nextSin;
            cosPhase = nextCos;
            if ((i & 255) == 255)
            {
                var invNorm = 1.0 / Math.Sqrt((sinPhase * sinPhase) + (cosPhase * cosPhase));
                sinPhase *= invNorm;
                cosPhase *= invNorm;
            }
        }

        phase = WrapPhase(phase + (omega * destination.Length));
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
    /// 約 20Hz〜20kHz に帯域制限したホワイトノイズ・チャンクを生成します。
    /// </summary>
    /// <param name="destination">書き込み先（実部のみ）。</param>
    /// <param name="amplitude">ピーク振幅（0〜1）。</param>
    /// <param name="rng">乱数源。</param>
    /// <param name="filter">連続チャンク用の帯域制限フィルタ状態。</param>
    public static void FillWhiteNoiseChunk(
        Span<Complex> destination,
        double amplitude,
        Random rng,
        ref WhiteNoiseBandFilter filter)
    {
        ArgumentNullException.ThrowIfNull(rng);
        var amp = Math.Clamp(amplitude, 0.0, 1.0);
        for (var i = 0; i < destination.Length; i++)
        {
            // 一様乱数を帯域制限し、ピークが amplitude 付近になるようスケールする。
            var raw = (2.0 * rng.NextDouble()) - 1.0;
            var filtered = filter.Process(raw);
            var sample = Math.Clamp(filtered * WhiteNoisePeakScale, -1.0, 1.0) * amp;
            destination[i] = new Complex(sample, 0.0);
        }
    }

    /// <summary>
    /// フィルタ通過後のピーク補正係数（経験値。±1 入力で概ね ±1 出力）。
    /// </summary>
    private const double WhiteNoisePeakScale = 1.35;

    /// <summary>
    /// 正弦波トーンを生成します。
    /// </summary>
    /// <param name="frequencyHz">周波数（Hz）。</param>
    /// <param name="sampleCount">サンプル数。</param>
    /// <param name="channelMode">モノラル／ステレオ。</param>
    /// <param name="amplitude">振幅（0〜1）。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。0 以下は <see cref="SampleRate"/>。</param>
    /// <returns>L/R PCM（モノラル時 Right は空）。</returns>
    public static (Complex[] Left, Complex[] Right) GenerateTone(
        double frequencyHz,
        int sampleCount,
        ChannelMode channelMode,
        double amplitude = 0.7,
        int sampleRate = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        var fs = sampleRate > 0 ? sampleRate : SampleRate;
        var amp = Math.Clamp(amplitude, 0.0, 1.0);
        var omega = 2.0 * Math.PI * Math.Clamp(frequencyHz, 1.0, fs * 0.49) / fs;
        var left = new Complex[sampleCount];
        var sinStep = Math.Sin(omega);
        var cosStep = Math.Cos(omega);
        var sinPhase = 0.0;
        var cosPhase = 1.0;
        for (var i = 0; i < sampleCount; i++)
        {
            left[i] = new Complex(amp * sinPhase, 0.0);
            var nextSin = (sinPhase * cosStep) + (cosPhase * sinStep);
            var nextCos = (cosPhase * cosStep) - (sinPhase * sinStep);
            sinPhase = nextSin;
            cosPhase = nextCos;
            if ((i & 255) == 255)
            {
                var invNorm = 1.0 / Math.Sqrt((sinPhase * sinPhase) + (cosPhase * cosPhase));
                sinPhase *= invNorm;
                cosPhase *= invNorm;
            }
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
    /// <param name="startHz">開始周波数（Hz）。</param>
    /// <param name="endHz">終了周波数（Hz）。</param>
    /// <param name="sampleCount">サンプル数。</param>
    /// <param name="channelMode">モノラル／ステレオ。</param>
    /// <param name="amplitude">振幅（0〜1）。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。0 以下は <see cref="SampleRate"/>。</param>
    /// <returns>L/R PCM（モノラル時 Right は空）。</returns>
    public static (Complex[] Left, Complex[] Right) GenerateLogSweep(
        double startHz,
        double endHz,
        int sampleCount,
        ChannelMode channelMode,
        double amplitude = 0.7,
        int sampleRate = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        var fs = sampleRate > 0 ? sampleRate : SampleRate;
        var amp = Math.Clamp(amplitude, 0.0, 1.0);
        var f0 = Math.Clamp(startHz, 1.0, fs * 0.49);
        var f1 = Math.Clamp(endHz, f0 + 1.0, fs * 0.49);
        var lnRatio = Math.Log(f1 / f0);
        var denom = Math.Max(1, sampleCount - 1);
        var twoPiOverFs = 2.0 * Math.PI / fs;
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
    /// 約 20Hz〜20kHz の帯域制限ホワイトノイズを生成します。
    /// </summary>
    /// <param name="sampleCount">サンプル数。</param>
    /// <param name="channelMode">モノラル／ステレオ。</param>
    /// <param name="amplitude">ピーク振幅。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。0 以下は <see cref="SampleRate"/>。</param>
    /// <returns>L/R PCM。</returns>
    public static (Complex[] Left, Complex[] Right) GenerateWhiteNoise(
        int sampleCount,
        ChannelMode channelMode,
        double amplitude = 0.7,
        int sampleRate = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        var fs = sampleRate > 0 ? sampleRate : SampleRate;
        var rng = new Random();
        var filter = WhiteNoiseBandFilter.Create(fs);
        var left = new Complex[sampleCount];
        FillWhiteNoiseChunk(left, amplitude, rng, ref filter);

        if (channelMode == ChannelMode.Mono)
        {
            return (left, Array.Empty<Complex>());
        }

        // ステレオは L/R 独立ノイズ（相関なし）。
        var rightFilter = WhiteNoiseBandFilter.Create(fs);
        var right = new Complex[sampleCount];
        FillWhiteNoiseChunk(right, amplitude, rng, ref rightFilter);
        return (left, right);
    }

    /// <summary>
    /// 測定用 OFDM ジェネレータを構築します。
    /// </summary>
    /// <param name="activeSubcarriers">サブキャリア数。</param>
    /// <param name="modulation">変調方式。</param>
    /// <param name="channelMode">モノラル／ステレオ。</param>
    /// <param name="ofdmSymbolCount">1 フレームの OFDM シンボル数。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。0 以下は <see cref="SampleRate"/>。</param>
    /// <returns>設定済み OfdmGenerator。</returns>
    public static OfdmGenerator CreateOfdmGenerator(
        int activeSubcarriers,
        ModulationScheme modulation,
        ChannelMode channelMode,
        int ofdmSymbolCount = 8,
        int sampleRate = 0)
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
            sampleRate: sampleRate > 0 ? sampleRate : SampleRate,
            randomSeed: Random.Shared.Next(),
            carrierGrid: grid);
        return new OfdmGenerator(config);
    }

    /// <summary>
    /// OFDM フレームを指定秒数ぶん連結して生成します。
    /// データキャリアのペイロードは乱数です（ファイルや固定パターンは使いません）。
    /// </summary>
    /// <param name="activeSubcarriers">サブキャリア数。</param>
    /// <param name="modulation">変調方式。</param>
    /// <param name="channelMode">モノラル／ステレオ。</param>
    /// <param name="durationSeconds">生成秒数。</param>
    /// <param name="amplitude">ピーク振幅（0〜1）。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。0 以下は <see cref="SampleRate"/>。</param>
    /// <returns>L/R PCM（モノラル時 Right は空）。</returns>
    public static (Complex[] Left, Complex[] Right) GenerateModulated(
        int activeSubcarriers,
        ModulationScheme modulation,
        ChannelMode channelMode,
        double durationSeconds,
        double amplitude = 0.7,
        int sampleRate = 0)
    {
        var fs = sampleRate > 0 ? sampleRate : SampleRate;
        var totalSamples = Math.Max(1, (int)Math.Round(durationSeconds * fs));
        var (leftChunks, rightChunks, _) = GenerateModulatedFrames(activeSubcarriers, modulation, channelMode, totalSamples, fs);
        return ConcatModulated(leftChunks, rightChunks, channelMode, totalSamples, amplitude);
    }

    /// <summary>
    /// 繰り返し再生用の OFDM 変調信号を、OFDM フレーム単位（末尾を切らない）で指定秒数以上生成します。
    /// </summary>
    /// <remarks>フレームの途中で切ると、先頭へ戻る位置でシンボル境界がずれて受信側の I-Q が乱れる。</remarks>
    /// <param name="activeSubcarriers">サブキャリア数。</param>
    /// <param name="modulation">変調方式。</param>
    /// <param name="channelMode">モノラル／ステレオ。</param>
    /// <param name="minSeconds">最低限の長さ（秒）。</param>
    /// <param name="amplitude">ピーク振幅（0〜1）。</param>
    /// <returns>L/R PCM（<see cref="SampleRate"/>。モノラル時 Right は空）。</returns>
    public static (Complex[] Left, Complex[] Right) GenerateModulatedLoop(
        int activeSubcarriers,
        ModulationScheme modulation,
        ChannelMode channelMode,
        double minSeconds,
        double amplitude = 1.0)
    {
        var minSamples = Math.Max(1, (int)Math.Round(minSeconds * SampleRate));
        var (leftChunks, rightChunks, produced) = GenerateModulatedFrames(activeSubcarriers, modulation, channelMode, minSamples, SampleRate);
        return ConcatModulated(leftChunks, rightChunks, channelMode, produced, amplitude);
    }

    /// <summary>
    /// 乱数ペイロードの OFDM フレームを、合計が指定サンプル数以上になるまで生成します。
    /// </summary>
    /// <param name="activeSubcarriers">サブキャリア数。</param>
    /// <param name="modulation">変調方式。</param>
    /// <param name="channelMode">モノラル／ステレオ。</param>
    /// <param name="minSamples">最低限のサンプル数。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <returns>L/R のフレーム列と合計サンプル数（モノラル時 Right は空リスト）。</returns>
    private static (List<Complex[]> Left, List<Complex[]> Right, int Produced) GenerateModulatedFrames(
        int activeSubcarriers,
        ModulationScheme modulation,
        ChannelMode channelMode,
        int minSamples,
        int sampleRate)
    {
        var ofdm = CreateOfdmGenerator(
            activeSubcarriers,
            modulation,
            channelMode,
            ofdmSymbolCount: 8,
            sampleRate: sampleRate);
        var leftChunks = new List<Complex[]>(capacity: 64);
        var rightChunks = new List<Complex[]>(capacity: 64);
        var produced = 0;
        while (produced < minSamples)
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

        return (leftChunks, rightChunks, produced);
    }

    /// <summary>
    /// OFDM フレーム列を連結して指定長へ切り詰め、ピーク振幅をそろえます。
    /// </summary>
    /// <param name="leftChunks">L フレーム列。</param>
    /// <param name="rightChunks">R フレーム列。</param>
    /// <param name="channelMode">モノラル／ステレオ。</param>
    /// <param name="sampleCount">出力サンプル数。</param>
    /// <param name="amplitude">ピーク振幅（0〜1）。</param>
    /// <returns>L/R PCM（モノラル時 Right は空）。</returns>
    private static (Complex[] Left, Complex[] Right) ConcatModulated(
        List<Complex[]> leftChunks,
        List<Complex[]> rightChunks,
        ChannelMode channelMode,
        int sampleCount,
        double amplitude)
    {
        var left = ConcatAndTrim(leftChunks, sampleCount);
        ScaleInPlace(left, amplitude);
        if (channelMode == ChannelMode.Mono)
        {
            return (left, Array.Empty<Complex>());
        }

        var right = ConcatAndTrim(rightChunks, sampleCount);
        ScaleInPlace(right, amplitude);
        return (left, right);
    }

    /// <summary>
    /// キャリア周波数一覧（L）を返します。
    /// </summary>
    /// <param name="activeSubcarriers">サブキャリア数。</param>
    /// <returns>L キャリア周波数（Hz）。</returns>
    public static double[] ResolveLeftCarrierHz(int activeSubcarriers)
    {
        var sc = ClampSubcarriers(activeSubcarriers);
        var slot = sc / 8;
        var existing = Volatile.Read(ref LeftCarrierHzCache[slot]);
        if (existing is not null)
        {
            return existing;
        }

        lock (CarrierHzLock)
        {
            existing = LeftCarrierHzCache[slot];
            if (existing is not null)
            {
                return existing;
            }

            existing = BuildCarrierHz(sc, useRight: false);
            Volatile.Write(ref LeftCarrierHzCache[slot], existing);
            return existing;
        }
    }

    /// <summary>
    /// キャリア周波数一覧（R）を返します。
    /// </summary>
    /// <param name="activeSubcarriers">サブキャリア数。</param>
    /// <returns>R キャリア周波数（Hz）。</returns>
    public static double[] ResolveRightCarrierHz(int activeSubcarriers)
    {
        var sc = ClampSubcarriers(activeSubcarriers);
        var slot = sc / 8;
        var existing = Volatile.Read(ref RightCarrierHzCache[slot]);
        if (existing is not null)
        {
            return existing;
        }

        lock (CarrierHzLock)
        {
            existing = RightCarrierHzCache[slot];
            if (existing is not null)
            {
                return existing;
            }

            existing = BuildCarrierHz(sc, useRight: true);
            Volatile.Write(ref RightCarrierHzCache[slot], existing);
            return existing;
        }
    }

    /// <summary>
    /// SC に対応する L/R キャリア周波数表を構築します。
    /// </summary>
    /// <param name="activeSubcarriers">サブキャリア数。</param>
    /// <param name="useRight">R 側周波数を使うか。</param>
    /// <returns>キャリア周波数（Hz）。</returns>
    private static double[] BuildCarrierHz(int activeSubcarriers, bool useRight)
    {
        var bins = OfdmConfig.ResolveConceptualLeftBins(activeSubcarriers);
        var grid = OfdmConfig.ResolveCarrierGrid(activeSubcarriers);
        var hz = new double[bins.Length];
        for (var i = 0; i < bins.Length; i++)
        {
            hz[i] = grid == OfdmCarrierGrid.Sc8Family
                ? (useRight
                    ? OfdmConfig.RightCarrierHzSc8(bins[i] - 1)
                    : OfdmConfig.LeftCarrierHzSc8(bins[i] - 1))
                : (useRight
                    ? OfdmConfig.RightCarrierHzSc24(bins[i])
                    : OfdmConfig.LeftCarrierHzSc24(bins[i]));
        }

        return hz;
    }

    /// <summary>
    /// UI の SC 値をコア対応範囲へ丸めます。
    /// </summary>
    /// <param name="value">希望サブキャリア数。</param>
    /// <returns>16〜72 の対応値。</returns>
    public static int ClampSubcarriers(int value) =>
        value switch
        {
            <= 16 => 16,
            <= 24 => 24,
            <= 32 => 32,
            <= 40 => 40,
            <= 48 => 48,
            <= 56 => 56,
            <= 64 => 64,
            _ => MaxSubcarriers
        };

    /// <summary>
    /// UI の変調をコア対応範囲へ丸めます。
    /// </summary>
    /// <param name="value">希望変調。</param>
    /// <returns>有効な変調方式。</returns>
    public static ModulationScheme ClampModulation(ModulationScheme value) =>
        value is ModulationScheme.Bpsk
            or ModulationScheme.Qpsk
            or ModulationScheme.Psk8
            or ModulationScheme.Qam16
            or ModulationScheme.Qam64
            or ModulationScheme.Qam256
            ? value
            : ModulationScheme.Qam64;

    /// <summary>
    /// チャンク列を連結し sampleCount に切り詰めます。
    /// </summary>
    /// <param name="chunks">PCM チャンク。</param>
    /// <param name="sampleCount">出力サンプル数。</param>
    /// <returns>連結結果。</returns>
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

    /// <summary>
    /// ピーク正規化して振幅を合わせます。
    /// </summary>
    /// <param name="samples">PCM（破壊的）。</param>
    /// <param name="amplitude">目標ピーク振幅。</param>
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

    /// <summary>
    /// 位相を 0 以上 2π 未満へ折り返します。
    /// </summary>
    /// <param name="phase">折り返す位相（ラジアン）。</param>
    /// <returns>0 以上 2π 未満の位相。</returns>
    private static double WrapPhase(double phase)
    {
        var twoPi = Math.PI * 2.0;
        phase %= twoPi;
        if (phase < 0.0)
        {
            phase += twoPi;
        }

        return phase;
    }
}

/// <summary>
/// ホワイトノイズ用の 20Hz HPF + 20kHz LPF（RBJ biquad）状態です。
/// </summary>
internal struct WhiteNoiseBandFilter
{
    private double _hpB0, _hpB1, _hpB2, _hpA1, _hpA2;
    private double _hpZ1, _hpZ2;
    private double _lpB0, _lpB1, _lpB2, _lpA1, _lpA2;
    private double _lpZ1, _lpZ2;

    /// <summary>
    /// サンプリング周波数から 20Hz〜20kHz 帯域制限フィルタを構築します。
    /// </summary>
    /// <param name="sampleRate">PCM サンプリング周波数。</param>
    /// <returns>初期化済みフィルタ。</returns>
    public static WhiteNoiseBandFilter Create(int sampleRate)
    {
        var fs = Math.Max(1, sampleRate);
        var filter = new WhiteNoiseBandFilter();
        // Nyquist 付近の 20kHz LPF は効果が薄いが、仕様の帯域表記に合わせて入れる。
        filter.SetHighPass(20.0, fs);
        filter.SetLowPass(Math.Min(20000.0, fs * 0.45), fs);
        return filter;
    }

    /// <summary>
    /// 1 サンプルを帯域制限して返します。
    /// </summary>
    /// <param name="input">入力（概ね ±1）。</param>
    /// <returns>フィルタ出力。</returns>
    public double Process(double input)
    {
        var hp = (_hpB0 * input) + _hpZ1;
        _hpZ1 = (_hpB1 * input) - (_hpA1 * hp) + _hpZ2;
        _hpZ2 = (_hpB2 * input) - (_hpA2 * hp);

        var lp = (_lpB0 * hp) + _lpZ1;
        _lpZ1 = (_lpB1 * hp) - (_lpA1 * lp) + _lpZ2;
        _lpZ2 = (_lpB2 * hp) - (_lpA2 * lp);
        return lp;
    }

    /// <summary>
    /// ハイパス係数を設定します（RBJ、Q=1/√2）。
    /// </summary>
    /// <param name="cutoffHz">遮断周波数（Hz）。</param>
    /// <param name="sampleRate">サンプリング周波数。</param>
    private void SetHighPass(double cutoffHz, int sampleRate)
    {
        // RBJ Audio EQ Cookbook — highpass, Q=1/√2
        var w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
        var cos = Math.Cos(w0);
        var sin = Math.Sin(w0);
        var alpha = sin / Math.Sqrt(2.0);
        var b0 = (1.0 + cos) * 0.5;
        var b1 = -(1.0 + cos);
        var b2 = (1.0 + cos) * 0.5;
        var a0 = 1.0 + alpha;
        var a1 = -2.0 * cos;
        var a2 = 1.0 - alpha;
        _hpB0 = b0 / a0;
        _hpB1 = b1 / a0;
        _hpB2 = b2 / a0;
        _hpA1 = a1 / a0;
        _hpA2 = a2 / a0;
        _hpZ1 = 0;
        _hpZ2 = 0;
    }

    /// <summary>
    /// ローパス係数を設定します（RBJ、Q=1/√2）。
    /// </summary>
    /// <param name="cutoffHz">遮断周波数（Hz）。</param>
    /// <param name="sampleRate">サンプリング周波数。</param>
    private void SetLowPass(double cutoffHz, int sampleRate)
    {
        var w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
        var cos = Math.Cos(w0);
        var sin = Math.Sin(w0);
        var alpha = sin / Math.Sqrt(2.0);
        var b0 = (1.0 - cos) * 0.5;
        var b1 = 1.0 - cos;
        var b2 = (1.0 - cos) * 0.5;
        var a0 = 1.0 + alpha;
        var a1 = -2.0 * cos;
        var a2 = 1.0 - alpha;
        _lpB0 = b0 / a0;
        _lpB1 = b1 / a0;
        _lpB2 = b2 / a0;
        _lpA1 = a1 / a0;
        _lpA2 = a2 / a0;
        _lpZ1 = 0;
        _lpZ2 = 0;
    }
}
