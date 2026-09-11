using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace FileReader.Services;

public sealed record QueryResult(DataTable Table, bool Truncated);

public static class AnalysisService
{
    public const int RowLimit = 10000;
    public static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    // Strip literals/comments before checking statement boundaries. Do not mistake
    // semicolons in strings or quoted identifiers for additional statements.
    public static void ValidateSql(string sql)
    {
        var tokens = Regex.Replace(sql, "'(''|[^'])*'|\"(\"\"|[^\"])*\"|`(``|[^`])*`|\\[[^\\]]*\\]|--[^\\r\\n]*|/\\*[\\s\\S]*?\\*/", " ");
        tokens = tokens.Trim();
        if (tokens.EndsWith(';')) tokens = tokens[..^1].TrimEnd();
        if (tokens.Contains(';') || !Regex.IsMatch(tokens, @"\A(SELECT|WITH)\b", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("SELECT / WITH による単一の問い合わせを入力してください。");
    }

    public static double? Number(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return number;
        throw new FormatException($"数値に変換できません: {value}（桁区切り・通貨記号は除いてください）");
    }

    public static Task<QueryResult> ExecuteAsync(string path, string sql, CancellationToken token) => Task.Run(() =>
    {
        ValidateSql(sql);
        token.ThrowIfCancellationRequested();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        connection.CreateFunction<string?, double?>("safe_number", Number);
        using (var guard = connection.CreateCommand())
        {
            guard.CommandText = "PRAGMA query_only=ON;";
            guard.ExecuteNonQuery();
        }
        // This registration is disposed before the connection. Interrupt also stops
        // aggregate/sort queries which have not produced their first row yet.
        using var registration = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var table = new DataTable();
        try
        {
            using var reader = command.ExecuteReader();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                string label = reader.GetName(i);
                if (string.IsNullOrEmpty(label)) label = $"Column{i + 1}";
                string unique = label;
                for (int n = 2; table.Columns.Contains(unique); n++) unique = $"{label}_{n}";
                table.Columns.Add(unique, typeof(object));
            }
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (table.Rows.Count == RowLimit) return new QueryResult(table, true);
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                table.Rows.Add(values);
            }
            token.ThrowIfCancellationRequested();
            return new QueryResult(table, false);
        }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }, token);

    public static void Export(DataTable table, string path)
    {
        static string Escape(object value) => "\"" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"") + "\"";
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine(string.Join(",", table.Columns.Cast<DataColumn>().Select(c => Escape(c.ColumnName))));
        foreach (DataRow row in table.Rows) writer.WriteLine(string.Join(",", row.ItemArray.Select(v => Escape(v ?? ""))));
    }
}
