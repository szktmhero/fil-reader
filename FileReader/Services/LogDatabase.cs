using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using CsvHelper;
using CsvHelper.Configuration;
using FileReader.Models;

namespace FileReader.Services
{
    public class ProgressInfo
    {
        public long LoadedRows { get; set; }
        public double RowsPerSecond { get; set; }
        public double Percent { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    public class LogDatabase : IDisposable
    {
        private readonly string _dbFilePath;
        private readonly string _connectionString;
        private SqliteConnection? _connection;
        private readonly List<string> _columns = new();
        private bool _isIndexed = false;
        private bool _isDisposed = false;

        public List<string> Columns => _columns;
        public string DbFilePath => _dbFilePath;
        public bool IsIndexed => _isIndexed;

        public LogDatabase()
        {
            // 一時DBファイルのパスを作成
            string tempDir = Path.GetTempPath();
            string fileName = $"FileReader_{Guid.NewGuid():N}.db";
            _dbFilePath = Path.Combine(tempDir, fileName);
            _connectionString = $"Data Source={_dbFilePath};Pooling=False;";
        }

        public void Init()
        {
            _connection = new SqliteConnection(_connectionString);
            _connection.Open();

            // SQLite高速化のための設定
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "PRAGMA synchronous = OFF; PRAGMA journal_mode = WAL; PRAGMA temp_store = MEMORY; PRAGMA cache_size = -200000; PRAGMA mmap_size = 268435456;";
            cmd.ExecuteNonQuery();
        }

        public async Task ImportCsvAsync(string filePath, LogFormat format, IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                var fileInfo = new FileInfo(filePath);
                long totalBytes = fileInfo.Length;
                long processedBytes = 0;

                // CsvHelper設定
                var config = new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    Delimiter = format.Delimiter,
                    HasHeaderRecord = format.HasHeader,
                    BadDataFound = context =>
                    {
                        // 破損行エラーを検知してもスキップして継続
                        Debug.WriteLine($"[Warning] Bad data found at: {context.RawRecord}");
                    },
                    MissingFieldFound = (args) =>
                    {
                        // フィールド不足も許容
                    }
                };

                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: false);
                // ストリームバイト数を監視するためのラッパー
                using var progressStream = new ProgressReportingStream(fileStream, bytesRead =>
                {
                    processedBytes += bytesRead;
                });

                using var reader = new StreamReader(progressStream, Encoding.UTF8);
                using var csv = new CsvReader(reader, config);

                if (!csv.Read())
                {
                    throw new Exception("ファイルが空か、読み込み不可能です。");
                }

                // ヘッダーまたはカラムの決定
                _columns.Clear();
                if (format.HasHeader)
                {
                    csv.ReadHeader();
                    string[]? headers = csv.HeaderRecord;
                    if (headers != null)
                    {
                        int emptyColIndex = 1;
                        foreach (var header in headers)
                        {
                            string colName = string.IsNullOrWhiteSpace(header) ? $"Column_{emptyColIndex++}" : header;
                            // SQLiteのカラム名として安全な名前にサニタイズ
                            colName = colName.Replace(" ", "_").Replace("-", "_").Replace(".", "_");
                            _columns.Add(colName);
                        }
                    }
                }
                
                // ヘッダーがない、または取得できなかった場合
                if (_columns.Count == 0)
                {
                    if (format.Columns != null && format.Columns.Count > 0)
                    {
                        _columns.AddRange(format.Columns);
                    }
                    else
                    {
                        // 1行目をパースしてカラム数を推測
                        int fieldCount = csv.Parser.Count;
                        if (fieldCount == 0) fieldCount = 10; // デフォルト
                        for (int i = 1; i <= fieldCount; i++)
                        {
                            _columns.Add($"Column_{i}");
                        }
                    }
                }

                // テーブル作成SQLの組み立て
                var createTableSql = new StringBuilder();
                createTableSql.Append("CREATE TABLE log_data (rowid INTEGER PRIMARY KEY");
                foreach (var col in _columns)
                {
                    createTableSql.Append($", [{col}] TEXT");
                }
                createTableSql.Append(");");

                using (var cmd = _connection!.CreateCommand())
                {
                    cmd.CommandText = createTableSql.ToString();
                    cmd.ExecuteNonQuery();
                }

                // インサート用SQLの準備
                var insertSql = new StringBuilder();
                insertSql.Append("INSERT INTO log_data (");
                insertSql.Append(string.Join(", ", _columns.ConvertAll(c => $"[{c}]")));
                insertSql.Append(") VALUES (");
                insertSql.Append(string.Join(", ", _columns.ConvertAll(c => $"@{c}")));
                insertSql.Append(");");

                using var insertCmd = _connection.CreateCommand();
                insertCmd.CommandText = insertSql.ToString();
                
                // パラメータを事前に準備
                var parameters = new List<SqliteParameter>();
                foreach (var col in _columns)
                {
                    var param = new SqliteParameter($"@{col}", SqliteType.Text);
                    insertCmd.Parameters.Add(param);
                    parameters.Add(param);
                }

                long rowCount = 0;
                var stopwatch = Stopwatch.StartNew();
                var lastReportStopwatch = Stopwatch.StartNew();
                
                SqliteTransaction transaction = _connection.BeginTransaction();
                insertCmd.Transaction = transaction;

                const int batchSize = 100000;

                try
                {
                    // 最初のレコード（既にRead()したもの）を処理
                    if (!format.HasHeader)
                    {
                        InsertRecord(csv, insertCmd, parameters);
                        rowCount++;
                    }

                    while (csv.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        InsertRecord(csv, insertCmd, parameters);
                        rowCount++;

                        if (rowCount % batchSize == 0)
                        {
                            transaction.Commit();
                            transaction.Dispose();

                            transaction = _connection.BeginTransaction();
                            insertCmd.Transaction = transaction;

                            // 定期的に進捗報告
                            if (lastReportStopwatch.ElapsedMilliseconds > 200)
                            {
                                double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                                double rowsPerSec = elapsedSec > 0 ? rowCount / elapsedSec : 0;
                                double percent = totalBytes > 0 ? (double)processedBytes / totalBytes * 100 : 0;
                                progress.Report(new ProgressInfo
                                {
                                    LoadedRows = rowCount,
                                    RowsPerSecond = rowsPerSec,
                                    Percent = Math.Min(percent, 99.9),
                                    Status = $"インポート中 ({rowCount:N0} 行)..."
                                });
                                lastReportStopwatch.Restart();
                            }
                        }
                    }

                    transaction.Commit();
                }
                catch (Exception)
                {
                    try { transaction.Rollback(); } catch { }
                    throw;
                }
                finally
                {
                    transaction.Dispose();
                }

                stopwatch.Stop();
                progress.Report(new ProgressInfo
                {
                    LoadedRows = rowCount,
                    RowsPerSecond = stopwatch.Elapsed.TotalSeconds > 0 ? rowCount / stopwatch.Elapsed.TotalSeconds : 0,
                    Percent = 100,
                    Status = $"インポート完了! 計 {rowCount:N0} 行 ({stopwatch.Elapsed.TotalSeconds:F2}秒)"
                });

            }, cancellationToken);
        }

        private void InsertRecord(CsvReader csv, SqliteCommand cmd, List<SqliteParameter> parameters)
        {
            int parseCount = csv.Parser.Count;
            int limit = Math.Min(_columns.Count, parseCount);

            for (int i = 0; i < limit; i++)
            {
                parameters[i].Value = csv.GetField(i) ?? string.Empty;
            }

            for (int i = limit; i < _columns.Count; i++)
            {
                parameters[i].Value = string.Empty;
            }

            cmd.ExecuteNonQuery();
        }

        public async Task CreateIndexesAsync(IProgress<string> progress)
        {
            if (_connection == null || _columns.Count == 0) return;

            await Task.Run(() =>
            {
                progress.Report("インデックス構築を開始します...");
                int count = 0;
                foreach (var col in _columns)
                {
                    progress.Report($"インデックスを作成中: {col} ({++count}/{_columns.Count})");
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = $"CREATE INDEX IF NOT EXISTS [idx_log_{col}] ON log_data ([{col}]);";
                    cmd.ExecuteNonQuery();
                }
                _isIndexed = true;
                progress.Report("インデックス構築が完了しました。検索が高速化されました。");
            });
        }

        public int GetTotalCount(string? filterColumn, string? filterKeyword, bool searchPrefixOnly)
        {
            if (_connection == null) return 0;

            using var cmd = _connection.CreateCommand();
            var sql = new StringBuilder("SELECT COUNT(*) FROM log_data");
            BuildFilterQuery(sql, cmd, filterColumn, filterKeyword, searchPrefixOnly);
            
            cmd.CommandText = sql.ToString();
            var obj = cmd.ExecuteScalar();
            return obj != null ? Convert.ToInt32(obj) : 0;
        }

        public List<Dictionary<string, string>> GetPage(int offset, int limit, string? filterColumn, string? filterKeyword, bool searchPrefixOnly)
        {
            var results = new List<Dictionary<string, string>>();
            if (_connection == null) return results;

            using var cmd = _connection.CreateCommand();
            var sql = new StringBuilder("SELECT rowid, * FROM log_data");
            BuildFilterQuery(sql, cmd, filterColumn, filterKeyword, searchPrefixOnly);
            
            // rowidで並べることで、インポート順の表示を保証し、スクロールを安定化
            sql.Append($" ORDER BY rowid LIMIT {limit} OFFSET {offset}");
            
            cmd.CommandText = sql.ToString();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, string>();
                // rowidも追加
                row["__rowid__"] = reader["rowid"].ToString() ?? "";
                foreach (var col in _columns)
                {
                    row[col] = reader[col]?.ToString() ?? string.Empty;
                }
                results.Add(row);
            }

            return results;
        }

        private void BuildFilterQuery(StringBuilder sql, SqliteCommand cmd, string? filterColumn, string? filterKeyword, bool searchPrefixOnly)
        {
            if (string.IsNullOrWhiteSpace(filterKeyword)) return;

            sql.Append(" WHERE ");
            string likePattern = searchPrefixOnly ? $"{filterKeyword}%" : $"%{filterKeyword}%";

            if (!string.IsNullOrEmpty(filterColumn) && _columns.Contains(filterColumn))
            {
                // 特定列での絞り込み
                sql.Append($"[{filterColumn}] LIKE @keyword");
                cmd.Parameters.AddWithValue("@keyword", likePattern);
            }
            else
            {
                // 全列での絞り込み
                var orParts = new List<string>();
                int paramIndex = 0;
                foreach (var col in _columns)
                {
                    string paramName = $"@keyword_{paramIndex++}";
                    orParts.Add($"[{col}] LIKE {paramName}");
                    cmd.Parameters.AddWithValue(paramName, likePattern);
                }
                sql.Append(string.Join(" OR ", orParts));
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_connection != null)
            {
                try
                {
                    _connection.Close();
                    _connection.Dispose();
                }
                catch { }
            }

            // 一時DBファイルを物理削除
            if (File.Exists(_dbFilePath))
            {
                try
                {
                    File.Delete(_dbFilePath);
                    Debug.WriteLine($"[Info] Deleted temporary DB file: {_dbFilePath}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Warning] Failed to delete temporary DB file: {ex.Message}");
                }
            }

            GC.SuppressFinalize(this);
        }

        ~LogDatabase()
        {
            Dispose();
        }
    }

    /// <summary>
    /// ストリーミング中の読み込みバイト数をフックするためのカスタムストリームクラス
    /// </summary>
    public class ProgressReportingStream : Stream
    {
        private readonly Stream _baseStream;
        private readonly Action<int> _onBytesRead;

        public ProgressReportingStream(Stream baseStream, Action<int> onBytesRead)
        {
            _baseStream = baseStream;
            _onBytesRead = onBytesRead;
        }

        public override bool CanRead => _baseStream.CanRead;
        public override bool CanSeek => _baseStream.CanSeek;
        public override bool CanWrite => _baseStream.CanWrite;
        public override long Length => _baseStream.Length;
        public override long Position
        {
            get => _baseStream.Position;
            set => _baseStream.Position = value;
        }

        public override void Flush() => _baseStream.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = _baseStream.Read(buffer, offset, count);
            if (bytesRead > 0)
            {
                _onBytesRead(bytesRead);
            }
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin) => _baseStream.Seek(offset, origin);
        public override void SetLength(long value) => _baseStream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _baseStream.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _baseStream.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
