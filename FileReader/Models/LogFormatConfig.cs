using System.Collections.Generic;

namespace FileReader.Models
{
    public class LogFormat
    {
        public string Extension { get; set; } = string.Empty;
        public string Delimiter { get; set; } = ",";
        public bool HasHeader { get; set; } = true;
        public List<string>? Columns { get; set; }
    }

    public class LogFormatConfig
    {
        public List<LogFormat> LogFormats { get; set; } = new();
    }
}
