namespace Onta.Core.Tests;

/// <summary>
/// テスト入出力パス（in_files / out_files）を解決します。
/// </summary>
internal static class TestPaths
{
    private const string InputFileName = "Sample1.png";
    private static readonly string TestProjectDir = LocateTestProjectDir();

    /// <summary>
    /// 既定の入力PNG（test/in_files/Sample1.png）を解決します。
    /// </summary>
    public static string ResolveInputPng()
    {
        var path = Path.Combine(TestProjectDir, "in_files", InputFileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"入力ファイルが見つかりません: {path}");
        }

        return path;
    }

    /// <summary>
    /// 出力ディレクトリ（test/out_files）を解決し、なければ作成します。
    /// </summary>
    public static string ResolveOutputDir()
    {
        var dir = Path.Combine(TestProjectDir, "out_files");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// out_files 配下の出力ファイルパスを返します。
    /// </summary>
    public static string ResolveOutputPath(string fileName)
    {
        return Path.Combine(ResolveOutputDir(), fileName);
    }

    /// <summary>
    /// `Onta_core.Tests.csproj` からテストプロジェクトディレクトリを探索します。
    /// </summary>
    private static string LocateTestProjectDir()
    {
        const string projectFile = "Onta_core.Tests.csproj";
        var known = @"C:\proj\Onta\Onta_core\test";
        if (File.Exists(Path.Combine(known, projectFile)))
        {
            return known;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, projectFile)))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"テストプロジェクト ディレクトリが見つかりません: {projectFile}");
    }
}

