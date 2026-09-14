using System.Numerics;

namespace Onta.Core;

public sealed partial class OfdmGenerator
{
    /// <summary>
    /// ScoreLock を実行します。
    /// </summary>
    /// <param name="samples">samples を指定します。</param>
    /// <param name="symbolCount">symbolCount を指定します。</param>
    /// <param name="useRightChannel">useRightChannel を指定します。</param>
    /// <param name="start">隧穂ｾ｡髢句ｧ九し繝ｳ繝励Ν菴咲ｽｮ縲・/param>
    public double ScoreLock(Complex[] samples, int start, int symbolCount, bool useRightChannel = false)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (symbolCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(symbolCount));
        }

        var symbolLength = SamplesPerOfdmSymbol;
        if (start < 0 || start + (symbolCount * symbolLength) > samples.Length)
        {
            return double.NegativeInfinity;
        }

        var pilotBins = useRightChannel ? _rightPilotBins : _leftPilotBins;
        var timeNoCp = _scoreTimeNoCpScratch;
        var freqBins = _scoreFreqBinsScratch;
        var score = 0.0;
        for (var s = 0; s < symbolCount; s++)
        {
            var symbolStart = start + (s * symbolLength);
            score += ScoreSingleSymbolLock(samples.AsSpan(symbolStart, symbolLength), pilotBins, timeNoCp, freqBins);
        }

        return score / symbolCount;
    }

    /// <summary>
    /// FindBestSymbolStart を実行します。
    /// </summary>
    /// <param name="samples">samples を指定します。</param>
    /// <param name="searchRadius">searchRadius を指定します。</param>
    /// <param name="useRightChannel">useRightChannel を指定します。</param>
    /// <param name="expectedStart">expectedStart を指定します。</param>
    public int FindBestSymbolStart(
        Complex[] samples,
        int expectedStart,
        int searchRadius,
        bool useRightChannel = false)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var symbolLength = SamplesPerOfdmSymbol;
        expectedStart = Math.Clamp(expectedStart, 0, Math.Max(0, samples.Length - symbolLength));
        if (searchRadius <= 0)
        {
            return expectedStart;
        }

        var pilotBins = useRightChannel ? _rightPilotBins : _leftPilotBins;
        var timeNoCp = _scoreTimeNoCpScratch;
        var freqBins = _scoreFreqBinsScratch;
        var back = Math.Min(searchRadius, expectedStart);
        var forward = Math.Min(searchRadius, samples.Length - symbolLength - expectedStart);
        var cpAtExpected = ScoreSingleSymbolCpLock(samples.AsSpan(expectedStart, symbolLength));
        var scoreAtExpected = ScoreSingleSymbolLock(samples.AsSpan(expectedStart, symbolLength), pilotBins, timeNoCp, freqBins);
        var bestDelta = 0;
        var bestScore = scoreAtExpected;
        var candidateCount = back + forward + 1;

        if (candidateCount <= 7)
        {
            for (var delta = -back; delta <= forward; delta++)
            {
                if (delta == 0)
                {
                    continue;
                }

                var score = ScoreSingleSymbolLock(samples.AsSpan(expectedStart + delta, symbolLength), pilotBins, timeNoCp, freqBins);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestDelta = delta;
                }
            }

            const double smallRangeLockMargin = 0.15;
            if (bestDelta != 0 && bestScore < scoreAtExpected + smallRangeLockMargin)
            {
                return expectedStart;
            }

            return expectedStart + bestDelta;
        }

        var topCandidateCount = Math.Min(candidateCount - 1, 4);
        Span<int> topDeltas = stackalloc int[topCandidateCount];
        Span<double> topCpScores = stackalloc double[topCandidateCount];
        for (var i = 0; i < topCandidateCount; i++)
        {
            topDeltas[i] = 0;
            topCpScores[i] = double.NegativeInfinity;
        }

        for (var delta = -back; delta <= forward; delta++)
        {
            if (delta == 0)
            {
                continue;
            }

            var cpScore = ScoreSingleSymbolCpLock(samples.AsSpan(expectedStart + delta, symbolLength));
            if (cpScore < cpAtExpected - 0.2)
            {
                continue;
            }

            if (cpScore <= topCpScores[^1])
            {
                continue;
            }

            var insertIndex = topCandidateCount - 1;
            while (insertIndex > 0 && cpScore > topCpScores[insertIndex - 1])
            {
                topCpScores[insertIndex] = topCpScores[insertIndex - 1];
                topDeltas[insertIndex] = topDeltas[insertIndex - 1];
                insertIndex--;
            }

            topCpScores[insertIndex] = cpScore;
            topDeltas[insertIndex] = delta;
        }

        for (var i = 0; i < topCandidateCount; i++)
        {
            var delta = topDeltas[i];
            if (delta == 0)
            {
                continue;
            }

            var score = ScoreSingleSymbolLock(samples.AsSpan(expectedStart + delta, symbolLength), pilotBins, timeNoCp, freqBins);
            if (score > bestScore)
            {
                bestScore = score;
                bestDelta = delta;
            }
        }

        const double lockMargin = 0.15;
        if (bestDelta != 0 && bestScore < scoreAtExpected + lockMargin)
        {
            return expectedStart;
        }

        return expectedStart + bestDelta;
    }
}
