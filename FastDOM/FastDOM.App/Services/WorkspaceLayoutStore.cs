using System.IO;
using System.Text.Json;
using FastDOM.Infrastructure.Config;

namespace FastDOM.App.Services;

public sealed class WorkspaceLayout
{
    public WorkspaceWindowLayout Main { get; set; } = new();
    public WorkspaceWindowLayout? Movers { get; set; }
    public L2WindowLayout? L2Heat { get; set; }
    public List<ChartWindowLayout> Charts { get; set; } = [];
}

public class WorkspaceWindowLayout
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

public sealed class ChartWindowLayout : WorkspaceWindowLayout
{
    public string Symbol { get; set; } = "SPY";
    public string Timeframe { get; set; } = "5m · 5D";
    public bool ExtendedHours { get; set; } = true;
    public string AccountId { get; set; } = "";
    public int Quantity { get; set; } = 100;
}

public sealed class L2WindowLayout : WorkspaceWindowLayout
{
    public List<string> Symbols { get; set; } = [];
    public int SelectedTab { get; set; }
}

public sealed class WorkspaceLayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public WorkspaceLayoutStore(ConfigManager config) =>
        _path = config.GetConfigPath("workspace.layout.json");

    public WorkspaceLayout Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<WorkspaceLayout>(File.ReadAllText(_path), JsonOptions) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }

    public void Save(WorkspaceLayout layout)
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(layout, JsonOptions));
        File.Move(temp, _path, true);
    }
}
