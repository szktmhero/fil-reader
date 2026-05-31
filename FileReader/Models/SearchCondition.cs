using System.Collections.ObjectModel;

namespace FileReader.Models
{
    public class SearchCondition
    {
        public string Column { get; set; } = "全列";
        public string Operator { get; set; } = "LIKE (部分一致)";
        public string Keyword { get; set; } = string.Empty;
    }
}
