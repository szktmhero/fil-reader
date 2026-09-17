using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FileReader.Models;
using FileReader.Services;
using FileReader.ViewModels;
using Microsoft.Win32;

namespace FileReader;

// A modal analysis workspace keeps the source tab/database alive while querying.
public sealed class AnalysisWindow : Window
{
    private readonly LogTabViewModel source;
    private readonly PatternStore store = new();
    private List<AnalysisPattern> patterns = new();
    private readonly ComboBox saved = new() { MinWidth = 210, DisplayMemberPath = "" };
    private readonly TextBox name = new() { Width = 180 };
    private readonly TextBox sql = new() { AcceptsReturn = true, AcceptsTab = true, Height = 125, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), Text = "SELECT * FROM log_data LIMIT 1000;" };
    private readonly ComboBox group = new() { Width = 160 };
    private readonly ComboBox measure = new() { Width = 160 };
    private readonly ComboBox aggregate = new() { Width = 100, ItemsSource = new[] { "COUNT", "SUM", "AVG", "MIN", "MAX" }, SelectedIndex = 0 };
    private readonly ComboBox chartType = new() { Width = 120, ItemsSource = new[] { "棒グラフ", "折れ線グラフ" }, SelectedIndex = 0 };
    private readonly ComboBox x = new() { Width = 160 };
    private readonly ComboBox y = new() { Width = 160 };
    private readonly DataGrid grid = new() { IsReadOnly = true, AutoGenerateColumns = true, CanUserAddRows = false, EnableRowVirtualization = true };
    private readonly Canvas chart = new() { Background = Brushes.White, MinHeight = 200, ClipToBounds = true };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4), Text = "テーブル: log_data。SQL・簡易集計はファイル全体が対象です。元画面の検索条件は適用されません。絞り込みはSQLのWHEREで指定してください。" };
    private readonly StackPanel controls = new();
    private CancellationTokenSource? running;
    private DataTable? result;
    private string restoredX = "", restoredY = "";
    private bool storeHealthy = true;

    public AnalysisWindow(LogTabViewModel source)
    {
        this.source = source;
        Title = $"分析・パターン — {source.FileName}";
        Width = 1100; Height = 850; MinWidth = 850; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.WhiteSmoke; Foreground = Brushes.Black;
        var root = new DockPanel { Margin = new Thickness(12) };
        Content = root;
        DockPanel.SetDock(controls, Dock.Top); root.Children.Add(controls);
        controls.Children.Add(Row(Label("パターン"), saved, Button("呼び出す", LoadPattern), Button("削除", DeletePattern)));
        controls.Children.Add(Row(Label("名前"), name, Button("SQL＋グラフを保存", () => SavePattern("SQL")), Button("現在の検索条件を保存", () => SavePattern("検索条件"))));
        controls.Children.Add(new TextBlock { Text = "SQL（SELECT / WITH）　列: " + string.Join(", ", source.DataColumns), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        controls.Children.Add(sql);
        group.ItemsSource = new[] { "（グループなし）" }.Concat(source.DataColumns).ToList(); group.SelectedIndex = 0;
        measure.ItemsSource = source.DataColumns; measure.SelectedIndex = 0;
        controls.Children.Add(Row(Label("簡易集計"), group, aggregate, measure, Button("SQLを作成", BuildAggregate)));
        controls.Children.Add(Row(Button("実行", async () => await Execute()), Button("表示結果をCSV出力", Export)));
        controls.Children.Add(Row(Label("グラフ X"), x, Label("Y（数値）"), y, chartType, Button("グラフを表示", DrawChart)));
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(Button("実行を中断", () => running?.Cancel())); footer.Children.Add(status);
        var tabs = new TabControl(); root.Children.Add(tabs);
        tabs.Items.Add(new TabItem { Header = "結果テーブル", Content = grid });
        tabs.Items.Add(new TabItem { Header = "グラフ", Content = chart });
        chart.SizeChanged += (_, _) => { if (result != null) DrawChart(); };
        x.SelectionChanged += (_, _) => DrawChart();
        y.SelectionChanged += (_, _) => DrawChart();
        chartType.SelectionChanged += (_, _) => DrawChart();
        Closing += (_, e) => { if (running != null) { running.Cancel(); e.Cancel = true; status.Text = "中断しています。完了後に閉じてください。"; } };
        try { patterns = store.Load(); RefreshPatterns(); }
        catch (Exception ex) { storeHealthy = false; status.Text = "パターンを読み込めません。既存ファイルを保護するため保存を停止しました: " + ex.Message; }
    }

    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) };
    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(4) };
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { MessageBox.Show(ex.Message, "分析", MessageBoxButton.OK, MessageBoxImage.Warning); } };
        return button;
    }
    private static WrapPanel Row(params UIElement[] elements)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 3, 0, 3) };
        foreach (var element in elements) row.Children.Add(element);
        return row;
    }
    private void RefreshPatterns() { saved.ItemsSource = null; saved.ItemsSource = patterns; }
    private static List<SearchCondition> CopyConditions(IEnumerable<SearchCondition> conditions) => conditions.Select(c => new SearchCondition { Column = c.Column, Operator = c.Operator, Keyword = c.Keyword }).ToList();

    private void SavePattern(string kind)
    {
        if (!storeHealthy) throw new InvalidOperationException("パターンファイルの読み込みエラーを解消してから再度開いてください。");
        string title = name.Text.Trim();
        if (title.Length == 0) throw new InvalidOperationException("パターン名を入力してください。");
        var existing = patterns.FirstOrDefault(p => p.Kind == kind && p.Name == title);
        if (existing != null && MessageBox.Show("同名のパターンを上書きしますか？", "パターン保存", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var pattern = new AnalysisPattern { Name = title, Kind = kind, Sql = sql.Text,
            Conditions = CopyConditions(source.SearchConditions), ChartType = chartType.SelectedItem?.ToString() ?? "棒グラフ",
            XColumn = x.SelectedItem?.ToString() ?? restoredX, YColumn = y.SelectedItem?.ToString() ?? restoredY };
        var updated = patterns.Where(p => p != existing).Append(pattern).ToList();
        store.Save(updated); patterns = updated; RefreshPatterns(); saved.SelectedItem = pattern;
        status.Text = "パターンを保存しました。別ファイル・次回起動時も呼び出せます。";
    }
    private void DeletePattern()
    {
        if (saved.SelectedItem is not AnalysisPattern pattern) return;
        if (MessageBox.Show($"「{pattern.Name}」を削除しますか？", "パターン削除", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var updated = patterns.Where(p => p != pattern).ToList(); store.Save(updated); patterns = updated; RefreshPatterns();
    }
    private void LoadPattern()
    {
        if (saved.SelectedItem is not AnalysisPattern pattern) return;
        name.Text = pattern.Name;
        if (pattern.Kind == "検索条件")
        {
            var missing = pattern.Conditions.Where(c => c.Column != "全列" && !source.DataColumns.Contains(c.Column)).Select(c => c.Column).Distinct();
            if (missing.Any()) throw new InvalidOperationException("このファイルに必要な列がありません: " + string.Join(", ", missing));
            source.SearchConditions.Clear(); foreach (var condition in CopyConditions(pattern.Conditions)) source.SearchConditions.Add(condition);
            if (source.SearchConditions.Count == 0) source.SearchConditions.Add(new SearchCondition());
            status.Text = "検索条件を復元しました。この画面を閉じ、条件を確認して検索してください。";
        }
        else
        {
            sql.Text = pattern.Sql; chartType.SelectedItem = pattern.ChartType;
            restoredX = pattern.XColumn; restoredY = pattern.YColumn;
            result = null; grid.ItemsSource = null; chart.Children.Clear();
            x.ItemsSource = null; y.ItemsSource = null;
            status.Text = "SQL・グラフ設定を復元しました。内容を確認して実行してください。列の不足は実行時に表示します。";
        }
    }
    private void BuildAggregate()
    {
        string function = aggregate.SelectedItem?.ToString() ?? "COUNT";
        string expression = function == "COUNT" ? "COUNT(*)" : $"{function}(safe_number(log_data.{AnalysisService.Quote(measure.SelectedItem?.ToString() ?? throw new InvalidOperationException("集計列を選んでください。"))}))";
        string? grouping = group.SelectedIndex > 0 ? "log_data." + AnalysisService.Quote(group.SelectedItem!.ToString()!) : null;
        sql.Text = grouping == null ? $"SELECT {expression} AS value FROM log_data;" : $"SELECT {grouping} AS category, {expression} AS value\nFROM log_data\nGROUP BY {grouping}\nORDER BY category;";
        status.Text = "集計SQLを作成しました。空欄はNULLとして扱い、不正な数値があればエラーにします。実行前にWHERE等を追加できます。";
    }
    private async Task Execute()
    {
        if (running != null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); running = cts;
        controls.IsEnabled = false; result = null; grid.ItemsSource = null; chart.Children.Clear(); status.Text = "実行中…（最大60秒）";
        string preferredX = x.SelectedItem?.ToString() ?? restoredX, preferredY = y.SelectedItem?.ToString() ?? restoredY;
        try
        {
            var response = await AnalysisService.ExecuteAsync(source.DatabasePath, sql.Text, cts.Token);
            result = response.Table; grid.ItemsSource = result.DefaultView;
            var columns = result.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
            x.ItemsSource = columns; y.ItemsSource = columns;
            x.SelectedItem = columns.Contains(preferredX) ? preferredX : columns.FirstOrDefault();
            y.SelectedItem = columns.Contains(preferredY) ? preferredY : columns.Skip(1).FirstOrDefault() ?? columns.FirstOrDefault();
            result.DefaultView.ListChanged += (_, _) => DrawChart();
            DrawChart();
            status.Text = $"{result.Rows.Count:N0}行。" + (response.Truncated ? "表示上限10,000行で打ち切りました。CSV・グラフも表示結果が対象です。" : "CSV・グラフはこの実行結果が対象です。")
                + ((preferredX.Length > 0 && !columns.Contains(preferredX)) || (preferredY.Length > 0 && !columns.Contains(preferredY)) ? " 保存されたグラフ列がないため選び直してください。" : "");
        }
        catch (OperationCanceledException) { status.Text = "中断しました（ユーザー操作または60秒の上限）。"; }
        catch (Exception ex) { status.Text = "SQL実行エラー: " + ex.Message; }
        finally { running = null; controls.IsEnabled = true; }
    }
    private void Export()
    {
        if (result == null) throw new InvalidOperationException("先にSQLを実行してください。");
        var dialog = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "analysis.csv" };
        if (dialog.ShowDialog(this) == true) { AnalysisService.Export(result, dialog.FileName); status.Text = $"表示結果 {result.Rows.Count:N0}行をCSVに出力しました。"; }
    }
    private void DrawChart()
    {
        chart.Children.Clear();
        if (result == null || x.SelectedItem is not string xc || y.SelectedItem is not string yc) return;
        if (result.DefaultView.Count == 0) { ChartText("結果がありません。", 20, 20); return; }
        if (result.DefaultView.Count > 200) { ChartText("グラフは200行までです。SQLで集計・絞り込みを行ってください。", 20, 20); return; }
        var points = new List<(string Label, double Value)>();
        try
        {
            foreach (DataRowView row in result.DefaultView)
            {
                var value = AnalysisService.Number(Convert.ToString(row[yc], CultureInfo.InvariantCulture));
                if (value == null) throw new InvalidOperationException("Y列にNULL・空欄があります。SQLで除外または補完してください。");
                points.Add((Convert.ToString(row[xc], CultureInfo.InvariantCulture) ?? "", value.Value));
            }
        }
        catch (Exception ex) { ChartText("グラフを描けません: " + ex.Message, 20, 20); return; }
        double width = chart.ActualWidth - 140, height = chart.ActualHeight - 105;
        if (width <= 0 || height <= 0) return;
        double min = Math.Min(0, points.Min(p => p.Value)), max = Math.Max(0, points.Max(p => p.Value));
        if (max == min) max = min + 1;
        double range = max - min;
        if (!double.IsFinite(range)) { ChartText("数値の範囲が大きすぎます。SQLで単位を調整してください。", 20, 20); return; }
        double Y(double value) => 35 + height * ((max - value) / range);
        double step = width / points.Count, baseline = Y(0);
        ChartText($"{yc} / {xc}（表の表示順・等間隔）", 75, 5);
        for (int tick = 0; tick <= 4; tick++)
        {
            double value = min + range * (tick / 4d); double py = Y(value);
            chart.Children.Add(new Line { X1 = 75, X2 = 75 + width, Y1 = py, Y2 = py, Stroke = Brushes.LightGray });
            ChartText(value.ToString("G4", CultureInfo.InvariantCulture), 0, py - 8);
        }
        var line = new Polyline { Stroke = Brushes.Teal, StrokeThickness = 2 };
        for (int i = 0; i < points.Count; i++)
        {
            double px = 75 + step * (i + .5), py = Y(points[i].Value);
            string tip = $"{points[i].Label}: {points[i].Value.ToString(CultureInfo.InvariantCulture)}";
            if (chartType.SelectedIndex == 0)
            {
                var bar = new Rectangle { Width = Math.Max(1, step * .7), Height = Math.Max(1, Math.Abs(baseline - py)), Fill = Brushes.Teal, ToolTip = tip };
                Canvas.SetLeft(bar, px - bar.Width / 2); Canvas.SetTop(bar, Math.Min(py, baseline)); chart.Children.Add(bar);
            }
            else
            {
                line.Points.Add(new Point(px, py));
                var dot = new Ellipse { Width = 6, Height = 6, Fill = Brushes.Teal, ToolTip = tip };
                Canvas.SetLeft(dot, px - 3); Canvas.SetTop(dot, py - 3); chart.Children.Add(dot);
            }
            if (i % Math.Max(1, (int)Math.Ceiling(points.Count / 10d)) == 0) ChartText(points[i].Label.Length > 12 ? points[i].Label[..12] + "…" : points[i].Label, px - 15, 40 + height);
        }
        if (chartType.SelectedIndex == 1) chart.Children.Add(line);
    }
    private void ChartText(string text, double left, double top)
    {
        var label = new TextBlock { Text = text, Foreground = Brushes.Black, FontSize = 12 };
        Canvas.SetLeft(label, left); Canvas.SetTop(label, top); chart.Children.Add(label);
    }
}
