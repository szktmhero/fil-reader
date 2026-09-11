using System.Data;
using System.Diagnostics;
using FileReader.Models;
using FileReader.Services;
using Microsoft.Data.Sqlite;

string directory = Path.Combine(Path.GetTempPath(), "FileReader-tests-" + Guid.NewGuid());
Directory.CreateDirectory(directory);
string path = Path.Combine(directory, "test.db");
int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
async Task Reject(string sql, string message)
{
    try { await AnalysisService.ExecuteAsync(path, sql, CancellationToken.None); }
    catch (Exception ex) when (ex is InvalidOperationException or SqliteException) { Check(true, message); return; }
    throw new Exception("Accepted: " + sql);
}
try
{
    using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
    {
        db.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE log_data(category TEXT, amount TEXT); INSERT INTO log_data VALUES ('A','10.5'),('A','2.5'),('B','-5'),('B','');"; cmd.ExecuteNonQuery();
    }
    var sum = await AnalysisService.ExecuteAsync(path, "SELECT category, SUM(safe_number(amount)) AS total FROM log_data GROUP BY category ORDER BY category", CancellationToken.None);
    Check(sum.Table.Rows.Count == 2 && Convert.ToDouble(sum.Table.Rows[0][1]) == 13 && Convert.ToDouble(sum.Table.Rows[1][1]) == -5, "group sums, negative values and blanks");
    await Reject("DELETE FROM log_data", "reject writes");
    await Reject("WITH a AS (SELECT 1) DELETE FROM log_data", "read-only database rejects CTE writes");
    await Reject("SELECT 1; DROP TABLE log_data", "reject multiple statements");
    await Reject("ATTACH DATABASE 'other.db' AS other", "reject attach");
    await Reject("SELECT safe_number('oops')", "invalid numeric input fails instead of becoming zero");
    var literal = await AnalysisService.ExecuteAsync(path, "-- comment\nSELECT ';DROP TABLE' AS x, 2 AS x /* ; */;", CancellationToken.None);
    Check(literal.Table.Columns[1].ColumnName == "x_2", "quoted semicolons, comments and duplicate result columns");
    var cap = await AnalysisService.ExecuteAsync(path, "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<10001) SELECT x FROM n", CancellationToken.None);
    Check(cap.Truncated && cap.Table.Rows.Count == 10000, "result cap reports truncation");
    using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
    {
        var clock = Stopwatch.StartNew();
        try { await AnalysisService.ExecuteAsync(path, "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<1000000000) SELECT SUM(x) FROM n", cancel.Token); throw new Exception("Cancellation failed"); }
        catch (OperationCanceledException) { Check(clock.Elapsed < TimeSpan.FromSeconds(5), "interrupt aggregate before first result row"); }
    }
    var store = new PatternStore(Path.Combine(directory, "patterns.json"));
    store.Save(new() { new AnalysisPattern { Name = "日別売上", Sql = "SELECT 1", XColumn = "日", YColumn = "合計", ChartType = "折れ線グラフ" }, new AnalysisPattern { Name = "エラー", Kind = "検索条件", Conditions = new() { new SearchCondition { Column = "category", Operator = "=", Keyword = "A" } } } });
    var restored = new PatternStore(Path.Combine(directory, "patterns.json")).Load();
    Check(restored.Count == 2 && restored[0].YColumn == "合計" && restored[1].Conditions[0].Keyword == "A", "persist SQL, chart settings and search conditions across store instances");
    File.WriteAllText(Path.Combine(directory, "patterns.json"), "broken");
    try { store.Load(); throw new Exception("Invalid JSON silently ignored"); } catch (System.Text.Json.JsonException) { Check(true, "corrupt patterns are reported"); }
    var csv = new DataTable(); csv.Columns.Add("quoted\"header"); csv.Rows.Add("a,\"b\"\nc");
    AnalysisService.Export(csv, Path.Combine(directory, "result.csv"));
    Check(File.ReadAllText(Path.Combine(directory, "result.csv")).Contains("\"a,\"\"b\"\"\nc\""), "CSV export escapes commas, quotes and newlines");
    string input = Path.Combine(directory, "input.csv");
    File.WriteAllText(input, "category,amount,description\nA,3,\"hello, world\"\nB,4,\"two\nlines\"\n");
    using (var imported = new LogDatabase())
    {
        imported.Init(); await imported.ImportCsvAsync(input, new LogFormat { HasHeader = true, Delimiter = "," }, new Progress<ProgressInfo>(), CancellationToken.None);
        var rows = await AnalysisService.ExecuteAsync(imported.DbFilePath, "SELECT * FROM log_data ORDER BY rowid", CancellationToken.None);
        Check(rows.Table.Rows.Count == 2 && (string)rows.Table.Rows[1]["description"] == "two\nlines", "CSV imports quoted delimiters and multiline values faithfully");
    }
    Console.WriteLine($"{checks} checks passed.");
}
finally { Directory.Delete(directory, true); }
