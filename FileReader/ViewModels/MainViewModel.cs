using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Configuration;
using FileReader.Models;

namespace FileReader.ViewModels
{
    public class MainViewModel : ViewModelBase, IDisposable
    {
        private ObservableCollection<LogTabViewModel> _tabs = new();
        private LogTabViewModel? _selectedTab;
        private LogFormatConfig _formatConfig = new();

        public ObservableCollection<LogTabViewModel> Tabs
        {
            get => _tabs;
            set => SetProperty(ref _tabs, value);
        }

        public LogTabViewModel? SelectedTab
        {
            get => _selectedTab;
            set => SetProperty(ref _selectedTab, value);
        }

        public ICommand OpenFileCommand { get; }
        public ICommand CloseTabCommand { get; }

        public MainViewModel()
        {
            OpenFileCommand = new RelayCommand<string>(ExecuteOpenFile);
            CloseTabCommand = new RelayCommand<LogTabViewModel>(ExecuteCloseTab);

            // アプリ起動時に過去の残存キャッシュDBをクリーンアップ (F-07)
            CleanupResidualCaches();

            // 設定ファイルのロード (F-01)
            LoadConfiguration();
        }

        private void LoadConfiguration()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                if (File.Exists(configPath))
                {
                    var builder = new ConfigurationBuilder()
                        .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                        .AddJsonFile("config.json", optional: true, reloadOnChange: true);
                    
                    var configuration = builder.Build();
                    
                    // 手動でパースするか、Getでバインド
                    var formats = new System.Collections.Generic.List<LogFormat>();
                    var section = configuration.GetSection("LogFormats");
                    foreach (var child in section.GetChildren())
                    {
                        var format = new LogFormat
                        {
                            Extension = child["Extension"] ?? string.Empty,
                            Delimiter = child["Delimiter"] ?? ",",
                            HasHeader = bool.Parse(child["HasHeader"] ?? "true")
                        };

                        var columnsSection = child.GetSection("Columns");
                        if (columnsSection.Exists())
                        {
                            format.Columns = columnsSection.GetChildren().Select(c => c.Value ?? "").ToList();
                        }
                        
                        formats.Add(format);
                    }
                    _formatConfig.LogFormats = formats;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"設定ファイル (config.json) の読み込み中にエラーが発生しました。デフォルト設定を使用します。\n{ex.Message}", 
                                "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public void ExecuteOpenFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return;

            // 既に同じファイルを開いているかチェック
            var existingTab = Tabs.FirstOrDefault(t => t.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
            if (existingTab != null)
            {
                SelectedTab = existingTab;
                return;
            }

            // 拡張子からフォーマットを決定 (F-02)
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            var format = _formatConfig.LogFormats.FirstOrDefault(f => f.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase));
            
            if (format == null)
            {
                // デフォルトのフォーマット (カンマ区切り、ヘッダー有)
                format = new LogFormat
                {
                    Extension = ext,
                    Delimiter = ",",
                    HasHeader = true
                };
            }

            var newTab = new LogTabViewModel(filePath);
            Tabs.Add(newTab);
            SelectedTab = newTab;

            // 非同期インポート開始 (F-03)
            newTab.StartImport(format);
        }

        private void ExecuteCloseTab(LogTabViewModel tab)
        {
            if (tab == null) return;
            
            Tabs.Remove(tab);
            tab.Dispose(); // これにより一時DBが削除される (F-07)
        }

        private void CleanupResidualCaches()
        {
            try
            {
                string tempDir = Path.GetTempPath();
                string[] residualDbs = Directory.GetFiles(tempDir, "FileReader_*.db");
                foreach (var dbFile in residualDbs)
                {
                    try
                    {
                        File.Delete(dbFile);
                    }
                    catch
                    {
                        // 使用中の一時DBは削除できないのでスキップしてOK
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Warning] Failed to cleanup residual caches: {ex.Message}");
            }
        }

        public void Dispose()
        {
            foreach (var tab in Tabs)
            {
                tab.Dispose();
            }
            Tabs.Clear();
        }
    }
}
