using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using FileReader.ViewModels;

namespace FileReader
{
    public partial class MainWindow : Window
    {
        private MainViewModel ViewModel => (MainViewModel)DataContext;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }

        private void OpenFileButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "ログ・CSVファイルを開く",
                Filter = "大容量ログファイル (*.csv;*.tsv;*.log;*.dat)|*.csv;*.tsv;*.log;*.dat|すべてのファイル (*.*)|*.*",
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                ViewModel.ExecuteOpenFile(dialog.FileName);
            }
        }

        // ドラッグ＆ドロップ処理
        private void Border_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                if (sender is Border border)
                {
                    border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D37"));
                    border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#06B6D4"));
                }
            }
            e.Handled = true;
        }

        private void Border_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Border border)
            {
                border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E24"));
                border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46"));
            }
        }

        private void Border_Drop(object sender, DragEventArgs e)
        {
            if (sender is Border border)
            {
                border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E24"));
                border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46"));
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    // 最初のファイルを開く
                    ViewModel.ExecuteOpenFile(files[0]);
                }
            }
        }

        // テキストボックスでEnter押下時に検索実行
        private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return || e.Key == Key.Enter)
            {
                if (DataContext is MainViewModel mainVm && mainVm.SelectedTab != null)
                {
                    if (mainVm.SelectedTab.SearchCommand.CanExecute(null))
                    {
                        mainVm.SelectedTab.SearchCommand.Execute(null);
                    }
                }
                e.Handled = true;
            }
        }

        // タブ切り替え時の処理（プレースホルダー等にテキストを入れるなど、必要なら）
        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 特になし
        }

        // DataGrid の DataContext が切り替わったときにカラムを動的に生成する
        private void DataGrid_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is DataGrid dataGrid)
            {
                // 既存のイベントリスナーを解除するため、一旦購読解除のクリーンアップ処理を行う
                if (e.OldValue is LogTabViewModel oldVm)
                {
                    oldVm.PropertyChanged -= Vm_PropertyChanged;
                }

                if (e.NewValue is LogTabViewModel newVm)
                {
                    newVm.PropertyChanged += Vm_PropertyChanged;
                    GenerateColumns(dataGrid, newVm);
                }
                else
                {
                    dataGrid.Columns.Clear();
                }
            }
        }

        private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Columns が更新された、またはインポート完了した際にカラムを再構成する
            if (e.PropertyName == nameof(LogTabViewModel.Columns) || e.PropertyName == nameof(LogTabViewModel.LogData))
            {
                if (sender is LogTabViewModel vm)
                {
                    // ビジュアルツリーを探索して DataGrid を見つける
                    // （より堅牢にするため、WPFのDispatcher経由で実行）
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // 画面内の DataGrid を探す
                        var dataGrid = FindVisualChild<DataGrid>(this);
                        if (dataGrid != null && dataGrid.DataContext == vm)
                        {
                            GenerateColumns(dataGrid, vm);
                        }
                    }));
                }
            }
        }

        private void GenerateColumns(DataGrid dataGrid, LogTabViewModel vm)
        {
            dataGrid.Columns.Clear();
            
            // "全列" は検索対象でありカラムではないので除外する
            var colNames = vm.Columns.Where(c => c != "全列").ToList();
            if (colNames.Count == 0) return;

            foreach (var col in colNames)
            {
                var textColumn = new DataGridTextColumn
                {
                    Header = col,
                    Binding = new Binding($"[{col}]"),
                    Width = new DataGridLength(1, DataGridLengthUnitType.Auto)
                };

                // 特定のカラム（MessageやLogなど）を広めにする
                if (col.Equals("Message", StringComparison.OrdinalIgnoreCase) || 
                    col.Equals("Log", StringComparison.OrdinalIgnoreCase) || 
                    col.Equals("Details", StringComparison.OrdinalIgnoreCase))
                {
                    textColumn.Width = new DataGridLength(3, DataGridLengthUnitType.Star);
                }

                dataGrid.Columns.Add(textColumn);
            }
        }

        // ビジュアルツリー内から特定型の子要素を再帰的に検索するヘルパー
        private T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child != null && child is T tChild)
                {
                    return tChild;
                }
                
                var childOfChild = FindVisualChild<T>(child!);
                if (childOfChild != null)
                {
                    return childOfChild;
                }
            }
            return null;
        }
    }
}