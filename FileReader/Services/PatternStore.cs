using System.IO;
using System.Text.Json;
using FileReader.Models;

namespace FileReader.Services;

public sealed class PatternStore
{
    private readonly string path;
    public PatternStore(string? path = null) => this.path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileReader", "patterns.json");
    public List<AnalysisPattern> Load() => File.Exists(path)
        ? JsonSerializer.Deserialize<List<AnalysisPattern>>(File.ReadAllText(path)) ?? throw new InvalidDataException("パターンファイルが不正です。")
        : new();
    public void Save(List<AnalysisPattern> patterns)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(patterns, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
