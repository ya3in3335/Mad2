using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Workforce;

namespace Veltrix.Desktop.Presentation.ViewModels;

/// <summary>Employee list row.</summary>
public sealed record EmployeeRow(LocalEmployee Employee, string Department, string Position, string StatusLabel)
{
    public Guid Id => Employee.Id;
    public string Code => Employee.Code;
    public string Name => Employee.Name;
    public string? Phone => Employee.Phone;
    public string Status => Employee.Status;
}

/// <summary>CSV layout of the employee export / import (header = property names).</summary>
public sealed record EmployeeCsvRow(string Code, string Name, string? Role, string? Phone, string? Email, string? Department, string? Position, string? Workplace,
    string? ContractType, string? ContractStart, string? ContractEnd, string Status, decimal Salary, string PayBasis, decimal HourlyRate, string HireDate,
    string? EmergencyContactName, string? EmergencyContactPhone, string? Notes);

/// <summary>Workforce employees: list, search, department / status filters, CSV import / export, and the details panel.</summary>
public sealed partial class WfEmployeesViewModel(DesktopServices s) : WorkforcePageViewModel(s)
{
    public override string TitleKey => "wf.nav.employees";

    public ObservableCollection<EmployeeRow> Items { get; } = [];
    public ObservableCollection<Choice<Guid?>> DepartmentFilters { get; } = [];
    public IReadOnlyList<Choice<string?>> StatusFilters =>
        [new(null, Loc.Get("wf.all")), .. EmployeeStatuses.All.Select(st => new Choice<string?>(st, T("emp", st)))];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private Guid? _departmentFilter;
    [ObservableProperty] private string? _statusFilter = EmployeeStatuses.Active;
    [ObservableProperty] private EmployeeRow? _selected;
    [ObservableProperty] private WfEmployeeDetailsViewModel? _details;
    [ObservableProperty] private string? _importSummary;

    public bool CanManage => Has(WorkforcePermissions.EmployeesManage);

    private IReadOnlyList<WfDepartment> _departments = [];
    private IReadOnlyList<WfPosition> _positions = [];

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        _departments = await Wf.DepartmentsAsync(Actor, ct);
        _positions = await Wf.PositionsAsync(Actor, ct);
        if (DepartmentFilters.Count != _departments.Count + 1)
            Fill(DepartmentFilters, [new Choice<Guid?>(null, Loc.Get("wf.all")), .. _departments.Select(d => new Choice<Guid?>(d.Id, d.Name))]);
        var list = await Wf.EmployeesAsync(Actor, Search, DepartmentFilter, StatusFilter, ct);
        Fill(Items, list.Select(e => new EmployeeRow(e, Name(_departments, e.DepartmentId), _positions.FirstOrDefault(p => p.Id == e.PositionId)?.Title ?? "",
            T("emp", e.Status))));
        return Items.Count > 0;
    }

    private static string Name(IEnumerable<WfDepartment> depts, Guid? id) => id is { } d ? depts.FirstOrDefault(x => x.Id == d)?.Name ?? "" : "";

    partial void OnDepartmentFilterChanged(Guid? value) => _ = ReloadAsync();
    partial void OnStatusFilterChanged(string? value) => _ = ReloadAsync();

    private bool _keepDetails;

    partial void OnSelectedChanged(EmployeeRow? value)
    {
        if (value is null || _keepDetails) return;
        var d = new WfEmployeeDetailsViewModel(S, this);
        Details = d;
        _ = d.OpenAsync(value.Id);
    }

    [RelayCommand] public Task SearchNowAsync() => ReloadAsync();

    [RelayCommand]
    public void New()
    {
        Selected = null;
        var d = new WfEmployeeDetailsViewModel(S, this);
        d.StartNew();
        Details = d;
    }

    /// <summary>Ctrl+S: saves the open details panel.</summary>
    [RelayCommand]
    public Task SaveDetailsAsync() => Details is { CanManage: true } d ? d.SaveAsync() : Task.CompletedTask;

    [RelayCommand]
    public void CloseDetails()
    {
        Selected = null;
        Details = null;
    }

    internal IReadOnlyList<WfDepartment> Departments => _departments;
    internal IReadOnlyList<WfPosition> Positions => _positions;

    internal async Task SavedAsync(Guid id)
    {
        await ReloadAsync();
        var row = Items.FirstOrDefault(i => i.Id == id);
        if (row is null) return;
        _keepDetails = true;
        try { Selected = row; }
        finally { _keepDetails = false; }
    }

    [RelayCommand]
    public Task ExportCsvAsync() => SubmitAsync(async () =>
    {
        var all = await Wf.EmployeesAsync(Actor, Search, DepartmentFilter, StatusFilter);
        await ExportCsvAsync("employees", all.Select(ToCsvRow));
    }, null);

    private EmployeeCsvRow ToCsvRow(LocalEmployee e) => new(e.Code, e.Name, e.Role, e.Phone, e.Email, Name(_departments, e.DepartmentId),
        _positions.FirstOrDefault(p => p.Id == e.PositionId)?.Title, e.Workplace, e.ContractType, Iso(e.ContractStart), Iso(e.ContractEnd), e.Status, e.Salary, e.PayBasis,
        e.HourlyRate, Iso(e.HireDate)!, e.EmergencyContactName, e.EmergencyContactPhone, e.Notes);

    private static string? Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Imports a CSV with the export's columns (header row required; Code matches existing employees, otherwise a new one is created).</summary>
    [RelayCommand]
    public Task ImportCsvAsync() => SubmitAsync(async () =>
    {
        var path = S.Ui.PickOpenFile("CSV (*.csv)|*.csv");
        if (path is null) return;
        var (created, updated, errors) = await ImportAsync(await File.ReadAllTextAsync(path));
        ImportSummary = Loc.Format("wf.emp.importSummary", created, updated, errors.Count) + (errors.Count > 0 ? "\n" + string.Join("\n", errors.Take(10)) : "");
        await ReloadAsync();
    }, null);

    public async Task<(int Created, int Updated, List<string> Errors)> ImportAsync(string csv)
    {
        var rows = CsvReader.Parse(csv);
        if (rows.Count == 0) throw new InvalidOperationException("wf.err.csvEmpty");
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => header.IndexOf(name.ToLowerInvariant());
        if (Col("Name") < 0) throw new InvalidOperationException("wf.err.csvHeader");
        var existing = (await Wf.EmployeesAsync(Actor)).ToDictionary(e => e.Code, StringComparer.OrdinalIgnoreCase);
        var depts = (await Wf.DepartmentsAsync(Actor)).ToList();
        var positions = await Wf.PositionsAsync(Actor);
        int created = 0, updated = 0;
        var errors = new List<string>();
        for (var i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            string? Get(string name) => Col(name) is var c and >= 0 && c < r.Count && r[c].Length > 0 ? r[c] : null;
            try
            {
                var code = Get("Code");
                var deptName = Get("Department");
                var dept = deptName is null ? null : depts.FirstOrDefault(d => string.Equals(d.Name, deptName, StringComparison.CurrentCultureIgnoreCase));
                if (deptName is not null && dept is null) depts.Add(dept = await Wf.UpsertDepartmentAsync(Actor, null, deptName));
                var posName = Get("Position");
                var pos = posName is null ? null : positions.FirstOrDefault(p => string.Equals(p.Title, posName, StringComparison.CurrentCultureIgnoreCase));
                var input = new EmployeeProfileInput(Get("Name") ?? throw new InvalidOperationException("wf.err.nameRequired"),
                    Get("Salary") is { } sal ? ParseDecimal(sal, "wf.err.salary") : 0, DateOrNull(Get("HireDate")) ?? Today)
                {
                    Code = code, Role = Get("Role"), Phone = Get("Phone"), Email = Get("Email"), DepartmentId = dept?.Id, PositionId = pos?.Id, Workplace = Get("Workplace"),
                    ContractType = Get("ContractType"), ContractStart = DateOrNull(Get("ContractStart")), ContractEnd = DateOrNull(Get("ContractEnd")),
                    Status = Get("Status") ?? EmployeeStatuses.Active, PayBasis = Get("PayBasis") ?? "Monthly",
                    HourlyRate = Get("HourlyRate") is { } hr ? ParseDecimal(hr, "wf.err.rate") : 0,
                    EmergencyContactName = Get("EmergencyContactName"), EmergencyContactPhone = Get("EmergencyContactPhone"), Notes = Get("Notes"),
                };
                var match = code is not null ? existing.GetValueOrDefault(code) : null;
                var saved = await Wf.UpsertEmployeeAsync(Actor, match?.Id, input);
                existing[saved.Code] = saved;
                if (match is null) created++; else updated++;
            }
            catch (Exception e)
            {
                errors.Add(Loc.Format("wf.emp.importLine", i + 1, Describe(e)));
            }
        }
        return (created, updated, errors);
    }

    private static DateOnly? DateOrNull(string? text) =>
        text is null ? null
        : DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
        : DateOnly.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out d) ? d
        : throw new InvalidOperationException("wf.err.date");
}

public sealed record CredentialRow(Guid Id, string Kind, string KindLabel, string Value, string? Label, string Created, bool IsActive);
public sealed record DocumentRow(Guid Id, string DocType, string Path, string Expiry, bool IsExpired, string? Notes);
public sealed record AuditRow(string At, string Actor, string Action, string Changes);

/// <summary>Employee details: profile (+ photo), contract, credentials (card / QR / PIN), documents and audit history.</summary>
public sealed partial class WfEmployeeDetailsViewModel : ObservableObject
{
    private readonly DesktopServices _s;
    private readonly WfEmployeesViewModel _owner;

    public WfEmployeeDetailsViewModel(DesktopServices s, WfEmployeesViewModel owner)
    {
        _s = s;
        _owner = owner;
    }

    private WorkforceService Wf => _s.Standalone.Workforce;
    private WorkforceActor Actor => _owner.Actor;
    private ILocalizer Loc => _s.Loc;

    public ObservableCollection<CredentialRow> Credentials { get; } = [];
    public ObservableCollection<DocumentRow> Documents { get; } = [];
    public ObservableCollection<AuditRow> History { get; } = [];
    public IReadOnlyList<Choice<Guid?>> DepartmentChoices => [new(null, "—"), .. _owner.Departments.Select(d => new Choice<Guid?>(d.Id, d.Name))];
    public IReadOnlyList<Choice<Guid?>> PositionChoices => [new(null, "—"), .. _owner.Positions.Select(p => new Choice<Guid?>(p.Id, p.Title))];
    public ObservableCollection<Choice<Guid?>> ManagerChoices { get; } = [];
    public IReadOnlyList<Choice<string>> StatusChoices => EmployeeStatuses.All.Select(st => new Choice<string>(st, WfText.Label(Loc, "emp", st))).ToList();
    public static IReadOnlyList<string> PayBases { get; } = ["Monthly", "Hourly"];
    public static IReadOnlyList<string> ContractTypes { get; } = ["CDI", "CDD", "Interim", "Apprentice", "Part-time"];

    public Guid? Id { get; private set; }
    public bool IsNew => Id is null;
    public bool CanManage => _s.Session.HasPermission(WorkforcePermissions.EmployeesManage);
    public bool CanCredentials => _s.Session.HasPermission(WorkforcePermissions.CredentialsManage);
    public bool CanAudit => _s.Session.HasPermission(WorkforcePermissions.AuditView);

    /// <summary>0 profile, 1 contract, 2 credentials, 3 documents, 4 history.</summary>
    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _role = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPhoto))] private string? _photoPath;
    [ObservableProperty] private Guid? _departmentId;
    [ObservableProperty] private Guid? _positionId;
    [ObservableProperty] private Guid? _managerId;
    [ObservableProperty] private string _workplace = "";
    [ObservableProperty] private string _status = EmployeeStatuses.Active;
    [ObservableProperty] private string _emergencyName = "";
    [ObservableProperty] private string _emergencyPhone = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _contractType = "";
    [ObservableProperty] private DateTime? _contractStart;
    [ObservableProperty] private DateTime? _contractEnd;
    [ObservableProperty] private DateTime? _terminationDate;
    [ObservableProperty] private DateTime? _hireDate;
    [ObservableProperty] private string _payBasis = "Monthly";
    [ObservableProperty] private string _salary = "0";
    [ObservableProperty] private string _hourlyRate = "0";

    [ObservableProperty] private string? _formError;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isBusy;

    /// <summary>Card capture: the keyboard-wedge reader types the UID here when the card is tapped.</summary>
    [ObservableProperty] private string _cardUid = "";
    [ObservableProperty] private string _cardLabel = "";
    [ObservableProperty] private bool _isCapturingCard;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasQrToken))] private string? _qrToken;
    [ObservableProperty] private string _newPin = "";

    [ObservableProperty] private string _docType = "";
    [ObservableProperty] private DateTime? _docExpiry;
    [ObservableProperty] private string _docNotes = "";

    public bool HasPhoto => !string.IsNullOrWhiteSpace(PhotoPath) && File.Exists(PhotoPath);
    public bool HasQrToken => QrToken is not null;
    /// <summary>Payload a QR generator should encode (prefix recognised by the kiosk and keyboard-mode 2D scanners).</summary>
    public string? QrPayload => QrToken is null ? null : WfKioskViewModel.QrPrefix + QrToken;

    partial void OnQrTokenChanged(string? value) => OnPropertyChanged(nameof(QrPayload));

    public void StartNew()
    {
        Id = null;
        HireDate = DateTime.Today;
        FillManagers(null);
        OnPropertyChanged(nameof(IsNew));
    }

    public async Task OpenAsync(Guid id)
    {
        try
        {
            var e = await Wf.EmployeeAsync(Actor, id) ?? throw new InvalidOperationException("erp.err.unknownEmployee");
            Id = e.Id;
            Code = e.Code; Name = e.Name; Role = e.Role ?? ""; Phone = e.Phone ?? ""; Email = e.Email ?? ""; PhotoPath = e.PhotoPath;
            DepartmentId = e.DepartmentId; PositionId = e.PositionId; ManagerId = e.ManagerId; Workplace = e.Workplace ?? ""; Status = e.Status;
            EmergencyName = e.EmergencyContactName ?? ""; EmergencyPhone = e.EmergencyContactPhone ?? ""; Notes = e.Notes ?? "";
            ContractType = e.ContractType ?? ""; ContractStart = D(e.ContractStart); ContractEnd = D(e.ContractEnd); TerminationDate = D(e.TerminationDate);
            HireDate = D(e.HireDate); PayBasis = e.PayBasis; Salary = Txt(e.Salary); HourlyRate = Txt(e.HourlyRate);
            OnPropertyChanged(nameof(IsNew));
            FillManagers(e.Id);
            await LoadTabsAsync();
        }
        catch (Exception ex)
        {
            _s.Log.Warn("Employee details failed", ex);
            FormError = _owner.Describe(ex);
        }
    }

    private void FillManagers(Guid? self)
    {
        ManagerChoices.Clear();
        ManagerChoices.Add(new Choice<Guid?>(null, "—"));
        foreach (var r in _owner.Items.Where(r => r.Id != self)) ManagerChoices.Add(new Choice<Guid?>(r.Id, $"{r.Code} — {r.Name}"));
    }

    private async Task LoadTabsAsync()
    {
        if (Id is not { } id) return;
        if (CanCredentials || CanManage)
        {
            var creds = await Wf.CredentialsAsync(Actor, id);
            Replace(Credentials, creds.OrderByDescending(c => c.IsActive).ThenByDescending(c => c.CreatedAt).Select(c => new CredentialRow(c.Id, c.Kind,
                WfText.Label(Loc, "cred", c.Kind), c.Kind is CredentialKinds.Card or CredentialKinds.TerminalUser ? AccessEngine.Mask(c.Value) : "••••", c.Label,
                Fmt(c.CreatedAt), c.IsActive)));
        }
        var today = DateOnly.FromDateTime(_s.Time.GetLocalNow().DateTime);
        var docs = await Wf.DocumentsAsync(Actor, id);
        Replace(Documents, docs.Select(d => new DocumentRow(d.Id, d.DocType, d.Path, d.Expiry?.ToString("d", Loc.Culture) ?? "", d.Expiry is { } x && x < today, d.Notes)));
        if (CanAudit)
            Replace(History, (await Wf.EmployeeHistoryAsync(Actor, id)).OrderByDescending(h => h.Seq).Select(h => new AuditRow(Fmt(h.At), h.Actor, h.Action,
                Summarize(h.OldJson, h.NewJson))));
    }

    private static string Summarize(string? oldJson, string? newJson) =>
        oldJson is null ? newJson ?? "" : newJson is null ? oldJson : $"{oldJson} → {newJson}";

    private string Fmt(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, _s.Time.LocalTimeZone).ToString("g", Loc.Culture);
    private static DateTime? D(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static DateOnly? O(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;
    private static string Txt(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    private async Task RunAsync(Func<Task> action, string? successKey = "dk.saved")
    {
        if (IsBusy) return;
        IsBusy = true;
        FormError = null;
        Message = null;
        try
        {
            await action();
            if (successKey is not null) Message = Loc.Get(successKey);
        }
        catch (Exception e)
        {
            _s.Log.Warn("Employee action failed", e);
            FormError = _owner.Describe(e);
        }
        finally { IsBusy = false; }
    }

    public EmployeeProfileInput ToInput()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidOperationException("wf.err.nameRequired");
        if (!Parse.TryDecimal(Salary, out var salary) || salary < 0) throw new InvalidOperationException("wf.err.salary");
        if (!Parse.TryDecimal(HourlyRate, out var rate) || rate < 0) throw new InvalidOperationException("wf.err.rate");
        return new EmployeeProfileInput(Name.Trim(), salary, O(HireDate) ?? DateOnly.FromDateTime(_s.Time.GetLocalNow().DateTime))
        {
            Code = string.IsNullOrWhiteSpace(Code) ? null : Code.Trim(), Role = Role, Phone = Phone, Email = Email, PhotoPath = PhotoPath, DepartmentId = DepartmentId,
            PositionId = PositionId, Workplace = Workplace, ManagerId = ManagerId, ContractType = ContractType, ContractStart = O(ContractStart), ContractEnd = O(ContractEnd),
            TerminationDate = O(TerminationDate), Status = Status, EmergencyContactName = EmergencyName, EmergencyContactPhone = EmergencyPhone, Notes = Notes,
            PayBasis = PayBasis, HourlyRate = rate,
        };
    }

    [RelayCommand]
    public Task SaveAsync() => RunAsync(async () =>
    {
        var saved = await Wf.UpsertEmployeeAsync(Actor, Id, ToInput());
        Id = saved.Id;
        Code = saved.Code;
        OnPropertyChanged(nameof(IsNew));
        await _owner.SavedAsync(saved.Id);
        await LoadTabsAsync();
    });

    [RelayCommand]
    public Task DeleteAsync() => RunAsync(async () =>
    {
        if (Id is not { } id || !_s.Ui.Confirm(Loc.Get("wf.emp.deleteConfirm"))) return;
        await Wf.DeleteEmployeeAsync(Actor, id);
        _owner.CloseDetails();
        await _owner.RefreshAsync();
    }, null);

    [RelayCommand]
    public void PickPhoto()
    {
        var path = _s.Ui.PickOpenFile(Loc.Get("wf.emp.imageFilter") + "|*.png;*.jpg;*.jpeg;*.bmp");
        if (path is not null) PhotoPath = path;
    }

    [RelayCommand]
    public void ClearPhoto() => PhotoPath = null;

    // ---- credentials

    [RelayCommand]
    public void StartCardCapture()
    {
        CardUid = "";
        IsCapturingCard = true;
    }

    /// <summary>Enter in the capture box (the reader types the UID then Enter) or the Assign button.</summary>
    [RelayCommand]
    public Task AssignCardAsync() => RunAsync(async () =>
    {
        if (Id is not { } id) return;
        var uid = CardUid.Trim();
        if (uid.Length == 0) throw new InvalidOperationException("wf.err.cardUid");
        await Wf.AssignCardAsync(Actor, id, uid, string.IsNullOrWhiteSpace(CardLabel) ? null : CardLabel.Trim());
        CardUid = "";
        CardLabel = "";
        IsCapturingCard = false;
        await LoadTabsAsync();
    });

    [RelayCommand]
    public Task GenerateQrAsync() => RunAsync(async () =>
    {
        if (Id is not { } id) return;
        if (Credentials.Any(c => c.Kind == CredentialKinds.Qr && c.IsActive) && !_s.Ui.Confirm(Loc.Get("wf.cred.qrRotateConfirm"))) return;
        QrToken = await Wf.IssueQrTokenAsync(Actor, id);
        await LoadTabsAsync();
    }, null);

    [RelayCommand]
    public void CopyQr()
    {
        if (QrPayload is { } p) _s.Ui.CopyToClipboard(p);
    }

    [RelayCommand]
    public Task SetPinAsync() => RunAsync(async () =>
    {
        if (Id is not { } id) return;
        await Wf.SetPinAsync(Actor, id, NewPin.Trim());
        NewPin = "";
        await LoadTabsAsync();
    });

    [RelayCommand]
    public Task RevokeAsync(CredentialRow? row) => RunAsync(async () =>
    {
        if (row is null || !row.IsActive || !_s.Ui.Confirm(Loc.Get("wf.cred.revokeConfirm"))) return;
        await Wf.RevokeCredentialAsync(Actor, row.Id);
        await LoadTabsAsync();
    });

    // ---- documents

    [RelayCommand]
    public Task AddDocumentAsync() => RunAsync(async () =>
    {
        if (Id is not { } id) return;
        if (string.IsNullOrWhiteSpace(DocType)) throw new InvalidOperationException("wf.err.document");
        var path = _s.Ui.PickOpenFile(Loc.Get("wf.doc.filter") + "|*.*");
        if (path is null) return;
        await Wf.AddDocumentAsync(Actor, id, new EmployeeDocumentInput(DocType.Trim(), path, O(DocExpiry), string.IsNullOrWhiteSpace(DocNotes) ? null : DocNotes.Trim()));
        DocType = "";
        DocExpiry = null;
        DocNotes = "";
        await LoadTabsAsync();
    });

    [RelayCommand]
    public Task RemoveDocumentAsync(DocumentRow? row) => RunAsync(async () =>
    {
        if (row is null || !_s.Ui.Confirm(Loc.Get("wf.doc.removeConfirm"))) return;
        await Wf.RemoveDocumentAsync(Actor, row.Id);
        await LoadTabsAsync();
    });
}
