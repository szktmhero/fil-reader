using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.Linq;
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
        public ObservableCollection<SearchCondition> SearchConditions { get; } = new ObservableCollection<SearchCondition>();
        
        public List<string> Operators { get; } = new List<string> 
        { 
            "LIKE (部分一致)", "LIKE (前方一致)", "=", "!=", "<", "<=", ">", ">=" 
        };
        
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
        public ICommand AddConditionCommand { get; }
        public ICommand RemoveConditionCommand { get; }
        public ICommand ExportCommand { get; }

        public LogTabViewModel(string filePath)
        {
            FilePath = filePath;
            FileName = Path.GetFileName(filePath);
            FileSizeString = FormatFileSize(filePath);

            _database = new LogDatabase();
            _database.Init();

            // デフォルトの検索条件を1つ追加
            SearchConditions.Add(new SearchCondition());

            SearchCommand = new RelayCommand(ExecuteSearch, () => CanSearch);
            CancelImportCommand = new RelayCommand(CancelImport, () => IsImporting);
            AddConditionCommand = new RelayCommand(AddCondition, () => CanSearch);
            RemoveConditionCommand = new RelayCommand<SearchCondition>(RemoveCondition, _ => CanSearch);
            ExportCommand = new RelayCommand(ExecuteExport, () => CanSearch && LogData != null && LogData.Count > 0);
        }

        private void AddCondition()
        {
            SearchConditions.Add(new SearchCondition());
        }

        private void RemoveCondition(SearchCondition? condition)
        {
            if (condition != null && SearchConditions.Count > 1)
            {
                SearchConditions.Remove(condition);
            }
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

                IsImporting = false;

                // DataGridに最初の表示データをバインド
                LogData = new VirtualizingList(_database, new List<SearchCondition>());
                
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
                // 検索結果バインド
                LogData = new VirtualizingList(_database, SearchConditions.ToList());
                
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

        private async void ExecuteExport()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "検索結果を保存",
                Filter = "元の形式 (自動)|*.*|CSVファイル (*.csv)|*.csv|TSVファイル (*.tsv)|*.tsv",
                FileName = $"export_{FileName}"
            };

            if (dialog.ShowDialog() == true)
            {
                string targetPath = dialog.FileName;
                string delimiter = "\t"; 
                bool writeHeader = true;

                if (targetPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) delimiter = ",";
                else if (targetPath.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase)) delimiter = "\t";
                else
                {
                    if (FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) delimiter = ",";
                    else if (FileName.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase)) delimiter = "\t";
                    else delimiter = "\t";
                }

                IsSearching = true;
                StatusMessage = "エクスポートの準備中...";

                var progress = new Progress<ProgressInfo>(info =>
                {
                    StatusMessage = info.Status;
                });

                try
                {
                    await _database.ExportSearchResultsAsync(targetPath, delimiter, writeHeader, SearchConditions.ToList(), progress, _cts.Token);
                    MessageBox.Show("エクスポートが完了しました。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    StatusMessage = $"エクスポートエラー: {ex.Message}";
                    MessageBox.Show($"保存中にエラーが発生しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    IsSearching = false;
                    StatusMessage = $"エクスポート完了: {LogData?.Count ?? 0:N0} 件該当";
                }
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
