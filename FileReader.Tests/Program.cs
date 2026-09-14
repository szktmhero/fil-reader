using System.Data;
using System.Diagnostics;
using FileReader.Models;
using FileReader.Services;
using Microsoft.Data.Sqlite;
using System.Text;

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
void RejectSave(PatternStore store, List<AnalysisPattern> patterns, string message)
{
    try { store.Save(patterns); }
    catch (IOException) { Check(true, message); return; }
    throw new Exception("Unexpected pattern overwrite: " + message);
}
async Task<LogDatabase> Import(string content, LogFormat? format = null, bool bom = false)
{
    string inputPath = Path.Combine(directory, Guid.NewGuid() + ".csv");
    File.WriteAllText(inputPath, content, new UTF8Encoding(bom));
    var database = new LogDatabase();
    try
    {
        database.Init();
        await database.ImportCsvAsync(inputPath, format ?? new LogFormat(), new Progress<ProgressInfo>(), CancellationToken.None);
        return database;
    }
    catch { database.Dispose(); throw; }
}
async Task RejectCsv(string content, string expectedError, string message)
{
    try { using var imported = await Import(content); }
    catch (InvalidDataException ex) when (ex.Message.Contains(expectedError)) { Check(true, message); return; }
    throw new Exception("Accepted invalid CSV: " + message);
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
    await Reject("SELECT \"missing_category\" AS category, COUNT(*) FROM log_data GROUP BY \"missing_category\"", "saved SQL rejects missing quoted group columns on the bundled SQLite");
    var quoted = await AnalysisService.ExecuteAsync(path, "SELECT log_data.\"category\", COUNT(*) FROM log_data GROUP BY log_data.\"category\"", CancellationToken.None);
    Check(quoted.Table.Rows.Count == 2, "qualified quoted columns still group correctly");
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
    string patternPath = Path.Combine(directory, "patterns.json");
    var stale = new PatternStore(patternPath); var stalePatterns = stale.Load();
    restored.Add(new AnalysisPattern { Name = "追加", Sql = "SELECT 2" });
    store.Save(restored);
    string latest = File.ReadAllText(patternPath);
    RejectSave(stale, stalePatterns, "stale store cannot overwrite another instance's saved patterns");
    Check(File.ReadAllText(patternPath) == latest, "conflict preserves the current file exactly");
    var refreshed = stale.Load(); refreshed.RemoveAt(0); stale.Save(refreshed);
    Check(new PatternStore(patternPath).Load().Count == 2, "reloading after conflict allows intentional deletion");
    var lockedStore = new PatternStore(patternPath); var lockedPatterns = lockedStore.Load();
    using (var held = new FileStream(patternPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        RejectSave(lockedStore, lockedPatterns, "exclusive lock prevents simultaneous writers");
    lockedStore.Save(lockedPatterns);
    Check(new PatternStore(patternPath).Load().Count == 2, "save recovers after the lock is released");
    File.WriteAllText(Path.Combine(directory, "patterns.json"), "broken");
    try { store.Load(); throw new Exception("Invalid JSON silently ignored"); } catch (System.Text.Json.JsonException) { Check(true, "corrupt patterns are reported"); }
    foreach (string invalid in new[] { "[null]", "[{\"Name\":\"bad\",\"Conditions\":null}]", "[{\"Name\":\"bad\",\"ChartType\":\"unknown\"}]" })
    {
        File.WriteAllText(patternPath, invalid);
        try { store.Load(); throw new Exception("Invalid pattern structure accepted"); }
        catch (InvalidDataException) { Check(true, "invalid pattern structure is rejected: " + invalid); }
    }
    try { store.Save(new()); throw new Exception("Saved after failed load"); }
    catch (InvalidDataException) { Check(true, "failed load prevents overwriting the malformed file"); }
    var csv = new DataTable(); csv.Columns.Add("quoted\"header"); csv.Rows.Add("a,\"b\"\nc");
    AnalysisService.Export(csv, Path.Combine(directory, "result.csv"));
    Check(File.ReadAllText(Path.Combine(directory, "result.csv")).Contains("\"a,\"\"b\"\"\nc\""), "CSV export escapes commas, quotes and newlines");
    var sorted = new DataTable(); sorted.Columns.Add("label"); sorted.Columns.Add("value", typeof(int));
    sorted.Rows.Add("A", 1); sorted.Rows.Add("B", 3); sorted.Rows.Add("C", 2);
    sorted.DefaultView.Sort = "value DESC";
    AnalysisService.Export(sorted, Path.Combine(directory, "sorted.csv"));
    Check(File.ReadAllLines(Path.Combine(directory, "sorted.csv")).Skip(1).SequenceEqual(new[] { "\"B\",\"3\"", "\"C\",\"2\"", "\"A\",\"1\"" }), "CSV follows the DataGrid view sort order");
    string input = Path.Combine(directory, "input.csv");
    File.WriteAllText(input, "category,amount,description\nA,3,\"hello, world\"\nB,4,\"two\nlines\"\n");
    using (var imported = new LogDatabase())
    {
        imported.Init(); await imported.ImportCsvAsync(input, new LogFormat { HasHeader = true, Delimiter = "," }, new Progress<ProgressInfo>(), CancellationToken.None);
        var rows = await AnalysisService.ExecuteAsync(imported.DbFilePath, "SELECT * FROM log_data ORDER BY rowid", CancellationToken.None);
        Check(rows.Table.Rows.Count == 2 && (string)rows.Table.Rows[1]["description"] == "two\nlines", "CSV imports quoted delimiters and multiline values faithfully");
        string exported = Path.Combine(directory, "all-columns.csv");
        AnalysisService.Export(rows.Table, exported);
        using var reimported = await Import(File.ReadAllText(exported));
        var page = reimported.GetPage(0, 10, new());
        Check(page.Count == 2 && page[0]["rowid"] == "1" && page[1]["description"] == "two\nlines", "SELECT star CSV round-trips with the exported rowid column");
    }
    using (var ids = await Import("rowid,__file_reader_rowid_1,amount\n99,original,7\n2,second,8\n"))
    {
        await ids.CreateIndexesAsync(new Progress<string>());
        var page = ids.GetPage(0, 10, new());
        Check(page[0]["rowid"] == "99" && page[1]["rowid"] == "2" && page[0]["__rowid__"] == "1", "input rowids stay data and paging keeps import order even with fallback-name collisions");
    }
    string special = "\"quoted\"\"header\",\"a,b\",\"bracket]name\",\"two\nlines\"\r\n日本語,\"x,y\",z,4\r\n";
    using (var headers = await Import(special, bom: true))
    {
        await headers.CreateIndexesAsync(new Progress<string>());
        var conditions = new List<SearchCondition> { new() { Column = "bracket]name", Operator = "=", Keyword = "z" } };
        Check(headers.GetTotalCount(conditions) == 1 && headers.GetPage(0, 10, conditions)[0]["quoted\"header"] == "日本語", "special headers support import, indexes, search and paging");
        string exportPath = Path.Combine(directory, "search-export.csv");
        await headers.ExportSearchResultsAsync(exportPath, ",", true, conditions, new Progress<ProgressInfo>(), CancellationToken.None);
        using var roundTrip = await Import(File.ReadAllText(exportPath));
        Check(roundTrip.Columns.SequenceEqual(headers.Columns) && roundTrip.GetPage(0, 10, new())[0]["a,b"] == "x,y", "search CSV round-trips quoted, delimited and multiline headers");
    }
    using (var noHeader = await Import("日本語\t10\r\n次\t20\r\n", new LogFormat { HasHeader = false, Delimiter = "\t" }, bom: true))
        Check(noHeader.GetPage(0, 10, new())[0]["Column_1"] == "日本語" && noHeader.GetTotalCount(new()) == 2, "headerless BOM TSV keeps the first value and infers columns");
    await RejectCsv("a,b\n1,2,3\n", "2行目", "extra CSV fields fail with a row number");
    await RejectCsv("a,b\n1\n", "2行目", "missing CSV fields fail with a row number");
    await RejectCsv("a,b\n1,\"unclosed\n", "引用符", "malformed CSV quoting fails explicitly");
    await RejectCsv("a-b,a.b\n1,2\n", "重複", "colliding normalized headers are reported");
    Console.WriteLine($"{checks} checks passed.");
}
finally { Directory.Delete(directory, true); }
