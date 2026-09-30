using NAudio.Dsp;

namespace Onta.View.Core;

/// <summary>
/// チャンク単位で PCM を別サンプリング周波数へ変換します。
/// </summary>
internal sealed class StreamingPcmResampler
{
    private readonly WdlResampler _resampler = new();
    private readonly int _channels;
    private readonly int _inputRate;
    private readonly int _outputRate;
    private float[] _output = new float[4096];

    /// <summary>
    /// 入力レートから出力レートへの変換器を作ります。
    /// </summary>
    /// <param name="inputRate">入力サンプリング周波数。</param>
    /// <param name="outputRate">出力サンプリング周波数。</param>
    /// <param name="channels">チャネル数（1 または 2）。</param>
    public StreamingPcmResampler(int inputRate, int outputRate, int channels)
    {
        if (channels is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        _channels = channels;
        _inputRate = Math.Max(1, inputRate);
        _outputRate = Math.Max(1, outputRate);
        _resampler.SetMode(true, 0, true, 64, 32);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(true);
        _resampler.SetRates(_inputRate, _outputRate);
    }

    /// <summary>
    /// インターリーブされた入力を出力レートへ変換し、結果を destination へ追記します。
    /// </summary>
    /// <param name="interleaved">入力サンプル（チャネル順にインターリーブ）。</param>
    /// <param name="destination">変換結果の追記先。</param>
    public void Process(ReadOnlySpan<float> interleaved, List<float> destination)
    {
        if (interleaved.Length == 0 || interleaved.Length % _channels != 0)
        {
            return;
        }

        var offset = 0;
        while (offset < interleaved.Length)
        {
            var remainFrames = (interleaved.Length - offset) / _channels;
            var prepared = _resampler.ResamplePrepare(remainFrames, _channels, out var inBuffer, out var inOffset);
            if (prepared <= 0 || inBuffer is null)
            {
                return;
            }

            var frames = Math.Min(remainFrames, prepared);
            interleaved.Slice(offset, frames * _channels).CopyTo(inBuffer.AsSpan(inOffset, frames * _channels));
            offset += frames * _channels;

            var outFrames = (int)Math.Ceiling(frames * (_outputRate / (double)_inputRate)) + 16;
            var outSamples = outFrames * _channels;
            if (_output.Length < outSamples)
            {
                _output = new float[outSamples];
            }

            var produced = _resampler.ResampleOut(_output, 0, frames, outFrames, _channels);
            if (produced <= 0)
            {
                continue;
            }

            var producedSamples = produced * _channels;
            for (var i = 0; i < producedSamples; i++)
            {
                destination.Add(_output[i]);
            }
        }
    }
}
