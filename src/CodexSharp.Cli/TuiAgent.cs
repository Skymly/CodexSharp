using CodexSharp.AppServer;
using CodexSharp.Client;
using CodexSharp.Protocol;
using CodexSharp.Runtime;

namespace CodexSharp.Cli;

/// Interactive TUI talks to App Server JSON-RPC, same split as official tui/app_server_session.
internal sealed class TuiAgent : IAsyncDisposable
{
    public TuiAgent(InProcessAppServer hosted, AppServerSession app, CodexConfig config)
    {
        Hosted = hosted;
        App = app;
        Config = config;
        Thread = new ThreadRef(this);
        Approver = new InteractiveApprover();
        UserInput = new InteractiveUserInput();
        McpElicitation = new InteractiveUserInput();
    }

    public InProcessAppServer Hosted { get; }
    public AppServerSession App { get; }
    public CodexConfig Config { get; private set; }
    public ThreadRef Thread { get; }
    public InteractiveApprover Approver { get; }
    public InteractiveUserInput UserInput { get; }
    public InteractiveUserInput McpElicitation { get; }
    public string? Goal { get; private set; }
    public string GoalStatus { get; private set; } = ThreadGoal.Cleared;
    public List<string> PendingImages { get; } = [];

    public event Action<AgentEvent>? Event
    {
        add => App.Event += value;
        remove => App.Event -= value;
    }

    public static async Task<TuiAgent> StartAsync(CodexConfig? config = null)
    {
        var cfg = config ?? ConfigService.Load();
        var hosted = InProcessAppServer.Start();
        var app = new AppServerSession(hosted.Client);
        await app.InitializeAsync("codexsharp_tui", "CodexSharp TUI");
        await app.StartThreadAsync(cfg.Cwd);
        await app.UpdateSettingsAsync(cfg.Model, cfg.SandboxMode, cfg.ApprovalPolicy, cfg.Cwd);
        return new TuiAgent(hosted, app, cfg);
    }

    public static async Task<TuiAgent> ResumeAsync(string threadId, CodexConfig? config = null)
    {
        var cfg = config ?? ConfigService.Load();
        var hosted = InProcessAppServer.Start();
        var app = new AppServerSession(hosted.Client);
        await app.InitializeAsync("codexsharp_tui", "CodexSharp TUI");
        await app.ResumeThreadAsync(threadId);
        var agent = new TuiAgent(hosted, app, cfg);
        agent.SyncGoal();
        return agent;
    }

    public TuiAgent Restart()
    {
        App.StartThreadAsync(Config.Cwd).GetAwaiter().GetResult();
        Goal = null;
        GoalStatus = ThreadGoal.Cleared;
        PendingImages.Clear();
        return this;
    }

    public TuiAgent Resume(string threadId, CodexConfig? _ = null)
    {
        App.ResumeThreadAsync(threadId).GetAwaiter().GetResult();
        SyncGoal();
        return this;
    }

    public TuiAgent Fork()
    {
        App.ForkAsync().GetAwaiter().GetResult();
        return this;
    }

    public Task RunTurnAsync(string text, CancellationToken ct = default)
    {
        var images = PendingImages.Count == 0 ? null : PendingImages.ToArray();
        PendingImages.Clear();
        return App.StartTurnAsync(text, images, ct);
    }

    public bool TryCompact()
    {
        App.CompactAsync().GetAwaiter().GetResult();
        return true;
    }

    public bool RollbackTurns(int count)
    {
        App.RollbackAsync(Math.Max(1, count)).GetAwaiter().GetResult();
        return true;
    }

    public void SetName(string name) =>
        App.SetNameAsync(name).GetAwaiter().GetResult();

    public void ApplySettings(string? model = null, string? sandbox = null, string? approval = null, string? cwd = null, string? effort = null)
    {
        App.UpdateSettingsAsync(model, sandbox, approval, cwd, effort).GetAwaiter().GetResult();
        Config = ConfigService.Load(cwd ?? Config.Cwd, model, sandbox, approval);
    }

    public void SetGoal(string? objective, string? status = null)
    {
        App.SetGoalAsync(objective, status).GetAwaiter().GetResult();
        SyncGoal();
    }

    public void ClearGoal()
    {
        App.ClearGoalAsync().GetAwaiter().GetResult();
        Goal = null;
        GoalStatus = ThreadGoal.Cleared;
    }

    public void SyncGoal()
    {
        var payload = App.GetGoalAsync().GetAwaiter().GetResult();
        if (payload.TryGetProperty("goal", out var goal) && goal.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            Goal = goal.TryGetProperty("objective", out var obj) && obj.ValueKind == System.Text.Json.JsonValueKind.String
                ? obj.GetString()
                : null;
            GoalStatus = goal.TryGetProperty("status", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.String
                ? ThreadGoal.normalizeStatus(st.GetString() ?? "")
                : ThreadGoal.Cleared;
            if (string.IsNullOrWhiteSpace(Goal))
            {
                Goal = null;
                GoalStatus = ThreadGoal.Cleared;
            }
            return;
        }

        Goal = null;
        GoalStatus = ThreadGoal.Cleared;
    }

    public bool TogglePin()
    {
        var next = !ThreadPins.IsPinned(Thread.Id);
        App.SetPinnedAsync(Thread.Id, next).GetAwaiter().GetResult();
        return ThreadPins.IsPinned(Thread.Id);
    }

    public IReadOnlyList<SubagentSnapshot> ListSubagents() =>
        Hosted.TryGetSession(Thread.Id, out var live) ? live.ListSubagents() : [];

    public string ExportMarkdown()
    {
        var items = App.ListItemsAsync().GetAwaiter().GetResult();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# " + (string.IsNullOrWhiteSpace(App.Title) ? Thread.Id : App.Title));
        sb.AppendLine();
        if (items.TryGetProperty("data", out var data) && data.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var kind = item.TryGetProperty("type", out var t) ? t.GetString() : item.TryGetProperty("kind", out var k) ? k.GetString() : "item";
                var text = item.TryGetProperty("text", out var body) ? body.GetString() : item.GetRawText();
                sb.AppendLine("## " + kind);
                sb.AppendLine(text);
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    public async ValueTask DisposeAsync() => await Hosted.DisposeAsync();

    public sealed class ThreadRef(TuiAgent owner)
    {
        public string Id => owner.App.ThreadId;
        public string Title => owner.App.Title;
        public string Path => System.IO.Path.Combine(CodexPaths.SessionsDir, Id + ".jsonl");
    }
}
