using System.IO;
using System.Text;
using System.Text.Json;
using FileReader.Models;

namespace FileReader.Services;

public sealed class PatternStore
{
    private readonly string path;
    private string? snapshot;
    private bool loadFailed;
    public PatternStore(string? path = null) => this.path = Path.GetFullPath(path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileReader", "patterns.json"));

    // The lock covers both comparison and replacement across application instances.
    // Keep the lock file: deleting it could allow two different lock handles on Unix.
    private FileStream Lock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("別の画面でパターンを読み書きしています。少し待って再操作してください。", ex); }
    }

    private string? ReadCurrent() => File.Exists(path) ? File.ReadAllText(path) : null;

    public List<AnalysisPattern> Load()
    {
        loadFailed = true;
        using var locked = Lock();
        string? current = ReadCurrent();
        var patterns = current == null ? new List<AnalysisPattern>() :
            JsonSerializer.Deserialize<List<AnalysisPattern>>(current) ?? throw new InvalidDataException("パターンファイルが不正です。");
        Validate(patterns);
        snapshot = current;
        loadFailed = false;
        return patterns;
    }

    private static void Validate(List<AnalysisPattern> patterns)
    {
        var names = new HashSet<(string Kind, string Name)>();
        foreach (var pattern in patterns)
        {
            if (pattern == null || string.IsNullOrWhiteSpace(pattern.Name) || pattern.Kind is not ("SQL" or "検索条件")
                || pattern.Sql == null || pattern.XColumn == null || pattern.YColumn == null
                || pattern.ChartType is not ("棒グラフ" or "折れ線グラフ") || pattern.Conditions == null
                || pattern.Conditions.Any(c => c == null || c.Column == null || c.Keyword == null
                    || c.Operator is not ("LIKE (部分一致)" or "LIKE (前方一致)" or "=" or "!=" or "<" or "<=" or ">" or ">="))
                || !names.Add((pattern.Kind, pattern.Name)))
                throw new InvalidDataException("パターンの内容が不正です。名前・種類・検索条件・グラフ設定を確認してください。");
        }
    }

    public void Save(List<AnalysisPattern> patterns)
    {
        if (loadFailed) throw new InvalidDataException("パターンの読み込みエラーを解消してから再度開いてください。");
        Validate(patterns);
        using var locked = Lock();
        if (!string.Equals(snapshot, ReadCurrent(), StringComparison.Ordinal))
            throw new IOException("パターンが別の画面で変更されました。上書きせず停止しました。分析画面を開き直してから保存してください。");
        string content = JsonSerializer.Serialize(patterns, new JsonSerializerOptions { WriteIndented = true });
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, true);
            snapshot = content;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
