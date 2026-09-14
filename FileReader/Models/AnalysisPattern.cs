namespace FileReader.Models;

public sealed class AnalysisPattern
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "SQL";
    public string Sql { get; set; } = "";
    public List<SearchCondition> Conditions { get; set; } = new();
    public string ChartType { get; set; } = "棒グラフ";
    public string XColumn { get; set; } = "";
    public string YColumn { get; set; } = "";
    public override string ToString() => $"[{Kind}] {Name}";
}
