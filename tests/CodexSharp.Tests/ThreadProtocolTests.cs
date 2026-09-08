using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using CodexSharp.AppServer;
using CodexSharp.Client;
using CodexSharp.Core;
using CodexSharp.Protocol;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class ThreadGoalTests
{
    [Fact]
    public void Slash_parser_covers_pause_resume_clear()
    {
        Assert.True(GoalSlash.parse("").IsShow);
        Assert.True(GoalSlash.parse("pause").IsPause);
        Assert.True(GoalSlash.parse("resume").IsResume);
        Assert.True(GoalSlash.parse("clear").IsClear);
        var set = Assert.IsType<GoalSlash.Command.Set>(GoalSlash.parse("Finish the port"));
        Assert.Equal("Finish the port", set.Item);
        Assert.Equal("", ThreadGoal.promptBlock("x", ThreadGoal.Paused));
        Assert.Contains("Finish the port", ThreadGoal.promptBlock("Finish the port", ThreadGoal.Active));
    }

    [Fact]
    public async Task Set_pause_resume_clear_over_app_server()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var threadId = await session.StartThreadAsync(Path.GetTempPath(), "g");
            var set = await hosted.Client.CallAsync("thread/goal/set", new { threadId, objective = "Finish the port" });
            Assert.Equal("Finish the port", set.GetProperty("goal").GetProperty("objective").GetString());
            Assert.Equal(ThreadGoal.Active, set.GetProperty("goal").GetProperty("status").GetString());
            var paused = await hosted.Client.CallAsync("thread/goal/set", new { threadId, status = ThreadGoal.Paused });
            Assert.Equal("Finish the port", paused.GetProperty("goal").GetProperty("objective").GetString());
            Assert.Equal(ThreadGoal.Paused, paused.GetProperty("goal").GetProperty("status").GetString());
            var resumed = await hosted.Client.CallAsync("thread/goal/set", new { threadId, status = ThreadGoal.Active });
            Assert.Equal(ThreadGoal.Active, resumed.GetProperty("goal").GetProperty("status").GetString());
            var cleared = await hosted.Client.CallAsync("thread/goal/clear", new { threadId });
            Assert.True(cleared.GetProperty("cleared").GetBoolean());
            var get = await hosted.Client.CallAsync("thread/goal/get", new { threadId });
            Assert.Equal(JsonValueKind.Null, get.GetProperty("goal").ValueKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Resume_reloads_persisted_goal()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var threadId = await session.StartThreadAsync(Path.GetTempPath(), "persist-goal");
            await session.SetGoalAsync("Keep the objective");
            await session.SetGoalAsync(status: ThreadGoal.Paused);
            await session.ResumeThreadAsync(threadId);
            var get = await session.GetGoalAsync();
            Assert.Equal("Keep the objective", get.GetProperty("goal").GetProperty("objective").GetString());
            Assert.Equal(ThreadGoal.Paused, get.GetProperty("goal").GetProperty("status").GetString());

            var cfg = ConfigService.Load(Path.GetTempPath());
            var revived = CodexSession.Resume(threadId, cfg);
            Assert.Equal("Keep the objective", revived.Goal);
            Assert.Equal(ThreadGoal.Paused, revived.GoalStatus);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Active_goal_stays_in_prompt_after_steer()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
            var session = CodexSession.Start(cfg, "goal-steer");
            session.SetGoal("Finish the port");
            var model = new ScriptedModelClient(
                [ModelStreamEvent.NewOutputTextDelta("first"), ModelStreamEvent.NewStreamFinished("stop")],
                [ModelStreamEvent.NewOutputTextDelta("second"), ModelStreamEvent.NewStreamFinished("stop")]);
            var steer = new RecordingSteerHost("keep going");
            var sink = new RecordingSink();
            var deps = new AgentDeps(cfg, model, new BuiltinToolExecutor(cfg), new AutoApprover(true), sink, [], new NoopHookHost(), new NoopUserInputHost(), steer, session.Goal ?? "", session.GoalStatus);
            var history = new List<HistoryMessage>();
            await AgentLoop.runTurn(deps, session.Thread.Id, history, "hello", CancellationToken.None);
            Assert.True(model.Requests.Count >= 2);
            Assert.Contains(model.Requests[0].Messages, m => m.Content.Contains("Finish the port"));
            Assert.Contains(model.Requests[1].Messages, m => m.Content.Contains("Finish the port"));
            Assert.Contains(history, m => m.Role == "user" && m.Content.Contains("keep going"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public void Paused_goal_is_not_injected_into_prompt()
    {
        var cfg = ConfigService.Load(Path.GetTempPath());
        var active = Prompt.buildWithGoal(cfg, [new HistoryMessage("user", "hi", "", "", "")], [], "thr", "Finish the port", ThreadGoal.Active);
        var paused = Prompt.buildWithGoal(cfg, [new HistoryMessage("user", "hi", "", "", "")], [], "thr", "Finish the port", ThreadGoal.Paused);
        Assert.Contains(active.Messages, m => m.Content.Contains("Finish the port") && m.Role == "developer");
        Assert.DoesNotContain(paused.Messages, m => m.Content.Contains("Finish the port") && m.Role == "developer");
    }

    [Fact]
    public async Task OptIn_lmstudio_steer_preserves_goal()
    {
        if (Environment.GetEnvironmentVariable("CODEXSHARP_E2E_LMSTUDIO") != "1")
        {
            return;
        }

        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        HttpResponseMessage response;
        try
        {
            response = await probe.GetAsync("http://127.0.0.1:1234/v1/models");
        }
        catch
        {
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
        {
            return;
        }

        var modelId = data[0].GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        var home = Path.Combine(Path.GetTempPath(), "codexsharp-m1-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        Environment.SetEnvironmentVariable("CODEXSHARP_PROVIDER", "lmstudio");
        Environment.SetEnvironmentVariable("CODEXSHARP_MODEL", modelId);
        Environment.SetEnvironmentVariable("CODEXSHARP_BASE_URL", "http://127.0.0.1:1234/v1");
        Environment.SetEnvironmentVariable("CODEXSHARP_API_KEY", "lm-studio");
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("codexsharp_e2e", "M1");
            await session.StartThreadAsync(Path.GetTempPath(), "e2e-goal");
            await session.SetGoalAsync("Keep this goal text visible");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await session.RunTurnToCompletionAsync("Reply with the single word ok.", ct: cts.Token);
            var after = await session.GetGoalAsync();
            Assert.Equal("Keep this goal text visible", after.GetProperty("goal").GetProperty("objective").GetString());
            Assert.Equal(ThreadGoal.Active, after.GetProperty("goal").GetProperty("status").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
            Environment.SetEnvironmentVariable("CODEXSHARP_PROVIDER", null);
            Environment.SetEnvironmentVariable("CODEXSHARP_MODEL", null);
            Environment.SetEnvironmentVariable("CODEXSHARP_BASE_URL", null);
            Environment.SetEnvironmentVariable("CODEXSHARP_API_KEY", null);
        }
    }
}

file sealed class RecordingSteerHost(string payload) : ISteerHost
{
    private int _calls;
    public string TryDequeue()
    {
        _calls++;
        return _calls == 2 ? payload : "";
    }
}

public class ThreadCompactProtocolTests
{
    [Fact]
    public async Task Compact_start_returns_empty_object()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "c");
        var result = await hosted.Client.CallAsync("thread/compact/start", new { threadId });
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
    }
}

public class RecapAndCopyTests
{
    [Fact]
    public void BuildPrompt_skips_empty_history_and_includes_recent_turns()
    {
        Assert.Null(Recap.BuildPrompt([]));
        var history = new List<HistoryMessage>
        {
            new("user", "Fix the login button", "", "", ""),
            new("assistant", "Updated Login.fs", "", "", ""),
        };
        var prompt = Recap.BuildPrompt(history);
        Assert.NotNull(prompt);
        Assert.Contains("Fix the login button", prompt);
        Assert.Contains("Updated Login.fs", prompt);
        Assert.Contains("Recent conversation", prompt);
    }

    [Fact]
    public async Task Recap_empty_thread_is_honest_and_copy_reads_injected_agent()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            var threadId = await session.StartThreadAsync(Path.GetTempPath(), "recap");
            var empty = await session.RecapAsync();
            Assert.Equal("empty", empty.GetProperty("status").GetString());
            Assert.False(empty.GetProperty("persisted").GetBoolean());
            var none = await session.CopyLastAsync();
            Assert.Equal("empty", none.GetProperty("kind").GetString());
            await hosted.Client.CallAsync("thread/inject_items", new
            {
                threadId,
                items = new[] { new { type = "assistant", text = "hello\n```\nvar x = 1;\n```" } },
            });
            var copied = await session.CopyLastAsync();
            Assert.Equal("message", copied.GetProperty("kind").GetString());
            Assert.Contains("hello", copied.GetProperty("text").GetString());
            var code = await session.CopyLastAsync("code");
            Assert.Equal("code", code.GetProperty("kind").GetString());
            Assert.Contains("var x = 1;", code.GetProperty("text").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ThreadExportAndRolloutTests
{
    [Fact]
    public async Task Export_and_rollout_path_go_through_app_server()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            var threadId = await session.StartThreadAsync(cwd, "export-me");
            var dest = Path.Combine(cwd, "out.md");
            var exported = await session.ExportThreadAsync(dest);
            Assert.Equal(Path.GetFullPath(dest), exported.GetProperty("path").GetString());
            Assert.True(File.Exists(dest));
            Assert.Contains("# export-me", File.ReadAllText(dest));
            var rollout = await session.RolloutPathAsync();
            Assert.Contains(threadId, rollout.GetProperty("path").GetString());
            Assert.True(rollout.GetProperty("exists").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ThreadPinTests
{
    [Fact]
    public void Pins_persist_and_sort_first()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(Path.GetTempPath());
            var store = new JsonlThreadStore();
            var a = store.Create(cfg, "a");
            var b = store.Create(cfg, "b");
            ThreadPins.Set(a.Id, true);
            var listed = store.List();
            Assert.True(listed[0].Pinned);
            Assert.Equal(a.Id, listed[0].Id);
            Assert.True(ThreadPins.IsPinned(a.Id));
            Assert.False(ThreadPins.IsPinned(b.Id));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ThreadUnsubscribeTests
{
    [Fact]
    public async Task Unsubscribing_unloads_thread()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "u");
        await hosted.Client.CallAsync("thread/unsubscribe", new { threadId });
        var loaded = await hosted.Client.CallAsync("thread/loaded/list");
        Assert.Equal(0, loaded.GetProperty("threadIds").GetArrayLength());
    }
}

public class ConversationSummaryTests
{
    [Fact]
    public async Task Summary_returns_thread_title()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "summary-thread");
        var summary = await hosted.Client.CallAsync("getConversationSummary", new { conversationId = threadId });
        Assert.Equal("summary-thread", summary.GetProperty("summary").GetProperty("title").GetString());
    }
}

public class ThreadNameProtocolTests
{
    [Fact]
    public async Task Sets_thread_name()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "old");
        await hosted.Client.CallAsync("thread/name/set", new { threadId, name = "renamed" });
        var read = await hosted.Client.CallAsync("thread/read", new { threadId });
        Assert.Equal("renamed", read.GetProperty("thread").GetProperty("title").GetString());
    }
}

public class ThreadItemsListTests
{
    [Fact]
    public async Task Lists_history_after_start()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "items");
        var listed = await hosted.Client.CallAsync("thread/items/list", new { threadId });
        Assert.True(listed.TryGetProperty("data", out var data));
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
    }

    [Fact]
    public async Task Injects_items_and_honors_itemsView_turnId_and_paging()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "items-view");
        var longText = new string('x', 250);
        await hosted.Client.CallAsync("thread/inject_items", new
        {
            threadId,
            items = new object[]
            {
                new { type = "user_message", text = "first user" },
                new { type = "agent_message", text = "first assistant" },
                new { role = "user", content = longText },
                new { role = "assistant", content = "second assistant" },
            },
        });

        var summary = await hosted.Client.CallAsync("thread/items/list", new { threadId, itemsView = "summary", limit = 10 });
        var data = summary.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(4, data.Length);
        Assert.Equal("trn_0", data[0].GetProperty("turnId").GetString());
        Assert.Equal("user_message", data[0].GetProperty("type").GetString());
        var longItem = data.Single(e => e.GetProperty("type").GetString() == "user_message" && (e.GetProperty("text").GetString() ?? "").Contains('x'));
        Assert.Equal(201, longItem.GetProperty("text").GetString()!.Length);
        Assert.EndsWith("…", longItem.GetProperty("text").GetString());

        var full = await hosted.Client.CallAsync("thread/items/list", new { threadId, itemsView = "full" });
        var fullLong = full.GetProperty("data").EnumerateArray()
            .Single(e => e.GetProperty("type").GetString() == "user_message" && (e.GetProperty("text").GetString() ?? "").StartsWith("xxx"));
        Assert.Equal(longText, fullLong.GetProperty("text").GetString());

        var turn0 = await hosted.Client.CallAsync("thread/items/list", new { threadId, turnId = "trn_0" });
        Assert.Equal(2, turn0.GetProperty("data").GetArrayLength());
        Assert.All(turn0.GetProperty("data").EnumerateArray(), e => Assert.Equal("trn_0", e.GetProperty("turnId").GetString()));

        var page = await hosted.Client.CallAsync("thread/items/list", new { threadId, limit = 2 });
        Assert.Equal(2, page.GetProperty("data").GetArrayLength());
        Assert.Equal("2", page.GetProperty("nextCursor").GetString());
        Assert.Equal("0", page.GetProperty("backwardsCursor").GetString());
        var page2 = await hosted.Client.CallAsync("thread/items/list", new { threadId, limit = 2, cursor = "2" });
        Assert.Equal(2, page2.GetProperty("data").GetArrayLength());
        Assert.Null(page2.GetProperty("nextCursor").GetString());
    }
}

public class ThreadInjectItemsTests
{
    [Fact]
    public async Task Unknown_thread_is_not_loaded()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            hosted.Client.CallAsync("thread/inject_items", new { threadId = "missing", items = Array.Empty<object>() }));
        Assert.Contains("not loaded", ex.Message);
    }
}

public class ThreadRealtimeStubTests
{
    [Fact]
    public async Task Start_requires_experimental_api()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallAsync("thread/realtime/start", new { threadId = "x", outputModality = "text" }));
        Assert.Contains("experimentalApi", ex.Message);
    }

    [Fact]
    public async Task ListVoices_succeeds_but_start_is_notConfigured()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "realtime");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            hosted.Client.CallAsync("thread/realtime/start", new { threadId, outputModality = "text" }));
        Assert.Contains("notConfigured", ex.Message, StringComparison.OrdinalIgnoreCase);
        var voices = await hosted.Client.CallAsync("thread/realtime/listVoices");
        Assert.Equal(JsonValueKind.Array, voices.GetProperty("voices").ValueKind);
        await hosted.Client.CallAsync("thread/realtime/stop", new { threadId });
    }
}

public class ThreadHistoryPersistTests
{
    [Fact]
    public async Task Resume_reloads_saved_history()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
            var session = CodexSession.Start(cfg, "hist");
            var model = new ScriptedModelClient(
            [
                ModelStreamEvent.NewOutputTextDelta("remembered"),
                ModelStreamEvent.NewStreamFinished("stop"),
            ]);
            await session.RunTurnWithAsync(model, new BuiltinToolExecutor(cfg), new AutoApprover(true), "hello persist", CancellationToken.None);
            Assert.Contains(session.History, m => m.Content.Contains("hello persist"));

            var resumed = CodexSession.Resume(session.Thread.Id, cfg);
            Assert.Contains(resumed.History, m => m.Content.Contains("hello persist"));
            Assert.Contains(resumed.History, m => m.Content.Contains("remembered"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task App_server_items_list_after_resume()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var threadId = await session.StartThreadAsync(Path.GetTempPath(), "persist-ui");
            var cfg = ConfigService.Load(Path.GetTempPath(), sandboxOverride: "workspace-write", approvalOverride: "never");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", Path.GetTempPath(), false, 8);
            var live = CodexSession.Resume(threadId, cfg);
            live.RestoreHistory([new HistoryMessage("user", "from disk", "", "", "")]);
            new JsonlThreadStore().SaveHistory(threadId, live.History);

            await session.ResumeThreadAsync(threadId);
            var listed = await session.ListItemsAsync();
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("text").GetString() == "from disk");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ThreadRollbackTests
{
    [Fact]
    public void Drops_last_user_turn()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
            var session = CodexSession.Start(cfg, "rb");
            session.RestoreHistory(
            [
                new HistoryMessage("user", "one", "", "", ""),
                new HistoryMessage("assistant", "a1", "", "", ""),
                new HistoryMessage("user", "two", "", "", ""),
                new HistoryMessage("assistant", "a2", "", "", ""),
            ]);
            Assert.Equal(2, session.Turns().Count);
            Assert.True(session.RollbackTurns(1));
            Assert.Single(session.Turns());
            Assert.Equal("one", session.History[0].Content);
            Assert.DoesNotContain(session.History, m => m.Content == "two");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Turns_list_and_rollback_over_app_server()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "turns");
        var listed = await hosted.Client.CallAsync("thread/turns/list", new { threadId, limit = 1 });
        Assert.Equal(JsonValueKind.Array, listed.GetProperty("data").ValueKind);
        Assert.True(listed.TryGetProperty("nextCursor", out _));
        Assert.True(listed.TryGetProperty("backwardsCursor", out _));
        await hosted.Client.CallAsync("thread/rollback", new { threadId, numTurns = 1 });
    }
}

public class ThreadSearchAndQueueTests
{
    [Fact]
    public void Search_matches_title_and_history()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
            var session = CodexSession.Start(cfg, "unique-zebra-title");
            session.RestoreHistory([new HistoryMessage("user", "remember the pineapple token", "", "", "")]);
            new JsonlThreadStore().SaveHistory(session.Thread.Id, session.History);
            var hits = new JsonlThreadStore().Search("pineapple");
            Assert.Contains(hits, h => h.Snippet.Contains("pineapple"));
            var titled = new JsonlThreadStore().Search("zebra-title");
            Assert.Contains(titled, h => h.Thread.Id == session.Thread.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Queue_add_and_list()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        await session.StartThreadAsync(Path.GetTempPath(), "q");
        await session.QueueAddAsync("later");
        var listed = await session.QueueListAsync();
        Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("text").GetString() == "later");
    }
}

public class ThreadSettingsUpdateTests
{
    [Fact]
    public async Task Updates_model_on_loaded_thread()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "settings");
        await hosted.Client.CallAsync("thread/settings/update", new { threadId, model = "gpt-test-settings" });
        var read = await hosted.Client.CallAsync("thread/read", new { threadId });
        Assert.Equal("gpt-test-settings", read.GetProperty("thread").GetProperty("model").GetString());
    }
}

public class ThreadTimelineTests
{
    [Fact]
    public async Task Requires_experimental_api()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallAsync("thread/timeline/list", new { threadId = "x" }));
        Assert.Contains("experimentalApi", ex.Message);
    }

    [Fact]
    public async Task Returns_turn_boundaries()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "tl");
        var listed = await hosted.Client.CallAsync("thread/timeline/list", new { threadId });
        Assert.Equal(JsonValueKind.Array, listed.GetProperty("data").ValueKind);
    }
}

public class ThreadSearchOccurrencesTests
{
    [Fact]
    public void Finds_case_insensitive_snippet()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
            var session = CodexSession.Start(cfg, "occ");
            session.RestoreHistory(
            [
                new HistoryMessage("user", "please find the Pineapple token", "", "", ""),
                new HistoryMessage("assistant", "no fruit here", "", "", ""),
            ]);
            var hits = session.SearchOccurrences("pineapple");
            Assert.Single(hits);
            Assert.Contains("Pineapple", hits[0].Snippet);
            Assert.True(hits[0].End > hits[0].Start);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task App_server_requires_experimental_api()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallAsync("thread/searchOccurrences", new { threadId = "x", searchTerm = "a" }));
        Assert.Contains("experimentalApi", ex.Message);
    }
}

public class ThreadSectionProtocolTests
{
    [Fact]
    public async Task Create_list_move_update_and_delete()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var threadId = await session.StartThreadAsync(Path.GetTempPath(), "sectioned");
            var created = await hosted.Client.CallAsync("threadSection/create", new { name = "Work", appearance = new { color = "#f5c36c", icon = "folder" } });
            var sectionId = created.GetProperty("section").GetProperty("id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(sectionId));
            Assert.Equal("Work", created.GetProperty("section").GetProperty("name").GetString());

            await hosted.Client.CallAsync("thread/section/move", new { threadId, sectionId });
            Assert.Equal(sectionId, ThreadSections.SectionOf(threadId));

            var listed = await hosted.Client.CallAsync("threadSection/list", new { limit = 10 });
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("id").GetString() == sectionId);

            var updated = await hosted.Client.CallAsync("threadSection/update", new { sectionId, name = "Shipped" });
            Assert.Equal("Shipped", updated.GetProperty("section").GetProperty("name").GetString());

            await hosted.Client.CallAsync("thread/section/move", new { threadId, sectionId = (string?)null });
            Assert.Null(ThreadSections.SectionOf(threadId));

            await hosted.Client.CallAsync("threadSection/delete", new { sectionId });
            var after = await hosted.Client.CallAsync("threadSection/list");
            Assert.DoesNotContain(after.GetProperty("data").EnumerateArray(), e => e.GetProperty("id").GetString() == sectionId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ThreadListFilterTests
{
    [Fact]
    public async Task Lists_active_then_archived_and_unarchives()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var threadId = await session.StartThreadAsync(Path.GetTempPath(), "list-me");
            var listed = await hosted.Client.CallAsync("thread/list", new { limit = 20 });
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("thread").GetProperty("id").GetString() == threadId);
            Assert.True(listed.TryGetProperty("nextCursor", out _));

            await session.ArchiveAsync();
            var archived = await hosted.Client.CallAsync("thread/list", new { archived = true });
            Assert.Contains(archived.GetProperty("data").EnumerateArray(), e => e.GetProperty("thread").GetProperty("id").GetString() == threadId);
            var active = await hosted.Client.CallAsync("thread/list", new { archived = false });
            Assert.DoesNotContain(active.GetProperty("data").EnumerateArray(), e => e.GetProperty("thread").GetProperty("id").GetString() == threadId);

            await hosted.Client.CallAsync("thread/unarchive", new { threadId });
            var restored = await hosted.Client.CallAsync("thread/list");
            Assert.Contains(restored.GetProperty("data").EnumerateArray(), e => e.GetProperty("thread").GetProperty("id").GetString() == threadId);

            await hosted.Client.CallAsync("thread/delete", new { threadId });
            var after = await hosted.Client.CallAsync("thread/list");
            Assert.DoesNotContain(after.GetProperty("data").EnumerateArray(), e => e.GetProperty("thread").GetProperty("id").GetString() == threadId);
            var other = await session.StartThreadAsync(Path.GetTempPath(), "other");
            await session.ArchiveAsync(other);
            var archivedOther = await hosted.Client.CallAsync("thread/list", new { archived = true });
            Assert.Contains(archivedOther.GetProperty("data").EnumerateArray(), e => e.GetProperty("thread").GetProperty("id").GetString() == other);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Rejects_hosted_originators_filter()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallAsync("thread/list", new { originators = new[] { "vscode" } }));
        Assert.Contains("hosted-only", ex.Message);
    }
}

public class QueueUpdateReorderTests
{
    [Fact]
    public async Task Update_and_reorder_queued_submissions()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        await session.StartThreadAsync(Path.GetTempPath(), "q2");
        await session.QueueAddAsync("one");
        await session.QueueAddAsync("two");
        var listed = await session.QueueListAsync();
        var ids = listed.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(2, ids.Length);
        await session.QueueUpdateAsync(ids[0], "one-edited");
        await session.QueueReorderAsync([ids[1], ids[0]]);
        listed = await session.QueueListAsync();
        var texts = listed.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("text").GetString() ?? "").ToArray();
        Assert.Equal(new[] { "two", "one-edited" }, texts);
    }
}

public class ThreadLifecycleTests
{
    [Fact]
    public void Finds_last_patch_and_archives()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            File.WriteAllText(Path.Combine(cwd, "a.txt"), "old\n");
            var cfg = ConfigService.Load(cwd);
            var store = new JsonlThreadStore();
            var thread = store.Create(cfg, "patch");
            store.SaveHistory(thread.Id, [
                new HistoryMessage("user", "edit", "", "", ""),
                new HistoryMessage("tool", "*** Begin Patch\n*** Update File: a.txt\n@@\n-old\n+new\n*** End Patch", "c1", "apply_patch", ""),
            ]);
            var patch = store.FindLastPatch(thread.Id);
            Assert.Contains("*** Begin Patch", patch);
            var applied = ApplyPatchEngine.Apply(patch!, new WorkspaceSandbox(cfg));
            Assert.True(applied.Ok, applied.Output);
            Assert.Contains("new", File.ReadAllText(Path.Combine(cwd, "a.txt")));

            store.Archive(thread.Id);
            Assert.DoesNotContain(store.List(), t => t.Id == thread.Id);
            Assert.Contains(store.List(true), t => t.Id == thread.Id);
            Assert.True(store.Unarchive(thread.Id));
            Assert.Contains(store.List(), t => t.Id == thread.Id);
            Assert.True(store.Delete(thread.Id));
            Assert.Null(store.Find(thread.Id));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ThreadQueueStoreTests
{
    [Fact]
    public void Add_list_reorder_dequeue()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var a = ThreadQueueStore.Add("thr_test", "one");
            var b = ThreadQueueStore.Add("thr_test", "two");
            Assert.Equal(2, ThreadQueueStore.List("thr_test").Count);
            ThreadQueueStore.Update("thr_test", a.Id, "one-edited");
            ThreadQueueStore.Reorder("thr_test", [b.Id, a.Id]);
            var texts = ThreadQueueStore.List("thr_test").Select(x => x.Text).ToArray();
            Assert.Equal(new[] { "two", "one-edited" }, texts);
            Assert.Equal("two", ThreadQueueStore.Dequeue("thr_test")!.Text);
            Assert.Single(ThreadQueueStore.List("thr_test"));
            Assert.True(ThreadQueueStore.Delete("thr_test", a.Id));
            Assert.Empty(ThreadQueueStore.List("thr_test"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ThreadReadSubagentsAndPinTests
{
    [Fact]
    public async Task Thread_read_includes_subagents_and_pin_goes_through_metadata()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            var threadId = await session.StartThreadAsync(Path.GetTempPath(), "pin-thread");
            var read = await session.ReadThreadAsync();
            Assert.True(read.GetProperty("thread").TryGetProperty("subagents", out var agents));
            Assert.Equal(System.Text.Json.JsonValueKind.Array, agents.ValueKind);
            Assert.Equal(0, agents.GetArrayLength());
            Assert.False(ThreadPins.IsPinned(threadId));
            await session.SetPinnedAsync(threadId, true);
            Assert.True(ThreadPins.IsPinned(threadId));
            await session.SetGoalAsync("ship the port");
            var goal = await session.GetGoalAsync();
            Assert.Equal("ship the port", goal.GetProperty("goal").GetProperty("objective").GetString());
            Assert.True(hosted.TryGetSession(threadId, out var live));
            Assert.Empty(live.ListSubagents());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ThreadSourcesTests
{
    [Fact]
    public void Set_and_get_thread_source()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            CodexPaths.EnsureLayout();
            ThreadSources.Set("thr_a", "cli");
            Assert.Equal("cli", ThreadSources.Get("thr_a"));
            Assert.Null(ThreadSources.Get("missing"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class TurnSettingsUpdateTests
{
    [Fact]
    public async Task Idle_thread_is_target_unavailable()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        await session.StartThreadAsync(Path.GetTempPath(), "t");
        var result = await hosted.Client.CallAsync("turn/settings/update", new { threadId = session.ThreadId, turnId = "none", model = "gpt-5" });
        Assert.Equal("targetUnavailable", result.GetProperty("status").GetString());
    }
}

public class TokenUsageAndRequirementsTests
{
    [Fact]
    public async Task Turn_emits_local_token_estimate()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
            var session = CodexSession.Start(cfg, "tok");
            AgentEvent.TokenUsage? usage = null;
            session.Event += e =>
            {
                if (e is AgentEvent.TokenUsage t) usage = t;
            };
            var model = new ScriptedModelClient(
                [
                    ModelStreamEvent.NewOutputTextDelta("hello tokens"),
                    ModelStreamEvent.NewStreamFinished("stop"),
                ]);
            await session.RunTurnWithAsync(model, null, new AutoApprover(true), "count me");
            Assert.NotNull(usage);
            Assert.True(usage!.Total >= 0);
            var (total, last) = session.EstimateTokens();
            Assert.Equal(total, usage.Total);
            Assert.True(last >= 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Config_requirements_are_null_without_mdm()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var req = await hosted.Client.CallAsync("configRequirements/read");
        Assert.Equal(JsonValueKind.Null, req.GetProperty("requirements").ValueKind);
        Assert.False(req.GetProperty("allowBrowserAndComputerUse").GetBoolean());
    }
}

public class AppsAndBackgroundTerminalsTests
{
    [Fact]
    public async Task App_list_is_empty_and_background_terminals_list()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            CodexPaths.EnsureLayout();
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("tests", "tests");
            await session.StartThreadAsync();
            var apps = await session.ListAppsAsync();
            Assert.True(apps.TryGetProperty("data", out var data));
            Assert.Equal(JsonValueKind.Array, data.ValueKind);
            Assert.Equal(0, data.GetArrayLength());
            var terms = await session.ListBackgroundTerminalsAsync();
            Assert.True(terms.TryGetProperty("data", out var tdata));
            Assert.Equal(JsonValueKind.Array, tdata.ValueKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ReviewStartTests
{
    [Fact]
    public async Task Review_start_accepts_loaded_thread()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "r");
        var result = await hosted.Client.CallAsync("review/start", new { threadId });
        Assert.Equal(threadId, result.GetProperty("reviewThreadId").GetString());
    }
}

