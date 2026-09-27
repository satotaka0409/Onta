using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Onta.View.Core;

/// <summary>
/// 音声デバイスの動作サンプリング周波数を WASAPI のミックス形式から取得します。
/// WAV ファイルのサンプリング周波数はここを使わず 44100 Hz 固定です。
/// </summary>
internal static class AudioDeviceSampleRate
{
    private const uint DrvQueryFunctionInstanceId = 0x811;
    private const uint DrvQueryFunctionInstanceIdSize = 0x812;

    /// <summary>
    /// 入力デバイスのミックス形式サンプリング周波数を返します。取得できないときは fallbackRate です。
    /// </summary>
    /// <param name="deviceNumber">WaveIn デバイス番号（-1 は既定）。</param>
    /// <param name="fallbackRate">取得失敗時に使う周波数。</param>
    /// <returns>サンプリング周波数（Hz）。</returns>
    public static int ResolveCapture(int deviceNumber, int fallbackRate)
        => Resolve(deviceNumber, DataFlow.Capture, capture: true, fallbackRate);

    /// <summary>
    /// 指定した出力デバイスのミックス形式サンプリング周波数を返します。取得できないときは fallbackRate です。
    /// </summary>
    /// <param name="deviceNumber">WaveOut デバイス番号（-1 は既定）。</param>
    /// <param name="fallbackRate">取得失敗時に使う周波数。</param>
    /// <returns>サンプリング周波数（Hz）。</returns>
    public static int ResolveRender(int deviceNumber, int fallbackRate)
        => Resolve(deviceNumber, DataFlow.Render, capture: false, fallbackRate);

    /// <summary>
    /// 指定方向のエンドポイントからミックス形式の周波数を読みます。
    /// </summary>
    /// <param name="deviceNumber">MME デバイス番号（-1 は既定）。</param>
    /// <param name="flow">キャプチャまたはレンダー。</param>
    /// <param name="capture">true なら WaveIn 側のエンドポイント ID で照合します。</param>
    /// <param name="fallbackRate">取得失敗時の周波数。</param>
    /// <returns>サンプリング周波数（Hz）。</returns>
    private static int Resolve(int deviceNumber, DataFlow flow, bool capture, int fallbackRate)
    {
        var fallback = Math.Max(1, fallbackRate);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var rate = ReadMixRate(enumerator, deviceNumber, flow, capture);
            if (rate is >= 8000 and <= 384000)
            {
                return rate;
            }
        }
        catch
        {
            // デバイス列挙に失敗したときは呼び出し側のレートを使う。
        }

        return fallback;
    }

    /// <summary>
    /// MME デバイス番号に対応する WASAPI エンドポイントのミックス周波数を読みます。
    /// 番号を指定したときはそのデバイスだけを見ます。
    /// </summary>
    /// <param name="enumerator">デバイス列挙子。</param>
    /// <param name="deviceNumber">MME デバイス番号（-1 は既定）。</param>
    /// <param name="flow">キャプチャまたはレンダー。</param>
    /// <param name="capture">true なら WaveIn 側のエンドポイント ID で照合します。</param>
    /// <returns>ミックス形式のサンプリング周波数（Hz）。見つからなければ 0。</returns>
    private static int ReadMixRate(
        MMDeviceEnumerator enumerator,
        int deviceNumber,
        DataFlow flow,
        bool capture)
    {
        if (deviceNumber < 0)
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            return device.AudioClient.MixFormat.SampleRate;
        }

        var endpointId = QueryEndpointId(deviceNumber, capture);
        var product = Normalize(capture
            ? WaveIn.GetCapabilities(deviceNumber).ProductName
            : WaveOut.GetCapabilities(deviceNumber).ProductName);
        var endpoints = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        foreach (var endpoint in endpoints)
        {
            if (endpointId is not null)
            {
                if (string.Equals(endpoint.ID, endpointId, StringComparison.OrdinalIgnoreCase))
                {
                    return endpoint.AudioClient.MixFormat.SampleRate;
                }

                continue;
            }

            if (NamesMatch(product, Normalize(endpoint.FriendlyName)))
            {
                return endpoint.AudioClient.MixFormat.SampleRate;
            }
        }

        return 0;
    }

    /// <summary>
    /// WaveIn / WaveOut デバイス番号から WASAPI エンドポイント ID を取得します。
    /// </summary>
    /// <param name="deviceNumber">MME デバイス番号（0 以上）。</param>
    /// <param name="capture">true なら waveInMessage、false なら waveOutMessage。</param>
    /// <returns>エンドポイント ID。取得できなければ null。</returns>
    private static string? QueryEndpointId(int deviceNumber, bool capture)
    {
        var handle = (IntPtr)deviceNumber;
        int size;
        var sizeRc = capture
            ? WaveInMessageSize(handle, DrvQueryFunctionInstanceIdSize, out size, IntPtr.Zero)
            : WaveOutMessageSize(handle, DrvQueryFunctionInstanceIdSize, out size, IntPtr.Zero);
        if (sizeRc != 0 || size is <= 0 or > 2048)
        {
            return null;
        }

        var id = new StringBuilder(Math.Max(size / 2, 1));
        var idRc = capture
            ? WaveInMessageId(handle, DrvQueryFunctionInstanceId, id, (IntPtr)size)
            : WaveOutMessageId(handle, DrvQueryFunctionInstanceId, id, (IntPtr)size);
        if (idRc != 0)
        {
            return null;
        }

        var text = id.ToString().Trim().TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Wave 製品名と WASAPI 表示名が同一デバイスかを、切り詰めを考慮して判定します。
    /// </summary>
    /// <param name="product">MME 製品名。</param>
    /// <param name="friendly">WASAPI 表示名。</param>
    /// <returns>一致すれば true。</returns>
    private static bool NamesMatch(string product, string friendly)
    {
        if (product.Length == 0 || friendly.Length == 0)
        {
            return false;
        }

        return friendly.StartsWith(product, StringComparison.OrdinalIgnoreCase)
            || product.StartsWith(friendly, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// デバイス名の末尾ヌルと空白を除きます。
    /// </summary>
    /// <param name="name">生のデバイス名。</param>
    /// <returns>正規化した名前。空なら空文字。</returns>
    private static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        return name.Trim().TrimEnd('\0').Trim();
    }

    /// <summary>
    /// waveOutMessage でバッファサイズを問い合わせます。
    /// </summary>
    [DllImport("winmm.dll", EntryPoint = "waveOutMessage")]
    private static extern int WaveOutMessageSize(IntPtr device, uint message, out int size, IntPtr unused);

    /// <summary>
    /// waveOutMessage でエンドポイント ID 文字列を取得します。
    /// </summary>
    [DllImport("winmm.dll", EntryPoint = "waveOutMessage", CharSet = CharSet.Unicode)]
    private static extern int WaveOutMessageId(IntPtr device, uint message, StringBuilder id, IntPtr sizeBytes);

    /// <summary>
    /// waveInMessage でバッファサイズを問い合わせます。
    /// </summary>
    [DllImport("winmm.dll", EntryPoint = "waveInMessage")]
    private static extern int WaveInMessageSize(IntPtr device, uint message, out int size, IntPtr unused);

    /// <summary>
    /// waveInMessage でエンドポイント ID 文字列を取得します。
    /// </summary>
    [DllImport("winmm.dll", EntryPoint = "waveInMessage", CharSet = CharSet.Unicode)]
    private static extern int WaveInMessageId(IntPtr device, uint message, StringBuilder id, IntPtr sizeBytes);
}
