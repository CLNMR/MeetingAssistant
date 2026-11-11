using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Claude;
using MeetingAssistant.Core.Platform;

namespace MeetingAssistant.Core.Assist;

/// <summary>
/// The Aya connection, the cached lists for the pickers, and the Claude check (spec 03 §4, §11; 02 §8.5).
/// Settings and config save automatically on every change.
/// </summary>
public sealed class AssistConnection : ObservableObject
{
    readonly ISecretStore _secrets;
    readonly IPreferenceStore _prefs;
    readonly Func<HttpMessageHandler?>? _handler;

    public AssistConnection(ISecretStore secrets, IPreferenceStore? prefs = null, Func<HttpMessageHandler?>? handler = null)
    {
        _secrets = secrets;
        _prefs = prefs ?? FilePreferences.Shared;
        _handler = handler;
        _config = AyaConfig.Load(_prefs);
        _settings = AssistSettings.Load(_prefs);
        _hasApiKey = AyaConfig.ApiKey(_secrets) is not null;
        _schemas = AyaCache.Schemas();
        _clients = AyaCache.List(AyaSlot.Clients)?.Records ?? new();
        _projects = AyaCache.List(AyaSlot.Projects)?.Records ?? new();
        _templates = AyaCache.List(AyaSlot.Templates)?.Records ?? new();
        _members = AyaCache.List(AyaSlot.Members)?.Records ?? new();
        _listsFetchedAt = AyaCache.List(AyaSlot.Clients)?.FetchedAt;
    }

    // MARK: Settings

    AyaConfig _config;
    /// <summary>The Aya config; saved on every change.</summary>
    public AyaConfig Config
    {
        get => _config;
        set { if (!_config.Equals(value)) { _config = value.Copy(); _config.Save(_prefs); Raise(); } }
    }

    /// Changes the config through a copy (saves and notifies).
    public void UpdateConfig(Action<AyaConfig> change)
    {
        var c = _config.Copy();
        change(c);
        Config = c;
    }

    AssistSettings _settings;
    /// <summary>The Assist settings; saved on every change.</summary>
    public AssistSettings Settings
    {
        get => _settings;
        set { if (_settings != value) { _settings = value with { }; _settings.Save(_prefs); Raise(); } }
    }

    bool _hasApiKey;
    public bool HasApiKey { get => _hasApiKey; private set => Set(ref _hasApiKey, value); }

    Dictionary<string, AyaListSchema> _schemas;
    /// Slot raw name → cached schema.
    public Dictionary<string, AyaListSchema> Schemas { get => _schemas; private set => Set(ref _schemas, value); }
    public AyaListSchema? Schema(AyaSlot slot) => Schemas.TryGetValue(slot.Raw(), out var s) ? s : null;

    StepState _connectState = StepState.IdleState;
    public StepState ConnectState { get => _connectState; private set => Set(ref _connectState, value); }

    List<AyaWorkspace> _workspaces = new();
    public List<AyaWorkspace> Workspaces { get => _workspaces; private set => Set(ref _workspaces, value); }
    List<AyaRecordSpec> _specs = new();
    /// Every non-system record type of all workspaces (Settings list pickers).
    public List<AyaRecordSpec> Specs { get => _specs; private set => Set(ref _specs, value); }

    // MARK: Picker lists

    List<AyaRecord> _clients, _projects, _templates, _members;
    List<AyaRecord> _upcoming = new();
    DateTime? _listsFetchedAt;
    StepState _listState = StepState.IdleState;
    public List<AyaRecord> Clients { get => _clients; private set => Set(ref _clients, value); }
    public List<AyaRecord> Projects { get => _projects; private set => Set(ref _projects, value); }
    public List<AyaRecord> Templates { get => _templates; private set => Set(ref _templates, value); }
    public List<AyaRecord> Members { get => _members; private set => Set(ref _members, value); }
    /// Planned meetings, today and the next 14 days.
    public List<AyaRecord> Upcoming { get => _upcoming; private set => Set(ref _upcoming, value); }
    public DateTime? ListsFetchedAt { get => _listsFetchedAt; private set => Set(ref _listsFetchedAt, value); }
    public StepState ListState { get => _listState; private set => Set(ref _listState, value); }

    /// <summary>Raised after the lists were refreshed (the setup model drops selections that disappeared).</summary>
    public event Action? ListsRefreshed;

    /// <summary>
    /// Called at the end of <see cref="RefreshListsAsync"/> so the vocabulary can take Aya names (spec 03 §4:
    /// when "use Aya names" is on and 12 h passed or the term version changed). Use <see cref="AyaNamesAsync"/>.
    /// </summary>
    public Func<Task>? VocabularyRefresh { get; set; }

    // MARK: Claude

    string? _claudeBinary;
    ClaudeAuthStatus? _claudeAuth;
    StepState _claudeState = StepState.IdleState;
    public string? ClaudeBinary { get => _claudeBinary; private set { if (Set(ref _claudeBinary, value)) Raise(nameof(ClaudeReady)); } }
    public ClaudeAuthStatus? ClaudeAuth { get => _claudeAuth; private set { if (Set(ref _claudeAuth, value)) Raise(nameof(ClaudeReady)); } }
    public StepState ClaudeState { get => _claudeState; private set => Set(ref _claudeState, value); }
    /// Binary found and logged in.
    public bool ClaudeReady => ClaudeBinary is not null && ClaudeAuth?.LoggedIn == true;

    /// <summary>Locates the binary and reads <c>claude auth status</c> (spec 03 §11 "Check").</summary>
    public async Task CheckClaudeAsync()
    {
        ClaudeState = new StepState.Working("Checking Claude Code…");
        var b = await Task.Run(() => ClaudeCli.Locate(Settings.ClaudePath));
        if (b is null)
        {
            ClaudeBinary = null; ClaudeAuth = null;
            ClaudeState = new StepState.Failed(ClaudeException.NotInstalled().Message);
            return;
        }
        ClaudeBinary = b;
        ClaudeAuth = await ClaudeCli.AuthStatusAsync(b);
        ClaudeState = ClaudeAuth?.LoggedIn == true
            ? new StepState.Done("Claude Code logged in" + (ClaudeAuth.Subscription is { } s ? $" ({s} plan)" : ""))
            : new StepState.Failed(ClaudeException.NotLoggedIn().Message);
    }

    /// <summary>The binary, checking on demand; null when it is not installed.</summary>
    public async Task<string?> EnsureClaudeAsync()
    {
        if (ClaudeBinary is null) await CheckClaudeAsync();
        return ClaudeBinary;
    }

    /// <summary>The footer line of the prepare screen (spec 03 §5).</summary>
    public string ClaudeLine =>
        $"Claude {Capitalized(Settings.Model)} via your Claude subscription · transcript text is sent to Claude, audio stays on this PC";

    static string Capitalized(string s) => s.Length == 0 ? s : s[..1].ToUpperInvariant() + s[1..];

    // MARK: Connection

    /// API key stored and all required lists set.
    public bool IsConnected => HasApiKey && Config.IsComplete;

    /// A service for the current config, or null when not configured.
    public AyaService? Service => Config.Client(_secrets, _handler?.Invoke()) is { } c ? new AyaService(Config, c) : null;

    /// <summary>At app start (spec 03 §4): upgrade the lists when the config is incomplete or no templates are cached, else refresh them.</summary>
    public Task StartupAsync()
    {
        if (Config.OrgId.Length == 0 || !HasApiKey) return Task.CompletedTask;
        return !Config.IsComplete || Templates.Count == 0 ? UpgradeListsAsync() : RefreshListsAsync();
    }

    /// <summary>Fills in lists that are not chosen yet (and follows lists that moved workspace).</summary>
    public async Task UpgradeListsAsync()
    {
        if (Service is not { } svc) return;
        ConnectState = new StepState.Working("Updating the Aya lists…");
        try
        {
            Config = await svc.AutoSelectListsAsync();
            Schemas = await new AyaService(Config, svc.Client).RefreshSchemasAsync();
            await RefreshListsAsync();
            ConnectState = new StepState.Done();
        }
        catch (Exception e) when (e is AyaException or IOException) { ConnectState = new StepState.Failed(e.Message); }
    }

    /// <summary>
    /// Connect flow (spec 02 §8.5, 03 §4): store the key, check it, auto-select the lists, read the schemas,
    /// load the lists and the catalog.
    /// </summary>
    public async Task ConnectAsync(string orgId, string apiKey)
    {
        ConnectState = new StepState.Working("Connecting to Aya…");
        var key = apiKey.Trim();
        if (key.Length > 0) AyaConfig.SetApiKey(_secrets, key);
        HasApiKey = AyaConfig.ApiKey(_secrets) is not null;
        var cfg = Config.Copy();
        cfg.OrgId = orgId.Trim();
        if (cfg.Client(_secrets, _handler?.Invoke()) is not { } client)
        {
            ConnectState = new StepState.Failed("Enter the organization id and the API key.");
            return;
        }
        try
        {
            await client.MeAsync();
            cfg = await new AyaService(cfg, client).AutoSelectListsAsync();
            Config = cfg;
            ConnectState = new StepState.Working("Reading the lists' fields…");
            Schemas = await new AyaService(cfg, client).RefreshSchemasAsync();
            ConnectState = new StepState.Working("Loading clients, projects and templates…");
            await RefreshListsAsync();
            await LoadCatalogAsync();
            ConnectState = new StepState.Done($"Connected. {Clients.Count} clients, {Projects.Count} projects, {Templates.Count} templates.");
        }
        catch (Exception e) when (e is AyaException or IOException)
        {
            ConnectState = new StepState.Failed(e.Message);
        }
    }

    /// <summary>Remove Key: deletes the key, clears the caches and the lists; the org id is kept.</summary>
    public void Disconnect()
    {
        AyaConfig.SetApiKey(_secrets, null);
        HasApiKey = false;
        AyaCache.Clear();
        Clients = new(); Projects = new(); Templates = new(); Members = new(); Upcoming = new(); Schemas = new();
        ConnectState = StepState.IdleState;
    }

    /// <summary>Workspaces and record types for the list pickers in Settings.</summary>
    public async Task LoadCatalogAsync()
    {
        if (Service is not { } svc) return;
        try
        {
            var (ws, specs) = await svc.CatalogAsync();
            Workspaces = ws;
            Specs = specs;
        }
        catch (AyaException e) { ConnectState = new StepState.Failed(e.Message); }
    }

    public string WorkspaceLabel(string? id) => Workspaces.FirstOrDefault(w => w.Id == id)?.Label ?? "";

    /// <summary>
    /// Changes the list of a slot (spec 02 §8.5): set it in the spec's home workspace and refresh the schemas;
    /// for contacts the client link is re-picked (first single relation containing "firm"); other slots reload the lists.
    /// </summary>
    public async Task SetListAsync(AyaSlot slot, AyaRecordSpec spec)
    {
        var ws = Workspaces.FirstOrDefault(w => w.Id == spec.WorkspaceId) ?? Workspaces.FirstOrDefault();
        if (ws is null) return;
        UpdateConfig(c =>
        {
            c.SetList(slot, new AyaListRef(ws.Id, ws.Label, spec.TechName, spec.Label));
            if (slot == AyaSlot.Contacts) c.ContactsClientField = null;
        });
        await RefreshSchemasAsync();
        if (slot == AyaSlot.Contacts
            && Schema(AyaSlot.Contacts)?.Relations.FirstOrDefault(f => f.Kind == AyaFieldKind.Relation && f.Name.Contains("firm")) is { } link)
            UpdateConfig(c => c.ContactsClientField = link.Key);
        if (slot != AyaSlot.Contacts) await RefreshListsAsync();
    }

    /// <summary>Re-reads the schemas ("Refresh Fields").</summary>
    public async Task RefreshSchemasAsync()
    {
        if (Service is not { } svc) return;
        ConnectState = new StepState.Working("Reading the lists' fields…");
        try { Schemas = await svc.RefreshSchemasAsync(); ConnectState = new StepState.Done("Fields updated."); }
        catch (Exception e) when (e is AyaException or IOException) { ConnectState = new StepState.Failed(e.Message); }
    }

    /// <summary>
    /// Refresh lists (spec 03 §4): clients, templates, projects (if configured), members (if configured,
    /// errors ignored) and planned meetings; then the vocabulary hook.
    /// </summary>
    public async Task RefreshListsAsync()
    {
        if (Service is not { } svc) return;
        ListState = new StepState.Working("Loading from Aya…");
        try
        {
            Clients = await svc.RefreshListAsync(AyaSlot.Clients);
            Templates = await svc.RefreshListAsync(AyaSlot.Templates);
            if (Config.List(AyaSlot.Projects) is not null) Projects = await svc.RefreshListAsync(AyaSlot.Projects);
            if (Config.List(AyaSlot.Members) is not null)
            {
                try { Members = await svc.RefreshListAsync(AyaSlot.Members); } catch (AyaException) { }
            }
            Upcoming = await svc.UpcomingMeetingsAsync();
            ListsFetchedAt = DateTime.UtcNow;
            ListState = new StepState.Done();
            ListsRefreshed?.Invoke();
        }
        catch (Exception e) when (e is AyaException or IOException)
        {
            ListState = new StepState.Failed(e.Message);
        }
        if (VocabularyRefresh is { } v) await v();
    }

    /// <summary>
    /// Company, contact, member and project names for the vocabulary (spec 03 §4). Contacts are loaded here
    /// (and cached); person names containing "@" are skipped.
    /// </summary>
    public async Task<(List<string> Companies, List<string> People, List<string> Projects)> AyaNamesAsync()
    {
        var contacts = new List<AyaRecord>();
        if (Service is { } svc && Config.List(AyaSlot.Contacts) is not null)
        {
            try { contacts = await svc.RefreshListAsync(AyaSlot.Contacts); } catch (AyaException) { }
        }
        var people = contacts.Concat(Members).Select(r => r.Title).Where(t => !t.Contains('@')).ToList();
        return (Clients.Select(c => c.Title).ToList(), people, Projects.Select(p => p.Title).ToList());
    }

    /// <summary>Just the planned meetings (cheap; done each time the preparation screen opens).</summary>
    public async Task RefreshUpcomingAsync()
    {
        if (Service is not { } svc || Config.List(AyaSlot.Meetings) is null) return;
        try { Upcoming = await svc.UpcomingMeetingsAsync(); } catch (AyaException) { }
    }

    /// <summary>Server-side search for clients not in the cached list (from 2 characters).</summary>
    public async Task<List<AyaRecord>> SearchClientsAsync(string text)
    {
        if (Service is not { } svc || text.Length < 2) return new();
        try { return await svc.SearchAsync(AyaSlot.Clients, text); } catch (AyaException) { return new(); }
    }
}
