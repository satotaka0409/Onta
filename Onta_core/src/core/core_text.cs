using System.Globalization;

namespace Onta.Core;

/// <summary>
/// コア（変復調・性能測定・ストリーム）が作って画面に出る文言を、現在の UI カルチャに応じて返します。
/// </summary>
public static class CoreText
{
    private static bool IsJapanese =>
        string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "ja", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// UI カルチャに応じて日本語または英語の文言を選びます。
    /// </summary>
    /// <param name="ja">日本語の文言。</param>
    /// <param name="en">英語の文言。</param>
    /// <returns>UI カルチャが日本語なら ja、それ以外は en。</returns>
    private static string T(string ja, string en) => IsJapanese ? ja : en;

    public static string NotReceived => T("(未受信)", "(Not received)");
    public static string Unknown => T("(不明)", "(Unknown)");
    public static string RealtimeResyncRun => T("(リアルタイム受信 / 再同期)", "(Live receive / Resync)");

    public static string SegmentPreamble => T("プリアンブル", "Preamble");
    public static string SegmentFileHeader => T("ファイルヘッダ", "File Header");
    public static string SegmentTotal => T("合計", "Total");

    public static string InputFileNotFound => T("入力ファイルが見つかりません。", "Input file not found.");
    public static string MonoNeedsOneChannelWav => T("モノラル送信には 1ch WAV が必要です。", "Mono transmission requires a 1-channel WAV.");
    public static string StereoNeedsTwoChannelWav => T("ステレオ送信には L/R の 2ch WAV が必要です。", "Stereo transmission requires a 2-channel (L/R) WAV.");
    public static string FhMissingSavedAsUnknownBlocks =>
        T("FH未受信。BH+BD を未完了ブロックとして保存しました。", "No file header received. BH+BD were saved as unknown blocks.");

    /// <summary>
    /// BD 受信中に信号が消えたブロックのエラー文言を返します。
    /// </summary>
    /// <param name="blockIndex">ブロック番号。</param>
    /// <returns>表示用文言。</returns>
    public static string BlockSignalLost(long blockIndex) =>
        T($"BLK-{blockIndex} 受信レベル低下（BD 受信中に信号が消えました）", $"BLK-{blockIndex} Signal lost (the signal disappeared while receiving BD)");

    /// <summary>
    /// ブロックの復号で例外が出たときのエラー文言を返します。
    /// </summary>
    /// <param name="blockIndex">ブロック番号。</param>
    /// <param name="message">例外メッセージ。</param>
    /// <returns>表示用文言。</returns>
    public static string BlockDecodeError(long blockIndex, string message) =>
        T($"BLK-{blockIndex} デコードエラー: {message}", $"BLK-{blockIndex} Decode error: {message}");

    /// <summary>
    /// BD の復号に失敗したブロックのエラー文言を返します。
    /// </summary>
    /// <param name="blockIndex">ブロック番号。</param>
    /// <param name="pass">インターリーブのパス番号。</param>
    /// <param name="local">パス内の送信順。</param>
    /// <returns>表示用文言。</returns>
    public static string BlockBdFailed(long blockIndex, int pass, int local) =>
        T($"BLK-{blockIndex} BD受信失敗 (pass {pass}, local {local})", $"BLK-{blockIndex} BD receive failed (pass {pass}, local {local})");

    /// <summary>
    /// 親が分からないまま BD の復号に失敗し、破棄したブロックのエラー文言を返します。
    /// </summary>
    /// <param name="blockIndex">ブロック番号。</param>
    /// <param name="pass">インターリーブのパス番号。</param>
    /// <param name="local">パス内の送信順。</param>
    /// <returns>表示用文言。</returns>
    public static string BlockBdFailedOwnerUnknown(long blockIndex, int pass, int local) =>
        T($"BLK-{blockIndex} BD受信失敗（親不明・破棄） (pass {pass}, local {local})",
          $"BLK-{blockIndex} BD receive failed (owner unknown, discarded) (pass {pass}, local {local})");

    /// <summary>
    /// FH 未受信のまま BD の復号に失敗したブロックのエラー文言を返します。
    /// </summary>
    /// <param name="blockIndex">ブロック番号。</param>
    /// <returns>表示用文言。</returns>
    public static string BlockBdFailedFhMissing(long blockIndex) =>
        T($"BLK-{blockIndex} BD受信失敗（FH未受信）", $"BLK-{blockIndex} BD receive failed (no file header)");

    /// <summary>
    /// 親の分からないブロックを不明ブロックとして保存したときの詳細文言を返します。
    /// </summary>
    /// <param name="blockIndex">ブロック番号。</param>
    /// <param name="pass">インターリーブのパス番号。</param>
    /// <param name="local">パス内の送信順。</param>
    /// <returns>表示用文言。</returns>
    public static string OrphanBlockDetail(long blockIndex, int pass, int local) =>
        T($"孤立ブロック index={blockIndex} (pass {pass}, local {local})", $"Orphan block index={blockIndex} (pass {pass}, local {local})");

    /// <summary>
    /// ペイロードのハッシュ一致で親ブロックを特定したときの文言を返します（先頭の ORPHAN-RESOLVED は画面が識別に使う）。
    /// </summary>
    /// <param name="ownerBlockIndex">特定した親ブロック番号。</param>
    /// <param name="pass">インターリーブのパス番号。</param>
    /// <param name="local">パス内の送信順。</param>
    /// <returns>表示用文言。</returns>
    public static string OrphanResolvedByHash(long ownerBlockIndex, int pass, int local) =>
        T($"ORPHAN-RESOLVED hash一致で BLK-{ownerBlockIndex} に再割当 (pass {pass}, local {local})",
          $"ORPHAN-RESOLVED reassigned to BLK-{ownerBlockIndex} by hash match (pass {pass}, local {local})");

    /// <summary>
    /// ヘッダーの復号に失敗したときの例外文言を返します。
    /// </summary>
    /// <param name="sample">復号を試した位置（サンプル）。</param>
    /// <param name="payloadLength">ヘッダーのペイロード長（バイト）。</param>
    /// <returns>表示用文言。</returns>
    public static string HeaderDecodeFailed(long sample, int payloadLength) =>
        T($"ヘッダーの復号に失敗しました。sample={sample}, payload={payloadLength}。プリアンブル長のずれ、またはワウ・ノイズが大きい可能性があります。",
          $"Failed to decode the header. sample={sample}, payload={payloadLength}. The preamble length may be off, or wow/flutter or noise may be too large.");

    public static string PerformanceRxRun => T("性能測定受信", "Performance RX");
    public static string PerformanceTxRun => T("性能測定送信", "Performance TX");
    public static string SpecifyWavFile => T("WAV ファイルを指定してください。", "Please specify a WAV file.");
    public static string TxCompleted => T("送信完了", "Transmission completed");
    public static string TxAborted => T("送信を中断しました。", "Transmission was aborted.");

    public static string OpusDllNotFound =>
        T("opus.dll が見つかりません。Onta_core/native/opus/<rid>/opus.dll を配置してください。",
          "opus.dll not found. Place it at Onta_core/native/opus/<rid>/opus.dll.");
}
