# FileReader - 大容量ログ・CSVビューア (ベータ版)

`FileReader` は、10GBクラス（数千万〜数億行）の超大容量なログファイルやCSV・TSV・DATファイルを、メモリ不足（OOM）や画面のフリーズを起こすことなく高速に閲覧・キーワード検索できるWindowsデスクトップアプリケーション（WPF）です。

---

## 🚀 主な機能と特徴

1. **超大容量データのハンドリング (10GB+ 対応)**
   - ファイルをメモリに一括ロードするのではなく、一時SQLiteデータベースへストリーミング（バルクインサート）し、表示・検索に必要な分だけをオンデマンドで取得します。
2. **データ仮想化 (Data Virtualization) x UI仮想化 (UI Virtualization)**
   - WPF標準の `DataGrid` 仮想化機能と連携するカスタム `VirtualizingList` (ページキャッシュ機能付き) を実装。
   - 数億行あってもメモリ使用量は **500MB以下** に抑えられ、スクロールも非常に滑らかです。
3. **設定ファイルによる区切り文字の動的対応**
   - アプリ配置フォルダ内の設定ファイル `config.json` にて、対象ファイルの拡張子、区切り文字（カンマ `,`、タブ `\t`、パイプ `|` など）、ヘッダーの有無、列定義を柔軟に設定可能。
4. **高速キーワード検索**
   - インポート完了後、バックグラウンドスレッドで自動的に全列にインデックス（`CREATE INDEX`）を構築。
   - インデックスが効く「前方一致のみ（超高速）」や、任意の列指定検索、全列に対する曖昧検索（`LIKE '%word%'`）に対応。
5. **洗練されたダークテーマ UI**
   - ガラス調（Glassmorphism）の要素、グラデーションを活かしたモダンで高級感のあるダークモード。
   - 直感的なドラッグ＆ドロップによるインポートエリア、複数ファイルを同時に展開できるタブベースUI。
   - インポート中の処理速度（行数/秒）や進捗をリアルタイム表示。

---

## 🛠 システム要件

- **OS:** Windows 10 / 11
- **ランタイム:** .NET 9.0 SDK または .NET 9.0 ランタイム (WPFサポート)
- **依存パッケージ:**
  - `Microsoft.Data.Sqlite` (SQLite接続)
  - `CsvHelper` (高速CSV/TSVパース)
  - `Microsoft.Extensions.Configuration.Json` (JSON設定ロード)

---

## 📂 設定方法 (`config.json`)

アプリケーション実行フォルダ（またはプロジェクトフォルダ）の `config.json` にて、ログの拡張子や区切り文字を定義します。

```json
{
  "LogFormats": [
    {
      "Extension": ".csv",
      "Delimiter": ",",
      "HasHeader": true
    },
    {
      "Extension": ".tsv",
      "Delimiter": "\t",
      "HasHeader": true
    },
    {
      "Extension": ".log",
      "Delimiter": "\t",
      "HasHeader": false,
      "Columns": [ "Timestamp", "Level", "Logger", "Message" ]
    },
    {
      "Extension": ".dat",
      "Delimiter": "|",
      "HasHeader": false,
      "Columns": [ "Id", "Name", "Value", "Timestamp" ]
    }
  ]
}
```

- **`Extension`**: ファイルの拡張子（小文字）。
- **`Delimiter`**: 区切り文字。タブは `\t`、パイプは `|` など。
- **`HasHeader`**: ファイルの1行目を列ヘッダーとして扱う場合は `true`、そうでない場合は `false`。
- **`Columns`**: `HasHeader` が `false` の場合に適用されるデフォルトの列名リスト。指定しない場合は自動的に `Column_1, Column_2...` が生成されます。

---

## 💻 起動・ビルド手順

### 1. ビルド
プロジェクトルートディレクトリ（ソリューションファイルがある場所）で以下を実行します。

```bash
dotnet build
```

### 2. アプリケーションの実行
```bash
dotnet run --project FileReader/FileReader.csproj
```

---

## 🏗 技術設計と内部アーキテクチャ

```
[ログファイル]
   │
   ▼ (CsvHelper: ストリーミングパース)
[SQLite 一時データベース (temp_logs_*.db)]
   │
   ├─► (バックグラウンド: CREATE INDEX による検索最適化)
   │
   ▼ (オンデマンド取得: LIMIT 100 OFFSET ... / ページングキャッシュ)
[VirtualizingList: IList]
   │
   ▼ (UI仮想化: DataGrid recycling)
[WPF DataGrid] (メモリ消費極小・超高速スクロール)
```

1. **高速インポート:**
   `SQLiteTransaction` を用いて、50,000行単位でまとめてコミットすることで、ディスクへのI/O負荷を劇的に下げ、数千万行を数分でDB化します。
2. **破損行対策 (堅牢性):**
   列数が足りない行や不正なデータがある場合でも、パースエラーによるクラッシュを防ぐため、該当部分を空文字として補完し、全体の読み込みを続行します。
3. **自動クリーンアップ:**
   タブを閉じた際、またはアプリケーションを正常・異常終了した際の一時DBファイルは自動的に物理削除されます。また、次回起動時に前回の残存ファイルを自動検知してクリーンアップします。
