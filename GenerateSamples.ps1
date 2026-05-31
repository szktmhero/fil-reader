param (
    [int]$Lines = 10000000,
    [string]$Type = "all"
)

$source = @"
using System;
using System.IO;
using System.Text;
using System.Diagnostics;

public class FastGenerator {
    private static readonly string[] Levels = { "INFO", "WARN", "ERROR", "DEBUG" };
    private static readonly string[] Loggers = { "Application.Main", "Database.Pool", "Auth.Service", "Network.Client", "Cache.Redis" };
    private static readonly string[] Messages = {
        "User authentication success for uid_{0}",
        "Database connection pool size reached max limit ({0})",
        "Slow query detected: SELECT * FROM items WHERE id = {0}",
        "Failed to fetch resource from url: http://api.internal/resource/{0}",
        "Cache hit for key: user_profile_id_{0}",
        "Finished batch processing of job_{0} in {1}ms"
    };

    public static void GenerateCsv(string path, int count) {
        Console.WriteLine(string.Format("[C#] Generating CSV with {0:N0} lines to {1}...", count, path));
        Stopwatch sw = Stopwatch.StartNew();
        Random rand = new Random(42);
        using (StreamWriter writer = new StreamWriter(path, false, Encoding.UTF8, 65536)) {
            writer.WriteLine("Id,Timestamp,Level,Component,Message");
            DateTime baseDate = DateTime.Now;
            for (int i = 1; i <= count; i++) {
                string lvl = Levels[rand.Next(Levels.Length)];
                string log = Loggers[rand.Next(Loggers.Length)];
                string msgTemplate = Messages[rand.Next(Messages.Length)];
                string msg = string.Format(msgTemplate, i, rand.Next(5, 1500));
                string ts = baseDate.AddSeconds(-i).ToString("yyyy-MM-dd HH:mm:ss.fff");
                writer.WriteLine(string.Format("{0},{1},{2},{3},\"{4}\"", i, ts, lvl, log, msg));
            }
        }
        sw.Stop();
        Console.WriteLine(string.Format("[C#] CSV generated in {0:F2} seconds.", sw.Elapsed.TotalSeconds));
    }

    public static void GenerateTsv(string path, int count) {
        Console.WriteLine(string.Format("[C#] Generating TSV with {0:N0} lines to {1}...", count, path));
        Stopwatch sw = Stopwatch.StartNew();
        Random rand = new Random(43);
        using (StreamWriter writer = new StreamWriter(path, false, Encoding.UTF8, 65536)) {
            writer.WriteLine("Id\tTimestamp\tLevel\tComponent\tMessage");
            DateTime baseDate = DateTime.Now;
            for (int i = 1; i <= count; i++) {
                string lvl = Levels[rand.Next(Levels.Length)];
                string log = Loggers[rand.Next(Loggers.Length)];
                string msgTemplate = Messages[rand.Next(Messages.Length)];
                string msg = string.Format(msgTemplate, i, rand.Next(5, 1500));
                string ts = baseDate.AddSeconds(-i).ToString("yyyy-MM-dd HH:mm:ss.fff");
                writer.WriteLine(string.Format("{0}\t{1}\t{2}\t{3}\t{4}", i, ts, lvl, log, msg));
            }
        }
        sw.Stop();
        Console.WriteLine(string.Format("[C#] TSV generated in {0:F2} seconds.", sw.Elapsed.TotalSeconds));
    }

    public static void GenerateLog(string path, int count) {
        Console.WriteLine(string.Format("[C#] Generating LOG with {0:N0} lines to {1}...", count, path));
        Stopwatch sw = Stopwatch.StartNew();
        Random rand = new Random(44);
        using (StreamWriter writer = new StreamWriter(path, false, Encoding.UTF8, 65536)) {
            DateTime baseDate = DateTime.Now;
            for (int i = 1; i <= count; i++) {
                string lvl = Levels[rand.Next(Levels.Length)];
                string log = Loggers[rand.Next(Loggers.Length)];
                string msgTemplate = Messages[rand.Next(Messages.Length)];
                string msg = string.Format(msgTemplate, i, rand.Next(5, 1500));
                string ts = baseDate.AddSeconds(-i).ToString("yyyy-MM-dd HH:mm:ss.fff");
                writer.WriteLine(string.Format("{0}\t{1}\t{2}\t{3}", ts, lvl, log, msg));
            }
        }
        sw.Stop();
        Console.WriteLine(string.Format("[C#] LOG generated in {0:F2} seconds.", sw.Elapsed.TotalSeconds));
    }

    public static void GenerateDat(string path, int count) {
        Console.WriteLine(string.Format("[C#] Generating DAT with {0:N0} lines to {1}...", count, path));
        Stopwatch sw = Stopwatch.StartNew();
        Random rand = new Random(45);
        using (StreamWriter writer = new StreamWriter(path, false, Encoding.UTF8, 65536)) {
            DateTime baseDate = DateTime.Now;
            for (int i = 1; i <= count; i++) {
                string ts = baseDate.AddSeconds(-i).ToString("yyyy-MM-dd HH:mm:ss.fff");
                string name = string.Format("Item_{0}", i);
                string val = rand.Next(10, 9999).ToString();
                writer.WriteLine(string.Format("{0}|{1}|{2}|{3}", i, name, val, ts));
            }
        }
        sw.Stop();
        Console.WriteLine(string.Format("[C#] DAT generated in {0:F2} seconds.", sw.Elapsed.TotalSeconds));
    }
}
"@

Write-Host "Compiling high-performance C# generator using Add-Type..."
Add-Type -TypeDefinition $source

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

if ($Type -eq "all" -or $Type -eq "csv") {
    [FastGenerator]::GenerateCsv("sample_data.csv", $Lines)
}
if ($Type -eq "all" -or $Type -eq "tsv") {
    [FastGenerator]::GenerateTsv("sample_data.tsv", $Lines)
}
if ($Type -eq "all" -or $Type -eq "log") {
    [FastGenerator]::GenerateLog("sample_data.log", $Lines)
}
if ($Type -eq "all" -or $Type -eq "dat") {
    [FastGenerator]::GenerateDat("sample_data.dat", $Lines)
}

$stopwatch.Stop()
Write-Host "All selected sample files generated successfully in $($stopwatch.Elapsed.TotalSeconds.ToString("F2")) seconds!"
