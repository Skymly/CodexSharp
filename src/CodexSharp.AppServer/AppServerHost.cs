using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using CodexSharp.Core;
using CodexSharp.Protocol;
using CodexSharp.Runtime;

namespace CodexSharp.AppServer;

public sealed partial class AppServerHost
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Dictionary<string, CodexSession> _threads = new();
    private readonly Dictionary<string, CancellationTokenSource> _turnCts = new();
    private readonly Dictionary<string, FileSystemWatcher> _watches = new();
    private readonly Dictionary<string, string[]> _fuzzySessions = new();
    private readonly Dictionary<string, List<object>> _backgroundTerminals = new();
    private readonly Dictionary<string, int> _elicitations = new();
    private readonly HashSet<string> _mcpStreams = new(StringComparer.Ordinal);
    private readonly CommandExecBroker _exec = new();
    private readonly Dictionary<string, CancellationTokenSource> _logins = new();
    private readonly Dictionary<string, PendingServerRequest> _serverRequests = new(StringComparer.Ordinal);
    private bool _initialized;
    private bool _experimentalApi;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public AppServerHost(TextReader? input = null, TextWriter? output = null)
    {
        _input = input ?? Console.In;
        _output = output ?? Console.Out;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await _input.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                await HandleLineAsync(line, ct);
            }
            catch (Exception ex)
            {
                Notify("error", new { message = ex.Message });
            }
        }
    }

    private async Task HandleLineAsync(string line, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var method = root.TryGetProperty("method", out var methodEl) ? methodEl.GetString() ?? "" : "";
        var id = root.TryGetProperty("id", out var idEl) ? idEl.Clone() : default;
        var hasId = root.TryGetProperty("id", out _);
        var paramsEl = root.TryGetProperty("params", out var p) ? p : default;

        if (string.IsNullOrEmpty(method))
        {
            if (hasId) HandleServerResponse(id, root);
            return;
        }

        if (method == "initialized")
        {
            return;
        }

        if (method != "initialize" && !_initialized)
        {
            if (hasId) Error(id, -32000, "Not initialized");
            return;
        }

        switch (method)
        {
            case "initialize":
                await DispatchInitializeAsync(id, hasId, paramsEl, method, ct);
                break;
            case "mock/experimentalMethod":
                await DispatchMockExperimentalMethodAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/start":
                await DispatchThreadStartAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/resume":
                await DispatchThreadResumeAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/list":
                await DispatchThreadListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/unsubscribe":
                await DispatchThreadUnsubscribeAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/archive":
                await DispatchThreadArchiveAsync(id, hasId, paramsEl, method, ct);
                break;
            case "turn/start":
                await DispatchTurnStartAsync(id, hasId, paramsEl, method, ct);
                break;
            case "turn/settings/update":
                await DispatchTurnSettingsUpdateAsync(id, hasId, paramsEl, method, ct);
                break;
            case "turn/interrupt":
                await DispatchTurnInterruptAsync(id, hasId, paramsEl, method, ct);
                break;
            case "turn/steer":
                await DispatchTurnSteerAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/fork":
                await DispatchThreadForkAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/read":
                await DispatchThreadReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/loaded/list":
                await DispatchThreadLoadedListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/goal/set":
                await DispatchThreadGoalSetAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/goal/get":
                await DispatchThreadGoalGetAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/goal/clear":
                await DispatchThreadGoalClearAsync(id, hasId, paramsEl, method, ct);
                break;
            case "scheduled/list":
                await DispatchScheduledListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "scheduled/create":
                await DispatchScheduledCreateAsync(id, hasId, paramsEl, method, ct);
                break;
            case "scheduled/cancel":
                await DispatchScheduledCancelAsync(id, hasId, paramsEl, method, ct);
                break;
            case "scheduled/runs":
                await DispatchScheduledRunsAsync(id, hasId, paramsEl, method, ct);
                break;
            case "scheduled/tick":
                await DispatchScheduledTickAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/increment_elicitation":
                await DispatchThreadIncrementElicitationAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/decrement_elicitation":
                await DispatchThreadDecrementElicitationAsync(id, hasId, paramsEl, method, ct);
                break;
            case "currentTime/read":
                await DispatchCurrentTimeReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "mcpServer/event/stream/start":
                await DispatchMcpServerEventStreamStartAsync(id, hasId, paramsEl, method, ct);
                break;
            case "mcpServer/event/stream/stop":
                await DispatchMcpServerEventStreamStopAsync(id, hasId, paramsEl, method, ct);
                break;
            case "environment/info":
                await DispatchEnvironmentInfoAsync(id, hasId, paramsEl, method, ct);
                break;
            case "environment/add":
                await DispatchEnvironmentAddAsync(id, hasId, paramsEl, method, ct);
                break;
            case "environment/status":
                await DispatchEnvironmentStatusAsync(id, hasId, paramsEl, method, ct);
                break;
            case "diagnostics/doctor":
                await DispatchDiagnosticsDoctorAsync(id, hasId, paramsEl, method, ct);
                break;
            case "execPolicy/check":
                await DispatchExecPolicyCheckAsync(id, hasId, paramsEl, method, ct);
                break;
            case "execPolicy/list":
                await DispatchExecPolicyListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "execPolicy/addUserRule":
                await DispatchExecPolicyAddUserRuleAsync(id, hasId, paramsEl, method, ct);
                break;
            case "sandbox/extraReadRoot/list":
                await DispatchSandboxExtraReadRootListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "sandbox/extraReadRoot/add":
                await DispatchSandboxExtraReadRootAddAsync(id, hasId, paramsEl, method, ct);
                break;
            case "lastPatch/apply":
                await DispatchLastPatchApplyAsync(id, hasId, paramsEl, method, ct);
                break;
            case "git/worktree/add":
                await DispatchGitWorktreeAddAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/recap/start":
                await DispatchThreadRecapStartAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/copy":
                await DispatchThreadCopyAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/export":
                await DispatchThreadExportAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/rollout/path":
                await DispatchThreadRolloutPathAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/handoff":
                await DispatchThreadHandoffAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/worktree/start":
                await DispatchThreadWorktreeStartAsync(id, hasId, paramsEl, method, ct);
                break;
            case "agentsMarkdown/write":
                await DispatchAgentsMarkdownWriteAsync(id, hasId, paramsEl, method, ct);
                break;
            case "server/diagnostics":
                await DispatchServerDiagnosticsAsync(id, hasId, paramsEl, method, ct);
                break;
            case "collaborationMode/list":
                await DispatchCollaborationModeListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "experimentalFeature/list":
                await DispatchExperimentalFeatureListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "experimentalFeature/enablement/set":
                await DispatchExperimentalFeatureEnablementSetAsync(id, hasId, paramsEl, method, ct);
                break;
            case "permissionProfile/list":
                await DispatchPermissionProfileListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "gitDiffToRemote":
                await DispatchGitDiffToRemoteAsync(id, hasId, paramsEl, method, ct);
                break;
            case "getConversationSummary":
                await DispatchGetConversationSummaryAsync(id, hasId, paramsEl, method, ct);
                break;
            case "model/list":
                await DispatchModelListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "review/start":
                await DispatchReviewStartAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/compact/start":
                await DispatchThreadCompactStartAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/backgroundTerminals/list":
                await DispatchThreadBackgroundTerminalsListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/backgroundTerminals/clean":
                await DispatchThreadBackgroundTerminalsCleanAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/backgroundTerminals/terminate":
                await DispatchThreadBackgroundTerminalsTerminateAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/shellCommand":
                await DispatchThreadShellCommandAsync(id, hasId, paramsEl, method, ct);
                break;
            case "config/value/write":
                await DispatchConfigValueWriteAsync(id, hasId, paramsEl, method, ct);
                break;
            case "config/batchWrite":
                await DispatchConfigBatchWriteAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/turns/list":
                await DispatchThreadTurnsListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/search":
                await DispatchThreadSearchAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/queue/add":
                await DispatchThreadQueueAddAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/queue/list":
                await DispatchThreadQueueListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/queue/delete":
                await DispatchThreadQueueDeleteAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/queue/update":
                await DispatchThreadQueueUpdateAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/queue/reorder":
                await DispatchThreadQueueReorderAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/queue/start":
                await DispatchThreadQueueStartAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/searchOccurrences":
                await DispatchThreadSearchOccurrencesAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/timeline/list":
                await DispatchThreadTimelineListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/rollback":
                await DispatchThreadRollbackAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/revert":
                await DispatchThreadRevertAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/items/list":
                await DispatchThreadItemsListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "app/list":
            case "app/installed":
                await DispatchAppListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "project/list":
                await DispatchProjectListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "project/read":
                await DispatchProjectReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "project/create":
                await DispatchProjectCreateAsync(id, hasId, paramsEl, method, ct);
                break;
            case "project/import":
                await DispatchProjectImportAsync(id, hasId, paramsEl, method, ct);
                break;
            case "project/update":
                await DispatchProjectUpdateAsync(id, hasId, paramsEl, method, ct);
                break;
            case "project/move":
                await DispatchProjectMoveAsync(id, hasId, paramsEl, method, ct);
                break;
            case "project/delete":
                await DispatchProjectDeleteAsync(id, hasId, paramsEl, method, ct);
                break;
            case "project/trust/read":
                await DispatchProjectTrustReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "project/trust/set":
                await DispatchProjectTrustSetAsync(id, hasId, paramsEl, method, ct);
                break;
            case "config/write":
                await DispatchConfigWriteAsync(id, hasId, paramsEl, method, ct);
                break;
            case "fs/readFile":
                await DispatchFsReadFileAsync(id, hasId, paramsEl, method, ct);
                break;
            case "fs/writeFile":
                await DispatchFsWriteFileAsync(id, hasId, paramsEl, method, ct);
                break;
            case "fs/createDirectory":
                await DispatchFsCreateDirectoryAsync(id, hasId, paramsEl, method, ct);
                break;
            case "fs/getMetadata":
                await DispatchFsGetMetadataAsync(id, hasId, paramsEl, method, ct);
                break;
            case "fs/readDirectory":
                await DispatchFsReadDirectoryAsync(id, hasId, paramsEl, method, ct);
                break;
            case "fs/remove":
                await DispatchFsRemoveAsync(id, hasId, paramsEl, method, ct);
                break;
            case "fs/copy":
                await DispatchFsCopyAsync(id, hasId, paramsEl, method, ct);
                break;
            case "fs/watch":
                await DispatchFsWatchAsync(id, hasId, paramsEl, method, ct);
                break;
            case "fs/unwatch":
                await DispatchFsUnwatchAsync(id, hasId, paramsEl, method, ct);
                break;
            case "plugin/list":
            case "plugin/installed":
                await DispatchPluginListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "plugin/search":
                await DispatchPluginSearchAsync(id, hasId, paramsEl, method, ct);
                break;
            case "plugin/reconcile":
                await DispatchPluginReconcileAsync(id, hasId, paramsEl, method, ct);
                break;
            case "plugin/skill/read":
                await DispatchPluginSkillReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "plugin/read":
                await DispatchPluginReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "plugin/install":
                await DispatchPluginInstallAsync(id, hasId, paramsEl, method, ct);
                break;
            case "plugin/share/save":
                await DispatchPluginShareSaveAsync(id, hasId, paramsEl, method, ct);
                break;
            case "plugin/share/list":
                await DispatchPluginShareListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "plugin/share/delete":
                await DispatchPluginShareDeleteAsync(id, hasId, paramsEl, method, ct);
                break;
            case "plugin/share/checkout":
                await DispatchPluginShareCheckoutAsync(id, hasId, paramsEl, method, ct);
                break;
            case "plugin/share/updateTargets":
                await DispatchPluginShareUpdateTargetsAsync(id, hasId, paramsEl, method, ct);
                break;
            case "plugin/uninstall":
                await DispatchPluginUninstallAsync(id, hasId, paramsEl, method, ct);
                break;
            case "marketplace/list":
                await DispatchMarketplaceListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "marketplace/add":
                await DispatchMarketplaceAddAsync(id, hasId, paramsEl, method, ct);
                break;
            case "marketplace/remove":
                await DispatchMarketplaceRemoveAsync(id, hasId, paramsEl, method, ct);
                break;
            case "marketplace/upgrade":
                await DispatchMarketplaceUpgradeAsync(id, hasId, paramsEl, method, ct);
                break;
            case "threadSection/list":
                await DispatchThreadSectionListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "threadSection/create":
                await DispatchThreadSectionCreateAsync(id, hasId, paramsEl, method, ct);
                break;
            case "threadSection/update":
                await DispatchThreadSectionUpdateAsync(id, hasId, paramsEl, method, ct);
                break;
            case "threadSection/delete":
                await DispatchThreadSectionDeleteAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/section/move":
                await DispatchThreadSectionMoveAsync(id, hasId, paramsEl, method, ct);
                break;
            case "hooks/list":
                await DispatchHooksListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "configRequirements/read":
                await DispatchConfigRequirementsReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "skills/extraRoots/set":
                await DispatchSkillsExtraRootsSetAsync(id, hasId, paramsEl, method, ct);
                break;
            case "prompts/list":
                await DispatchPromptsListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "prompts/read":
                await DispatchPromptsReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "skills/list":
                await DispatchSkillsListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "skills/config/write":
                await DispatchSkillsConfigWriteAsync(id, hasId, paramsEl, method, ct);
                break;
            case "model/provider/capabilities/read":
            case "modelProvider/capabilities/read":
                await DispatchModelProviderCapabilitiesReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "app/read":
            case "apps/read":
                await DispatchAppReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "mcpServer/oauth/login":
                await DispatchMcpServerOauthLoginAsync(id, hasId, paramsEl, method, ct);
                break;
            case "mcpServer/resource/read":
                await DispatchMcpServerResourceReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "config/mcpServer/reload":
                await DispatchConfigMcpServerReloadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "mcpServer/tool/call":
                await DispatchMcpServerToolCallAsync(id, hasId, paramsEl, method, ct);
                break;
            case "mcpServerStatus/list":
                await DispatchMcpServerStatusListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "mcpServer/add":
                await DispatchMcpServerAddAsync(id, hasId, paramsEl, method, ct);
                break;
            case "mcpServer/remove":
                await DispatchMcpServerRemoveAsync(id, hasId, paramsEl, method, ct);
                break;
            case "fuzzyFileSearch/sessionStart":
                await DispatchFuzzyFileSearchSessionStartAsync(id, hasId, paramsEl, method, ct);
                break;
            case "fuzzyFileSearch/sessionUpdate":
                await DispatchFuzzyFileSearchSessionUpdateAsync(id, hasId, paramsEl, method, ct);
                break;
            case "fuzzyFileSearch/sessionStop":
                await DispatchFuzzyFileSearchSessionStopAsync(id, hasId, paramsEl, method, ct);
                break;
            case "fuzzyFileSearch":
                await DispatchFuzzyFileSearchAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/metadata/update":
                await DispatchThreadMetadataUpdateAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/memoryMode/set":
                await DispatchThreadMemoryModeSetAsync(id, hasId, paramsEl, method, ct);
                break;
            case "memory/list":
                await DispatchMemoryListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "memory/reset":
                await DispatchMemoryResetAsync(id, hasId, paramsEl, method, ct);
                break;
            case "memory/read":
                await DispatchMemoryReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "memory/write":
                await DispatchMemoryWriteAsync(id, hasId, paramsEl, method, ct);
                break;
            case "tui/keymap/list":
                await DispatchTuiKeymapListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "tui/keymap/set":
                await DispatchTuiKeymapSetAsync(id, hasId, paramsEl, method, ct);
                break;
            case "tui/pets/read":
                await DispatchTuiPetsReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "tui/pets/set":
                await DispatchTuiPetsSetAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/settings/update":
                await DispatchThreadSettingsUpdateAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/name/set":
                await DispatchThreadNameSetAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/unarchive":
                await DispatchThreadUnarchiveAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/delete":
                await DispatchThreadDeleteAsync(id, hasId, paramsEl, method, ct);
                break;
            case "config/read":
                await DispatchConfigReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "account/usage/read":
                await DispatchAccountUsageReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "account/rateLimits/read":
                await DispatchAccountRateLimitsReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "account/read":
                await DispatchAccountReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "getAuthStatus":
                await DispatchGetAuthStatusAsync(id, hasId, paramsEl, method, ct);
                break;
            case "account/login/start":
                await DispatchAccountLoginStartAsync(id, hasId, paramsEl, method, ct);
                break;
            case "account/bedrock/discover":
                await DispatchAccountBedrockDiscoverAsync(id, hasId, paramsEl, method, ct);
                break;
            case "account/bedrock/setup":
                await DispatchAccountBedrockSetupAsync(id, hasId, paramsEl, method, ct);
                break;
            case "account/rateLimitResetCredit/consume":
                await DispatchAccountRateLimitResetCreditConsumeAsync(id, hasId, paramsEl, method, ct);
                break;
            case "account/sendAddCreditsNudgeEmail":
                await DispatchAccountSendAddCreditsNudgeEmailAsync(id, hasId, paramsEl, method, ct);
                break;
            case "account/login/cancel":
                await DispatchAccountLoginCancelAsync(id, hasId, paramsEl, method, ct);
                break;
            case "account/chatgptAuthTokens/refresh":
                await DispatchAccountChatgptAuthTokensRefreshAsync(id, hasId, paramsEl, method, ct);
                break;
            case "account/workspaceMessages/read":
                await DispatchAccountWorkspaceMessagesReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "item/tool/call":
                await DispatchItemToolCallAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/approveGuardianDeniedAction":
                await DispatchThreadApproveGuardianDeniedActionAsync(id, hasId, paramsEl, method, ct);
                break;
            case "attestation/generate":
                await DispatchAttestationGenerateAsync(id, hasId, paramsEl, method, ct);
                break;
            case "externalAgentConfig/import/recordHistory":
                await DispatchExternalAgentConfigImportRecordHistoryAsync(id, hasId, paramsEl, method, ct);
                break;
            case "account/logout":
                await DispatchAccountLogoutAsync(id, hasId, paramsEl, method, ct);
                break;
            case "remoteControl/status/read":
                await DispatchRemoteControlStatusReadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "remoteControl/enable":
                await DispatchRemoteControlEnableAsync(id, hasId, paramsEl, method, ct);
                break;
            case "remoteControl/disable":
                await DispatchRemoteControlDisableAsync(id, hasId, paramsEl, method, ct);
                break;
            case "remoteControl/pairing/start":
                await DispatchRemoteControlPairingStartAsync(id, hasId, paramsEl, method, ct);
                break;
            case "remoteControl/pairing/status":
                await DispatchRemoteControlPairingStatusAsync(id, hasId, paramsEl, method, ct);
                break;
            case "remoteControl/client/list":
                await DispatchRemoteControlClientListAsync(id, hasId, paramsEl, method, ct);
                break;
            case "remoteControl/client/revoke":
                await DispatchRemoteControlClientRevokeAsync(id, hasId, paramsEl, method, ct);
                break;
            case "command/exec":
                await DispatchCommandExecAsync(id, hasId, paramsEl, method, ct);
                break;
            case "process/spawn":
                await DispatchProcessSpawnAsync(id, hasId, paramsEl, method, ct);
                break;
            case "process/writeStdin":
                await DispatchProcessWriteStdinAsync(id, hasId, paramsEl, method, ct);
                break;
            case "process/kill":
                await DispatchProcessKillAsync(id, hasId, paramsEl, method, ct);
                break;
            case "process/resizePty":
                await DispatchProcessResizePtyAsync(id, hasId, paramsEl, method, ct);
                break;
            case "externalAgentConfig/detect":
                await DispatchExternalAgentConfigDetectAsync(id, hasId, paramsEl, method, ct);
                break;
            case "externalAgentConfig/import":
                await DispatchExternalAgentConfigImportAsync(id, hasId, paramsEl, method, ct);
                break;
            case "externalAgentConfig/import/readHistories":
                await DispatchExternalAgentConfigImportReadHistoriesAsync(id, hasId, paramsEl, method, ct);
                break;
            case "feedback/upload":
                await DispatchFeedbackUploadAsync(id, hasId, paramsEl, method, ct);
                break;
            case "windowsSandbox/readiness":
                await DispatchWindowsSandboxReadinessAsync(id, hasId, paramsEl, method, ct);
                break;
            case "windowsSandbox/setupStart":
                await DispatchWindowsSandboxSetupStartAsync(id, hasId, paramsEl, method, ct);
                break;
            case "mcpServer/elicitation/respond":
                await DispatchMcpServerElicitationRespondAsync(id, hasId, paramsEl, method, ct);
                break;
            case "item/tool/respondUserInput":
                await DispatchItemToolRespondUserInputAsync(id, hasId, paramsEl, method, ct);
                break;
            case "item/permissions/respond":
            case "approval/respond":
                await DispatchItemPermissionsRespondAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/inject_items":
            case "thread/injectItems":
                await DispatchThreadInjectItemsAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/realtime/start":
            case "thread/realtime/appendAudio":
            case "thread/realtime/appendText":
            case "thread/realtime/appendSpeech":
                await DispatchThreadRealtimeStartAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/realtime/stop":
                await DispatchThreadRealtimeStopAsync(id, hasId, paramsEl, method, ct);
                break;
            case "thread/realtime/listVoices":
                await DispatchThreadRealtimeListVoicesAsync(id, hasId, paramsEl, method, ct);
                break;
            case "command/exec/write":
                await DispatchCommandExecWriteAsync(id, hasId, paramsEl, method, ct);
                break;
            case "command/exec/terminate":
                await DispatchCommandExecTerminateAsync(id, hasId, paramsEl, method, ct);
                break;
            case "command/exec/resize":
                await DispatchCommandExecResizeAsync(id, hasId, paramsEl, method, ct);
                break;
            default:
                if (hasId) Error(id, -32601, $"Unknown method {method}");
                break;
        }
    }

    private sealed record PendingServerRequest(string ThreadId, string ApprovalId, string Kind);

    private object[] QueueDto(string threadId) =>
        ThreadQueueStore.List(threadId).Select(x => new { id = x.Id, text = x.Text }).ToArray();

    private void StartTurn(CodexSession session, string text, CancellationToken ct)
    {
        var threadId = session.Thread.Id;
        var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _turnCts[threadId] = turnCts;
        _ = Task.Run(async () =>
        {
            try
            {
                await session.RunTurnAsync(text, turnCts.Token);
            }
            finally
            {
                _turnCts.Remove(threadId);
                DrainQueue(session, ct);
            }
        }, turnCts.Token);
    }

    private void DrainQueue(CodexSession session, CancellationToken ct)
    {
        var threadId = session.Thread.Id;
        if (_turnCts.ContainsKey(threadId))
        {
            return;
        }

        var next = ThreadQueueStore.Dequeue(threadId);

        if (next is null)
        {
            return;
        }

        Notify("thread/queue/changed", new { threadId, data = QueueDto(threadId) });
        StartTurn(session, next.Text, ct);
    }

    private static bool Flag(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind is JsonValueKind.True;

    private static bool HasPermissionProfile(JsonElement el) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty("permissionProfile", out var v) && v.ValueKind == JsonValueKind.String;

    private static List<string> ReadCommandArgv(JsonElement paramsEl)
    {
        var argv = new List<string>();
        if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("command", out var cmdEl))
        {
            if (cmdEl.ValueKind == JsonValueKind.Array)
            {
                argv.AddRange(cmdEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));
            }
            else if (cmdEl.ValueKind == JsonValueKind.String)
            {
                var text = cmdEl.GetString() ?? "";
                if (text.Length > 0)
                {
                    argv.Add(text);
                }
            }
        }

        if (argv.Count == 0)
        {
            var text = ReadInputText(paramsEl);
            if (!string.IsNullOrWhiteSpace(text))
            {
                argv.Add(text);
            }
        }

        return argv;
    }

    private static bool HasGranularApproval(JsonElement el) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty("approvalPolicy", out var ap)
        && ap.ValueKind == JsonValueKind.Object
        && ap.TryGetProperty("granular", out _);

    private bool RequireExperimental(JsonElement id, bool hasId, string method)
    {
        if (_experimentalApi)
        {
            return true;
        }

        if (hasId)
        {
            Error(id, -32601, $"{method} requires experimentalApi capability");
        }

        return false;
    }

    private static bool CommandExecNeedsPrompt(CodexConfig cfg, IReadOnlyList<string> argv)
    {
        if (string.Equals(cfg.ApprovalPolicy, "never", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ExecPolicy.Evaluate(string.Join(' ', argv), cfg.Cwd) == ExecDecision.Prompt;
    }

    private static async Task<string?> DenyReasonAsync(CodexSession session, ToolCallRequest call, CancellationToken ct)
    {
        var requestId = Ids.approval();
        var reason = "tool " + call.Name;
        session.Raise(AgentEvent.NewApprovalNeeded(requestId, call.ArgumentsJson, session.Config.Cwd, reason));
        var decision = await session.Approver.RequestAsync(requestId, call.ArgumentsJson, session.Config.Cwd, reason, ct);
                if (decision.IsAllow)
        {
            return null;
        }

        if (decision.IsDeny)
        {
            return "denied";
        }

        return "aborted";
    }

    private void Wire(CodexSession session)
    {
        session.Event += evt =>
        {
            switch (evt)
            {
                case AgentEvent.TurnStarted turn:
                    Notify("turn/started", new { turn = new { id = turn.TurnId }, threadId = turn.ThreadId });
                    Notify("thread/status/changed", new { threadId = turn.ThreadId, status = new { type = "active", activeFlags = Array.Empty<string>() } });
                    foreach (var hook in HookCatalog.List(session.Config.Home, session.Config.Cwd).Where(h => h.Event.Equals("UserPromptSubmit", StringComparison.OrdinalIgnoreCase)))
                    {
                        Notify("hook/started", new { threadId = turn.ThreadId, turnId = turn.TurnId, hook = new { name = hook.Name, eventName = hook.Event } });
                    }
                    break;
                case AgentEvent.ItemStarted item:
                    Notify("item/started", new { item = ItemDto(item.Item) });
                    break;
                case AgentEvent.ItemDelta delta:
                    Notify("item/agentMessage/delta", new { itemId = delta.ItemId, delta = delta.Text });
                    break;
                case AgentEvent.CommandOutputDelta output:
                {
                    var payload = new
                    {
                        threadId = session.Thread.Id,
                        turnId = session.Turns().LastOrDefault().Id ?? "",
                        itemId = output.CallId,
                        delta = output.Text,
                    };
                    Notify("item/commandExecution/outputDelta", payload);
                    Notify("item/fileChange/outputDelta", payload);
                    break;
                }
                case AgentEvent.ItemCompleted item:
                    Notify("item/completed", new { item = ItemDto(item.Item) });
                    if (item.Item.Kind is "plan_update" || item.Item.ToolName is "update_plan")
                    {
                        Notify("item/plan/delta", new { itemId = item.Item.Id, delta = item.Item.Text });
                        Notify("turn/plan/updated", new { threadId = session.Thread.Id, plan = item.Item.Text });
                    }
                    if (item.Item.Kind is "file_change" || item.Item.ToolName is "apply_patch")
                    {
                        Notify("turn/diff/updated", new { threadId = session.Thread.Id, diff = item.Item.Text });
                    }
                    break;
                case AgentEvent.Log log:
                    Notify("item/reasoning/textDelta", new { delta = log.Message });
                    break;
                case AgentEvent.Compacted compacted:
                    Notify("thread/compacted", new { threadId = session.Thread.Id, compacted = true, dropped = compacted.Dropped });
                    break;
                case AgentEvent.Warning warning:
                    Notify("warning", new { message = warning.Message });
                    break;
                case AgentEvent.TokenUsage usage:
                    Notify("thread/tokenUsage/updated", new
                    {
                        threadId = session.Thread.Id,
                        turnId = session.Turns().LastOrDefault().Id ?? "",
                        tokenUsage = new
                        {
                            total = TokenBreakdown(usage.Total),
                            last = TokenBreakdown(usage.Last),
                            modelContextWindow = (long?)null,
                        },
                    });
                    break;
                case AgentEvent.ApprovalNeeded approval:
                {
                    var method = approval.Reason.Contains("apply_patch", StringComparison.Ordinal)
                                 || approval.Reason.Contains("write_file", StringComparison.Ordinal)
                        ? "item/fileChange/requestApproval"
                        : "item/commandExecution/requestApproval";
                    var turnId = session.Turns().LastOrDefault().Id ?? "";
                    var rpcId = Request(method, new
                    {
                        threadId = session.Thread.Id,
                        turnId,
                        itemId = approval.RequestId,
                        requestId = approval.RequestId,
                        command = approval.Command,
                        cwd = approval.Cwd,
                        reason = approval.Reason,
                        startedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        kind = "command",
                    });
                    _serverRequests[rpcId] = new PendingServerRequest(session.Thread.Id, approval.RequestId, "approval");
                    Notify("thread/status/changed", new { threadId = session.Thread.Id, status = new { type = "active", activeFlags = new[] { "waitingOnApproval" } } });
                    break;
                }
                case AgentEvent.McpElicitationNeeded elicit:
                {
                    var rpcId = Request("mcpServer/elicitation/request", new
                    {
                        threadId = session.Thread.Id,
                        message = elicit.Message,
                        requestedSchema = new { type = "object" },
                    });
                    _serverRequests[rpcId] = new PendingServerRequest(session.Thread.Id, elicit.RequestId, "mcpElicit");
                    Notify("thread/status/changed", new { threadId = session.Thread.Id, status = new { type = "active", activeFlags = new[] { "waitingOnUserInput" } } });
                    break;
                }
                case AgentEvent.UserInputNeeded input:
                {
                    var questions = ParseUserInputQuestions(input.QuestionsJson);
                    var rpcId = Request("item/tool/requestUserInput", new
                    {
                        threadId = session.Thread.Id,
                        turnId = session.Turns().LastOrDefault().Id ?? "",
                        itemId = input.RequestId,
                        questions,
                        isBlocking = true,
                    });
                    _serverRequests[rpcId] = new PendingServerRequest(session.Thread.Id, input.RequestId, "userInput");
                    Notify("thread/status/changed", new { threadId = session.Thread.Id, status = new { type = "active", activeFlags = new[] { "waitingOnUserInput" } } });
                    break;
                }
                case AgentEvent.TurnCompleted turn:
                    Notify("turn/completed", new { turn = new { id = turn.TurnId, status = turn.Status } });
                    Notify("thread/status/changed", new { threadId = session.Thread.Id, status = new { type = "idle" } });
                    try
                    {
                        var diff = GitProbe.WorkingTreeDiff(session.Config.Cwd);
                        Notify("turn/diff/updated", new { threadId = session.Thread.Id, turnId = turn.TurnId, diff, empty = string.IsNullOrWhiteSpace(diff) });
                    }
                    catch
                    {
                        Notify("turn/diff/updated", new { threadId = session.Thread.Id, turnId = turn.TurnId, diff = "", empty = true });
                    }
                    foreach (var hook in HookCatalog.List(session.Config.Home, session.Config.Cwd).Where(h => h.Event.Equals("Stop", StringComparison.OrdinalIgnoreCase)))
                    {
                        Notify("hook/completed", new { threadId = session.Thread.Id, turnId = turn.TurnId, hook = new { name = hook.Name, eventName = hook.Event }, status = "completed" });
                    }
                    break;
                case AgentEvent.Failed failed:
                    Notify("error", new { message = failed.Message });
                    break;
            }
        };
    }

    public bool TryGetSession(string threadId, [NotNullWhen(true)] out CodexSession? session) =>
        _threads.TryGetValue(threadId, out session);

    private void ApplyLiveConfig(Dictionary<string, string> edits)
    {
        if (edits.TryGetValue("collaboration_mode", out var collab))
        {
            foreach (var live in _threads.Values) live.SetCollaborationMode(collab, persist: false);
        }

        if (edits.TryGetValue("personality", out var personality))
        {
            foreach (var live in _threads.Values) live.SetPersonality(personality, persist: false);
        }

        edits.TryGetValue("model", out var model);
        edits.TryGetValue("sandbox_mode", out var sandbox);
        edits.TryGetValue("approval_policy", out var approval);
        edits.TryGetValue("model_reasoning_effort", out var effort);
        if (!string.IsNullOrWhiteSpace(model) || !string.IsNullOrWhiteSpace(sandbox)
            || !string.IsNullOrWhiteSpace(approval) || !string.IsNullOrWhiteSpace(effort))
        {
            foreach (var live in _threads.Values)
            {
                live.ApplySettings(model, sandbox, approval, effort: effort);
            }
        }
    }

    private static object ThreadDto(CodexSession session) => new
    {
        id = session.Thread.Id,
        title = session.Thread.Title,
        cwd = session.Thread.Cwd,
        model = session.Config.Model,
        sandbox = session.Config.SandboxMode,
        approvalPolicy = session.Config.ApprovalPolicy,
        projectId = ProjectStore.ProjectOf(session.Thread.Id),
        worktree = WorktreeBindings.Find(session.Thread.Id) is { } wt
            ? new { path = wt.Root, cwd = wt.Cwd, sourceRoot = wt.SourceRoot, branch = wt.Branch, cloudWorktree = "notConfigured" }
            : null,
        cloudWorktree = "notConfigured",
        ephemeral = string.IsNullOrEmpty(session.Thread.Path),
        subagents = session.ListSubagents().Select(a => new { id = a.Id, done = a.Done, result = a.Result }).ToArray(),
    };

    private static object ProjectDto(ProjectInfo project) => new
    {
        id = project.Id,
        name = project.Name,
        roots = project.Roots.Select(r => new { path = r.Path }).ToArray(),
        metadata = project.Metadata,
        position = project.Position,
        createdAt = project.CreatedAt,
        updatedAt = project.UpdatedAt,
        recencyAt = project.RecencyAt,
    };

    private static List<string> ReadRoots(JsonElement paramsEl)
    {
        var roots = new List<string>();
        if (paramsEl.ValueKind != JsonValueKind.Object || !paramsEl.TryGetProperty("roots", out var el) || el.ValueKind != JsonValueKind.Array)
        {
            return roots;
        }

        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var path = item.GetString();
                if (!string.IsNullOrWhiteSpace(path)) roots.Add(path);
            }
            else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
            {
                var path = p.GetString();
                if (!string.IsNullOrWhiteSpace(path)) roots.Add(path);
            }
        }

        return roots;
    }

    private static Dictionary<string, string>? ReadMetadata(JsonElement paramsEl)
    {
        if (paramsEl.ValueKind != JsonValueKind.Object || !paramsEl.TryGetProperty("metadata", out var el) || el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                map[prop.Name] = prop.Value.GetString() ?? "";
            }
        }

        return map;
    }

    private static object[] FuzzyFiles(IEnumerable<string> roots, string query)
    {
        var files = new List<object>();
        foreach (var root in roots)
        {
            var cfg = ConfigService.Load(root);
            var sandbox = new WorkspaceSandbox(cfg);
            var result = FileSearch.SearchNames(new ToolCallRequest("search", "file_search", JsonSerializer.Serialize(new { query, path = root })), sandbox);
            foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line == "(no matches)") continue;
                files.Add(new { root, path = line, fileName = Path.GetFileName(line), score = 1, matchType = "file", indices = (int[]?)null });
                if (files.Count >= 50) return files.ToArray();
            }
        }
        return files.ToArray();
    }

    private static object PluginDto(PluginInfo plugin) => new
    {
        id = plugin.Name,
        name = plugin.Name,
        path = plugin.Path,
        kind = plugin.Kind,
        description = plugin.Description,
    };

    private static object SectionDto(ThreadSectionInfo section) => new
    {
        id = section.Id,
        name = section.Name,
        appearance = new { color = section.Color, icon = section.Icon },
    };

    private static object ItemRow(string turnId, int index, HistoryMessage message, string? itemsView) => new
    {
        turnId,
        id = $"{turnId}_{index}",
        type = message.Role switch
        {
            "user" => "user_message",
            "assistant" => "agent_message",
            "tool" => "tool_call",
            _ => message.Role,
        },
        text = ItemsView.Text(message.Content, itemsView),
        tool = message.Name,
    };

    private static HistoryMessage ParseInjectedItem(JsonElement item)
    {
        var type = Str(item, "type") ?? "";
        var role = Str(item, "role");
        if (string.IsNullOrWhiteSpace(role))
        {
            role = type switch
            {
                "user_message" or "user" or "input_text" => "user",
                "agent_message" or "assistant" or "output_text" or "function_call" => "assistant",
                "tool_call" or "tool" or "function_call_output" => "tool",
                "message" => "user",
                _ => "user",
            };
        }

        var text = Str(item, "text") ?? "";
        if (string.IsNullOrEmpty(text) && item.ValueKind == JsonValueKind.Object && item.TryGetProperty("content", out var content))
        {
            text = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString() ?? "",
                JsonValueKind.Array => string.Join("", content.EnumerateArray().Select(part => Str(part, "text") ?? "")),
                _ => content.GetRawText(),
            };
        }

        return new HistoryMessage(
            role ?? "user",
            text,
            Str(item, "callId") ?? Str(item, "call_id") ?? Str(item, "toolCallId") ?? "",
            Str(item, "name") ?? "",
            "");
    }

    private static object TokenBreakdown(long tokens) => new
    {
        totalTokens = tokens,
        inputTokens = tokens,
        cachedInputTokens = 0L,
        cacheWriteInputTokens = 0L,
        outputTokens = 0L,
        reasoningOutputTokens = 0L,
    };

    private static object ItemDto(ConversationItem item) => new
    {
        id = item.Id,
        type = item.Kind,
        text = item.Text,
        status = item.Status,
        command = item.Command,
        path = item.Path,
        tool = item.ToolName,
        toolCallId = item.ToolCallId,
    };

    private static string ReadInputText(JsonElement paramsEl)
    {
        if (paramsEl.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        if (paramsEl.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in input.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    parts.Add(item.GetString() ?? "");
                }
                else if (item.TryGetProperty("text", out var text))
                {
                    parts.Add(text.GetString() ?? "");
                }
                else if (item.TryGetProperty("type", out var ty))
                {
                    var kind = ty.GetString() ?? "";
                    var path = item.TryGetProperty("path", out var p) ? p.GetString()
                        : item.TryGetProperty("localPath", out var lp) ? lp.GetString()
                        : null;
                    if (path is not null && kind is "local_image" or "image" or "localImage")
                    {
                        parts.Add("Please inspect this image with view_image: " + path);
                    }
                }
            }

            return string.Join("\n", parts);
        }

        return Str(paramsEl, "text") ?? "";
    }


    private static int Int(JsonElement el, string name, int fallback = 0)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v))
        {
            return fallback;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
        {
            return n;
        }

        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n) ? n : fallback;
    }

    private static bool? Bool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static bool TryHostPath(string path, out string full, out string error)
    {
        full = "";
        error = "";
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            error = "path must be absolute";
            return false;
        }

        full = Path.GetFullPath(path);
        return true;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private void Result(JsonElement id, object result) =>
        Write(new JsonObject { ["id"] = CloneId(id), ["result"] = JsonSerializer.SerializeToNode(result, _json) });

    private void Error(JsonElement id, int code, string message) =>
        Write(new JsonObject
        {
            ["id"] = CloneId(id),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        });

    private void Notify(string method, object paramsObj) =>
        Write(new JsonObject
        {
            ["method"] = method,
            ["params"] = JsonSerializer.SerializeToNode(paramsObj, _json),
        });

    private string Request(string method, object paramsObj)
    {
        var reqId = Ids.newId("rpc");
        Write(new JsonObject
        {
            ["method"] = method,
            ["id"] = reqId,
            ["params"] = JsonSerializer.SerializeToNode(paramsObj, _json),
        });
        return reqId;
    }

    private void HandleServerResponse(JsonElement id, JsonElement root)
    {
        var key = IdText(id);
        if (string.IsNullOrEmpty(key) || !_serverRequests.Remove(key, out var pending))
        {
            return;
        }

        var allow = false;
        string? reason = null;
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("allow", out var allowEl))
            {
                allow = allowEl.ValueKind == JsonValueKind.True;
            }
            var decision = Str(result, "decision") ?? "";
            if (decision is "accept" or "acceptForSession") allow = true;
            if (decision is "decline" or "cancel") allow = false;
            reason = Str(result, "reason");
        }

        if (_threads.TryGetValue(pending.ThreadId, out var session))
        {
            if (pending.Kind == "userInput")
            {
                var payload = root.TryGetProperty("result", out var userResult) ? userResult.GetRawText() : "{}";
                session.ResolveUserInput(pending.ApprovalId, payload);
            }
            else if (pending.Kind == "mcpElicit")
            {
                var payload = root.TryGetProperty("result", out var elicitResult) ? elicitResult.GetRawText() : "{\"action\":\"decline\"}";
                session.ResolveMcpElicitation(pending.ApprovalId, payload);
            }
            else
            {
                session.ResolveApproval(pending.ApprovalId, allow, reason);
            }
        }

        Notify("serverRequest/resolved", new { requestId = key });
    }

    private static object[] ParseUserInputQuestions(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;
            if (root.TryGetProperty("questions", out var q) && q.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                return q.EnumerateArray().Select(item =>
                {
                    i++;
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var text = item.GetString() ?? "";
                        return (object)new { id = "q" + i, header = "Question", question = text };
                    }
                    return (object)new
                    {
                        id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : "q" + i,
                        header = item.TryGetProperty("header", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : "Question",
                        question = item.TryGetProperty("question", out var qq) && qq.ValueKind == JsonValueKind.String ? qq.GetString() : item.GetRawText(),
                    };
                }).ToArray();
            }
        }
        catch { /* fall through */ }

        return [new { id = "q1", header = "Question", question = json }];
    }

    private static string IdText(JsonElement id) =>
        id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" :
        id.ValueKind == JsonValueKind.Number ? id.GetRawText() :
        "";

    private static JsonNode CloneId(JsonElement id) =>
        id.ValueKind == JsonValueKind.Number ? id.GetInt32() :
        id.ValueKind == JsonValueKind.String ? id.GetString()! :
        0;

    private void Write(JsonNode node)
    {
        lock (_output)
        {
            _output.WriteLine(node.ToJsonString(_json));
            _output.Flush();
        }
    }
}
