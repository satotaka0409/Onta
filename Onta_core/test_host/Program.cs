using Onta.Core.Tests;

namespace Onta.Core.TestHost;

/// <summary>
/// デバッガ用ホストです。vstest を介さずテストメソッドを直接実行し、ブレークポイントを効かせます。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var target = args.Length > 0 ? args[0] : "1";
        Console.WriteLine($"Onta TestHost: running test {target} under debugger...");

        try
        {
            switch (target)
            {
                case "1":
                    new OntaTest1().EncodeDecode_QrPng_MatchesOriginal_Stereo9ScQpsk();
                    break;
                case "2":
                    new OntaTest2().EncodeDecode_QrPng_MatchesOriginal_Stereo36Sc64Qam();
                    break;
                case "3":
                    new OntaTest3().EncodeDecode_QrPng_MatchesOriginal_Stereo18Sc16Qam_WithNoiseAndWowFlutter();
                    break;
                case "3w":
                    new OntaTest3().EncodeDecode_QrPng_MatchesOriginal_Stereo18Sc16Qam_WowOnly();
                    break;
                case "4":
                    new OntaTest4().EncodeDecode_QrPng_MatchesOriginal_Mono27ScQpsk_WithNoiseAndWowFlutter();
                    break;
                default:
                    Console.Error.WriteLine("Usage: Onta_core.TestHost [1|2|3|3w|4]");
                    return 2;
            }

            Console.WriteLine("PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
