using System.Data;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Contracts.Reporting;
using Veltrix.Desktop.Core.Api;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed record ReportInfo(string Key, string Title);

/// <summary>Runs server reports for a date range, shows them as a table and exports CSV / Excel / PDF.</summary>
public sealed partial class ReportsViewModel(DesktopServices s) : PageViewModel(s)
{
    public override string TitleKey => "nav.reports";

    [ObservableProperty] private IReadOnlyList<ReportInfo> _reports = [];
    [ObservableProperty] private ReportInfo? _selectedReport;
    [ObservableProperty] private DateTime _from = DateTime.Today.AddDays(1 - DateTime.Today.Day);
    [ObservableProperty] private DateTime _to = DateTime.Today;
    [ObservableProperty] private DataView? _table;
    [ObservableProperty] private string? _tableTitle;
    [ObservableProperty] private IReadOnlyList<KeyValuePair<string, string>> _summary = [];
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _runError;

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        Reports = S.IsStandalone
            ? Core.Standalone.StandaloneStore.Reports.Select(r => new ReportInfo(r.Key, Loc.Get("dk.sa.report." + r.Key))).ToList()
            : await S.Api.GetAsync<List<ReportInfo>>("/api/v1/reports/", ct);
        SelectedReport ??= Reports.FirstOrDefault();
        return Reports.Count > 0;
    }

    private string RunPath(string? format) => $"/api/v1/reports/run/{Uri.EscapeDataString(SelectedReport!.Key)}" +
        ApiClient.Query(("from", DateOnly.FromDateTime(From)), ("to", DateOnly.FromDateTime(To)), ("format", format));

    [RelayCommand]
    public async Task RunAsync()
    {
        if (SelectedReport is null || IsRunning) return;
        if (To < From) { RunError = Loc.Get("dk.rep.badRange"); return; }
        IsRunning = true;
        RunError = null;
        try
        {
            var t = S.IsStandalone
                ? await S.Standalone.Store.RunReportAsync(SelectedReport.Key, DateOnly.FromDateTime(From), DateOnly.FromDateTime(To))
                : await S.Api.GetAsync<ReportTable>(RunPath(null));
            TableTitle = t.Title;
            Table = ToDataTable(t, Loc.Culture).DefaultView;
            Summary = t.Summary.Select(kv => new KeyValuePair<string, string>(kv.Key, FormatCell(kv.Value, "text", Loc.Culture))).ToList();
        }
        catch (Exception e)
        {
            RunError = Describe(e);
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    public Task ExportAsync(string? format) => TryActionAsync(async () =>
    {
        if (SelectedReport is null || format is not ("csv" or "xlsx" or "pdf")) return;
        if (S.IsStandalone)
        {
            // Excel/PDF rendering lives on the server; standalone exports CSV.
            var csvPath = S.Ui.PickSaveFile($"{SelectedReport.Key}-{From:yyyyMMdd}-{To:yyyyMMdd}.csv", "CSV (*.csv)|*.csv");
            if (csvPath is null) return;
            var table = await S.Standalone.Store.RunReportAsync(SelectedReport.Key, DateOnly.FromDateTime(From), DateOnly.FromDateTime(To));
            await File.WriteAllTextAsync(csvPath, Core.Standalone.StandaloneStore.ToCsv(table), new System.Text.UTF8Encoding(true));
            return;
        }
        var name = $"{SelectedReport.Key}-{From:yyyyMMdd}-{To:yyyyMMdd}.{format}";
        var path = S.Ui.PickSaveFile(name, format switch { "csv" => "CSV (*.csv)|*.csv", "xlsx" => "Excel (*.xlsx)|*.xlsx", _ => "PDF (*.pdf)|*.pdf" });
        if (path is null) return;
        var bytes = await S.Api.GetBytesAsync(RunPath(format));
        await File.WriteAllBytesAsync(path, bytes);
    }, "dk.rep.exported");

    /// <summary>Converts the generic report payload into a DataTable for an auto-generated grid.</summary>
    public static DataTable ToDataTable(ReportTable t, CultureInfo culture)
    {
        var dt = new DataTable(t.Key) { Locale = culture };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in t.Columns)
        {
            var title = string.IsNullOrWhiteSpace(c.Title) ? c.Key : c.Title;
            while (!names.Add(title)) title += " ";
            dt.Columns.Add(title, typeof(string));
        }
        foreach (var row in t.Rows)
        {
            var values = new object[t.Columns.Count];
            for (var i = 0; i < values.Length; i++) values[i] = i < row.Count ? FormatCell(row[i], t.Columns[i].Type, culture) : "";
            dt.Rows.Add(values);
        }
        return dt;
    }

    public static string FormatCell(object? value, string type, CultureInfo culture)
    {
        if (value is JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Null or JsonValueKind.Undefined: return "";
                case JsonValueKind.Number when e.TryGetDecimal(out var d):
                    return type switch
                    {
                        "money" => d.ToString("N2", culture),
                        "percent" => d.ToString("N1", culture) + " %",
                        _ => d.ToString("#,##0.###", culture),
                    };
                case JsonValueKind.True: return "✓";
                case JsonValueKind.False: return "";
                case JsonValueKind.String:
                    var str = e.GetString() ?? "";
                    return type == "date" && DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) ? dto.ToString("d", culture) : str;
                default: return e.ToString();
            }
        }
        return value switch
        {
            null => "",
            decimal d => d.ToString("N2", culture),
            IFormattable f => f.ToString(null, culture),
            _ => value.ToString() ?? "",
        };
    }
}
