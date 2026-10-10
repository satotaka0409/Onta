using System.Numerics;
using NAudio.Wave;

namespace Onta.Stream;

/// <summary>
/// WAV / MP3 / FLAC などを PCM（指定または元サンプリング周波数・ステレオ）へ変換しながらチャンク読み込みします。
/// </summary>
public sealed class StreamAudioFilePcmReader : IDisposable
{
    private readonly AudioFileReader _reader;
    private readonly ISampleProvider _samples;
    private readonly MediaFoundationResampler? _resampler;
    private readonly float[] _buffer;
    private bool _disposed;

    /// <summary>出力 PCM のサンプリング周波数（Hz）。</summary>
    public int SampleRate { get; }

    /// <summary>
    /// 音声ファイルを開き、必要なら指定サンプリング周波数のステレオへ変換するリーダーを構築します。
    /// </summary>
    /// <param name="path">入力ファイルパス。</param>
    /// <param name="outputSampleRate">出力サンプリング周波数。0 以下なら元ファイルのレートを使います。</param>
    public StreamAudioFilePcmReader(string path, int outputSampleRate = 0)
    {
        _reader = new AudioFileReader(path);
        var src = _reader.WaveFormat;
        SampleRate = outputSampleRate > 0
            ? outputSampleRate
            : src.SampleRate > 0 ? src.SampleRate : StreamConstants.DefaultSampleRate;
        var target = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);
        if (src.SampleRate != target.SampleRate
            || src.Channels != target.Channels
            || src.Encoding != WaveFormatEncoding.IeeeFloat)
        {
            _resampler = new MediaFoundationResampler(_reader, target)
            {
                ResamplerQuality = 60,
            };
            _samples = _resampler.ToSampleProvider();
        }
        else
        {
            _samples = _reader;
        }

        _buffer = new float[8192 * 2];
    }

    /// <summary>読み込み進捗（0.0〜1.0）。</summary>
    public double Progress =>
        _reader.Length <= 0
            ? 1.0
            : Math.Clamp(_reader.Position / (double)_reader.Length, 0.0, 1.0);

    /// <summary>
    /// 最大 frameCount フレームを読み、Complex の L/R 配列を返します。
    /// </summary>
    /// <param name="frameCount">読み取る最大フレーム数。</param>
    /// <param name="left">左チャネル。</param>
    /// <param name="right">右チャネル。</param>
    /// <returns>1 フレーム以上読めたとき true。</returns>
    public bool TryRead(int frameCount, out Complex[] left, out Complex[] right)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        left = Array.Empty<Complex>();
        right = Array.Empty<Complex>();
        if (frameCount <= 0)
        {
            return false;
        }

        var need = frameCount * 2;
        if (need > _buffer.Length)
        {
            need = _buffer.Length;
            frameCount = need / 2;
        }

        var read = _samples.Read(_buffer, 0, need);
        if (read < 2)
        {
            return false;
        }

        var frames = read / 2;
        left = new Complex[frames];
        right = new Complex[frames];
        for (var i = 0; i < frames; i++)
        {
            left[i] = new Complex(_buffer[i * 2], 0.0);
            right[i] = new Complex(_buffer[(i * 2) + 1], 0.0);
        }

        return true;
    }

    /// <summary>
    /// リサンプラと音声ファイルリーダーを破棄します。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _resampler?.Dispose();
        _reader.Dispose();
    }
}
