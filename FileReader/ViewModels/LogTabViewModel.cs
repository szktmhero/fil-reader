using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using FileReader.Collections;
using FileReader.Models;
using FileReader.Services;

namespace FileReader.ViewModels
{
    public class LogTabViewModel : ViewModelBase, IDisposable
    {
        private readonly LogDatabase _database;
        private readonly CancellationTokenSource _cts = new();
        
        private string _fileName = string.Empty;
        private string _filePath = string.Empty;
        private string _fileSizeString = string.Empty;
        
        private string _statusMessage = "準備完了";
        private double _progressPercent = 0;
        private bool _isImporting = true;
        private bool _isIndexed = false;
        private string _indexStatus = "インデックス未作成";

        private List<string> _columns = new();
        private string? _selectedColumn = "全列";
        private string _searchKeyword = string.Empty;
        private bool _searchPrefixOnly = false; // 前方一致（デフォルトは部分一致）
        
        private VirtualizingList? _logData;
        private bool _isSearching = false;

        public string FileName
        {
            get => _fileName;
            set => SetProperty(ref _fileName, value);
        }

        public string FilePath
        {
            get => _filePath;
            set => SetProperty(ref _filePath, value);
        }

        public string FileSizeString
        {
            get => _fileSizeString;
            set => SetProperty(ref _fileSizeString, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public double ProgressPercent
        {
            get => _progressPercent;
            set => SetProperty(ref _progressPercent, value);
        }

        public bool IsImporting
        {
            get => _isImporting;
            set
            {
                if (SetProperty(ref _isImporting, value))
                {
                    OnPropertyChanged(nameof(CanSearch));
                }
            }
        }

        public bool IsIndexed
        {
            get => _isIndexed;
            set => SetProperty(ref _isIndexed, value);
        }

        public string IndexStatus
        {
            get => _indexStatus;
            set => SetProperty(ref _indexStatus, value);
        }

        public List<string> Columns
        {
            get => _columns;
            set => SetProperty(ref _columns, value);
        }

        public string? SelectedColumn
        {
            get => _selectedColumn;
            set => SetProperty(ref _selectedColumn, value);
        }

        public string SearchKeyword
        {
            get => _searchKeyword;
            set => SetProperty(ref _searchKeyword, value);
        }

        public bool SearchPrefixOnly
        {
            get => _searchPrefixOnly;
            set => SetProperty(ref _searchPrefixOnly, value);
        }

        public VirtualizingList? LogData
        {
            get => _logData;
            private set => SetProperty(ref _logData, value);
        }

        public bool IsSearching
        {
            get => _isSearching;
            set
            {
                if (SetProperty(ref _isSearching, value))
                {
                    OnPropertyChanged(nameof(CanSearch));
                }
            }
        }

        public bool CanSearch => !IsImporting && !IsSearching;

        public ICommand SearchCommand { get; }
        public ICommand CancelImportCommand { get; }

        public LogTabViewModel(string filePath)
        {
            FilePath = filePath;
            FileName = Path.GetFileName(filePath);
            FileSizeString = FormatFileSize(filePath);

            _database = new LogDatabase();
            _database.Init();

            SearchCommand = new RelayCommand(ExecuteSearch, () => CanSearch);
            CancelImportCommand = new RelayCommand(CancelImport, () => IsImporting);
        }

        public async void StartImport(LogFormat format)
        {
            var progress = new Progress<ProgressInfo>(info =>
            {
                StatusMessage = info.Status;
                ProgressPercent = info.Percent;
            });

            try
            {
                await _database.ImportCsvAsync(FilePath, format, progress, _cts.Token);
                
                // インポート成功後、カラムを読み込み
                var cols = new List<string> { "全列" };
                cols.AddRange(_database.Columns);
                Columns = cols;
                SelectedColumn = "全列";

                IsImporting = false;

                // DataGridに最初の表示データをバインド
                LogData = new VirtualizingList(_database, null, null, false);
                
                // バックグラウンドでインデックスの構築を開始
                _ = Task.Run(() => BuildIndexesAsync());
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "読み込みがキャンセルされました。";
                IsImporting = false;
            }
            catch (Exception ex)
            {
                StatusMessage = $"エラー: {ex.Message}";
                IsImporting = false;
                MessageBox.Show($"ファイルのインポート中にエラーが発生しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task BuildIndexesAsync()
        {
            var indexProgress = new Progress<string>(msg =>
            {
                IndexStatus = msg;
            });

            try
            {
                await _database.CreateIndexesAsync(indexProgress);
                IsIndexed = true;
            }
            catch (Exception ex)
            {
                IndexStatus = $"インデックス構築エラー: {ex.Message}";
            }
        }

        private void ExecuteSearch()
        {
            if (LogData == null) return;

            IsSearching = true;
            StatusMessage = "検索中...";

            try
            {
                string? filterCol = SelectedColumn == "全列" ? null : SelectedColumn;
                
                // 検索結果バインド
                LogData = new VirtualizingList(_database, filterCol, SearchKeyword, SearchPrefixOnly);
                
                int count = LogData.Count; // Countへのアクセスにより SQLite で SELECT COUNT(*) が実行される
                StatusMessage = $"検索完了: {count:N0} 件該当";
            }
            catch (Exception ex)
            {
                StatusMessage = $"検索エラー: {ex.Message}";
                MessageBox.Show($"検索中にエラーが発生しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsSearching = false;
            }
        }

        private void CancelImport()
        {
            _cts.Cancel();
        }

        private string FormatFileSize(string path)
        {
            if (!File.Exists(path)) return "0 B";
            long bytes = new FileInfo(path).Length;

            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double number = bytes;
            while (number >= 1024 && i < suffixes.Length - 1)
            {
                number /= 1024;
                i++;
            }
            return $"{number:F2} {suffixes[i]}";
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            _database.Dispose();
        }
    }
}
