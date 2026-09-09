namespace CodexSharp.Core

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open CodexSharp.Protocol

module BuiltinTools =
    let shell =
        { Name = "shell"
          Description =
            "Runs a shell command in the workspace and returns stdout, stderr, and the exit code. Prefer this for git, tests, and build tools. Do not use it to read or write large files; use read_file / write_file / apply_patch instead."
          ParametersJson =
            """{"type":"object","properties":{"command":{"type":"array","items":{"type":"string"},"description":"Command and arguments. On Windows this is executed through the configured shell."},"workdir":{"type":"string","description":"Optional working directory."},"timeout_ms":{"type":"integer","description":"Optional timeout in milliseconds."}},"required":["command"]}""" }

    let readFile =
        { Name = "read_file"
          Description = "Reads a UTF-8 text file from the workspace and returns its contents with 1-based line numbers."
          ParametersJson =
            """{"type":"object","properties":{"path":{"type":"string"},"offset":{"type":"integer","description":"1-based start line."},"limit":{"type":"integer","description":"Max lines to return."}},"required":["path"]}""" }

    let writeFile =
        { Name = "write_file"
          Description = "Writes UTF-8 text to a file, creating parent directories as needed. Overwrites if the file exists."
          ParametersJson =
            """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}""" }

    let applyPatch =
        { Name = "apply_patch"
          Description =
            "Applies a Codex-style multi-file patch. The patch must start with *** Begin Patch and end with *** End Patch. Supported ops: *** Add File: path, *** Update File: path, *** Delete File: path."
          ParametersJson =
            """{"type":"object","properties":{"patch":{"type":"string"}},"required":["patch"]}""" }

    let updatePlan =
        { Name = "update_plan"
          Description = "Updates the visible task plan. Use this to show the user the current steps and their status."
          ParametersJson =
            """{"type":"object","properties":{"explanation":{"type":"string"},"plan":{"type":"array","items":{"type":"object","properties":{"step":{"type":"string"},"status":{"type":"string","enum":["pending","in_progress","completed"]}},"required":["step","status"]}}},"required":["plan"]}""" }

    let listDir =
        { Name = "list_dir"
          Description = "Lists files and directories at a path relative to the workspace."
          ParametersJson =
            """{"type":"object","properties":{"path":{"type":"string"},"depth":{"type":"integer"}},"required":["path"]}""" }

    let grepFiles =
        { Name = "grep_files"
          Description = "Search file contents in the workspace with a regular expression. Prefer this over shelling out to findstr/rg for code search."
          ParametersJson =
            """{"type":"object","properties":{"pattern":{"type":"string"},"path":{"type":"string"},"glob":{"type":"string"},"max_matches":{"type":"integer"}},"required":["pattern"]}""" }

    let fileSearch =
        { Name = "file_search"
          Description = "Find files by name fragment under the workspace. Inspired by Codex file-search."
          ParametersJson =
            """{"type":"object","properties":{"query":{"type":"string"},"path":{"type":"string"}},"required":["query"]}""" }

    let webSearch =
        { Name = "web_search"
          Description = "Search the public web and return a short snippet list. Use for facts not in the workspace."
          ParametersJson = """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""" }

    let sleep =
        { Name = "sleep"
          Description = "Pause execution for duration_ms milliseconds (1..43200000). Ends early if the turn is interrupted."
          ParametersJson = """{"type":"object","properties":{"duration_ms":{"type":"integer"}},"required":["duration_ms"]}""" }

    let currentTime =
        { Name = "current_time"
          Description = "Return the current local and UTC time."
          ParametersJson = """{"type":"object","properties":{}}""" }

    let viewImage =
        { Name = "view_image"
          Description = "Inspect a local image file and return path, size, and type so you can reason about screenshots or mockups."
          ParametersJson = """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""" }

    let imageGen =
        { Name = "image_gen"
          Description = "Generate an image from a text prompt via the public Images API using the user's API key, then save the PNG into the workspace. Unconfigured without a key. This is not ChatGPT quota or Focused/Canvas."
          ParametersJson = """{"type":"object","properties":{"prompt":{"type":"string"}},"required":["prompt"]}""" }

    let requestUserInput =
        { Name = "request_user_input"
          Description = "Ask the user 1-3 short questions and wait for answers before continuing."
          ParametersJson = """{"type":"object","properties":{"questions":{"type":"array","items":{"type":"string"}}},"required":["questions"]}""" }

    let loadSkill =
        { Name = "load_skill"
          Description = "Load a SKILL.md by name and return its full instructions. Use this before following a skill."
          ParametersJson = """{"type":"object","properties":{"name":{"type":"string"},"path":{"type":"string"}},"required":["name"]}""" }

    let spawnAgent =
        { Name = "spawn_agent"
          Description = "Spawn a sub-agent for a well-scoped parallel task. Returns {id,nickname}. Omit model to inherit. Depth is limited."
          ParametersJson = """{"type":"object","properties":{"message":{"type":"string"},"fork_context":{"type":"boolean"}},"required":["message"]}""" }
    let waitAgent =
        { Name = "wait_agent"
          Description = "Wait for spawned sub-agents to finish."
          ParametersJson = """{"type":"object","properties":{"targets":{"type":"array","items":{"type":"string"}},"timeout_ms":{"type":"integer"}},"required":["targets"]}""" }
    let sendInput =
        { Name = "send_input"
          Description = "Send a follow-up prompt to a running sub-agent."
          ParametersJson = """{"type":"object","properties":{"target":{"type":"string"},"message":{"type":"string"}},"required":["target","message"]}""" }
    let closeAgent =
        { Name = "close_agent"
          Description = "Close a sub-agent that is no longer needed."
          ParametersJson = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"}},\"required\":[\"target\"]}" }
    let listAgents =
        { Name = "list_agents"
          Description = "List this session's sub-agents and whether each is still running."
          ParametersJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\"}}}" }
    let resumeAgent =
        { Name = "resume_agent"
          Description = "Resume a closed sub-agent with a follow-up prompt. Use the agent id from spawn_agent."
          ParametersJson = "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"},\"target\":{\"type\":\"string\"},\"message\":{\"type\":\"string\"}},\"required\":[\"message\"]}" }
    let interruptAgent =
        { Name = "interrupt_agent"
          Description = "Cancel a running sub-agent. Closed or unknown ids return an error."
          ParametersJson = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"id\":{\"type\":\"string\"}}}" }


    let getContextRemaining =
        { Name = "get_context_remaining"
          Description = "Report how much of the local conversation budget is used. tokens_left is null when the provider window is unknown."
          ParametersJson = """{"type":"object","properties":{}}""" }

    let toolSearch =
        { Name = "tool_search"
          Description = "Search available tools by name or description. Use this to find a tool before calling it."
          ParametersJson = """{"type":"object","properties":{"query":{"type":"string"},"limit":{"type":"integer"}},"required":["query"]}""" }

    let listAvailablePlugins =
        { Name = "list_available_plugins_to_install"
          Description = "List plugins that can be installed from local marketplaces under ~/.codexsharp/marketplaces."
          ParametersJson = """{"type":"object","properties":{}}""" }

    let requestPluginInstall =
        { Name = "request_plugin_install"
          Description = "Install a plugin from a local marketplace by name. Does not talk to a remote OpenAI plugin service."
          ParametersJson = """{"type":"object","properties":{"name":{"type":"string"},"pluginName":{"type":"string"}},"required":[]}""" }

    let listMcpResources =
        { Name = "list_mcp_resources"
          Description = "List MCP resources. Without a live MCP session this returns configured servers only."
          ParametersJson = """{"type":"object","properties":{"server":{"type":"string"}} }""" }

    let readMcpResource =
        { Name = "read_mcp_resource"
          Description = "Read an MCP resource by uri. Requires a live MCP session."
          ParametersJson = """{"type":"object","properties":{"uri":{"type":"string"},"server":{"type":"string"}},"required":["uri"]}""" }

    let waitForEnvironment =
        { Name = "wait_for_environment"
          Description = "Wait for a selected execution environment. Local is ready immediately. Remote environments stay pending without an exec-server."
          ParametersJson = """{"type":"object","properties":{"environment_id":{"type":"string"}},"required":["environment_id"]}""" }

    let sendMessageToUser =
        { Name = "send_message_to_user"
          Description = "Show a short message to the user in the transcript. Does not wait for a reply; use request_user_input for questions."
          ParametersJson = """{"type":"object","properties":{"message":{"type":"string"},"text":{"type":"string"}}}""" }

    let execCommand =
        { Name = "exec_command"
          Description = "Start a persistent PTY/shell session (unified_exec). Returns session_id and output after yield_time_ms. Enable the unified_exec feature first. Prefer this over shell for interactive programs."
          ParametersJson = """{"type":"object","properties":{"cmd":{"type":"string"},"command":{"type":"string"},"workdir":{"type":"string"},"yield_time_ms":{"type":"integer"},"tty":{"type":"boolean"}},"required":["cmd"]}""" }

    let writeStdin =
        { Name = "write_stdin"
          Description = "Write to a unified_exec session started by exec_command, then wait yield_time_ms for more output."
          ParametersJson = """{"type":"object","properties":{"session_id":{},"chars":{"type":"string"},"yield_time_ms":{"type":"integer"}},"required":["session_id"]}""" }

    let newContextWindow =
        { Name = "new_context_window"
          Description = "Start a new context window without summarizing conversation history. Use when the current thread is too large and a summary would lose needed detail."
          ParametersJson = """{"type":"object","properties":{}}""" }

    let requestPermissions =
        { Name = "request_permissions"
          Description = "Ask the user to grant extra sandbox permissions for this session. Read roots may be added; writes stay workspace-write unless sandbox_mode is already danger-full-access. Network is not auto-enabled."
          ParametersJson = """{"type":"object","properties":{"justification":{"type":"string"},"additional_readonly_roots":{"type":"array","items":{"type":"string"}},"network":{"type":"boolean"}}}""" }

    let listMcpResourceTemplates =
        { Name = "list_mcp_resource_templates"
          Description = "List MCP resource templates. Without a live MCP session this returns configured servers only."
          ParametersJson = """{"type":"object","properties":{"server":{"type":"string"}}}""" }

    let all = [| shell; readFile; writeFile; applyPatch; updatePlan; listDir; grepFiles; fileSearch; viewImage; imageGen; currentTime; sleep; webSearch; loadSkill; requestUserInput; spawnAgent; waitAgent; sendInput; closeAgent; listAgents; resumeAgent; interruptAgent; getContextRemaining; toolSearch; listAvailablePlugins; requestPluginInstall; listMcpResources; readMcpResource; listMcpResourceTemplates; waitForEnvironment; sendMessageToUser; execCommand; writeStdin; requestPermissions; newContextWindow |]

module Prompt =
    let defaultInstructions =
        """You are CodexSharp, a local software engineering agent. You work in a real repository on the user's machine.

Rules:
- Solve the user's request completely. Prefer making the change over only describing it.
- Use tools to inspect the workspace before editing. Do not invent file paths.
- Make focused diffs. Match existing style. Do not add drive-by refactors.
- After code changes, run the most relevant tests or build command when it is cheap and available.
- When a command may be destructive, network-using, or outside the workspace, wait for approval instead of guessing.
- Never exfiltrate secrets. Never dump .env, id_rsa, auth.json, or credentials.
- Reply in the user's language unless they ask otherwise.
- Each turn must end with a concise assistant message describing what you did or what you need next.
"""

    let permissions (cfg: CodexConfig) =
        let sandbox = CodexConfig.sandbox cfg
        let approval = CodexConfig.approval cfg
        let writable =
            match sandbox with
            | ReadOnly -> "No writable roots unless the user approves an escalation."
            | WorkspaceWrite -> $"Writable root: {cfg.Cwd}. Protected paths inside it still include .git and {cfg.Home}."
            | DangerFullAccess -> "Sandbox disabled: the shell may read and write without CodexSharp filesystem enforcement."

        let network =
            if cfg.NetworkAccess then "Network access is allowed."
            else "Network access is denied by default; ask for approval before using the network."

        let ask =
            match approval with
            | Never -> "Do not ask the user to approve commands; the host will auto-allow or auto-deny."
            | Untrusted -> "Ask for approval before every shell command and every write."
            | OnRequest -> "Ask for approval when a command needs network, writes outside the workspace, or looks destructive."

        $"""{CodexTags.PermissionsOpen}
Sandbox: {SandboxMode.toWire sandbox}
Approval policy: {ApprovalPolicy.toWire approval}
{writable}
{network}
{ask}
The sandbox applies to CodexSharp built-in tools. External MCP tools are not sandboxed by this host.
{CodexTags.PermissionsClose}"""

    let private git (cwd: string) (args: string) =
        try
            let psi =
                ProcessStartInfo(
                    FileName = "git",
                    Arguments = args,
                    WorkingDirectory = cwd,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                )
            use proc = Process.Start psi
            if isNull proc || not (proc.WaitForExit 2000) then
                ""
            else
                proc.StandardOutput.ReadToEnd().Trim()
        with _ ->
            ""

    let extraReadRoots (home: string) =
        try
            let path = Path.Combine(home, "sandbox-read-roots.json")
            if not (File.Exists path) then
                ""
            else
                let doc = JsonDocument.Parse(File.ReadAllText path)
                try
                    if doc.RootElement.ValueKind <> JsonValueKind.Array then
                        ""
                    else
                        let roots =
                            doc.RootElement.EnumerateArray()
                            |> Seq.choose (fun x ->
                                if x.ValueKind = JsonValueKind.String then
                                    let s = x.GetString()
                                    if String.IsNullOrWhiteSpace s then None else Some s
                                else
                                    None)
                            |> Seq.toArray
                        if roots.Length = 0 then
                            ""
                        else
                            roots
                            |> Array.map (fun r -> sprintf "<extra_readonly_root>%s</extra_readonly_root>" r)
                            |> String.concat "\n"
                finally
                    doc.Dispose()
        with _ ->
            ""

    let environment (cfg: CodexConfig) =
        let shell =
            if OperatingSystem.IsWindows() then
                Option.ofObj (Environment.GetEnvironmentVariable "COMSPEC")
                |> Option.defaultValue "powershell"
            else
                Option.ofObj (Environment.GetEnvironmentVariable "SHELL")
                |> Option.defaultValue "/bin/bash"

        let branch = git cfg.Cwd "rev-parse --abbrev-ref HEAD"
        let sha = git cfg.Cwd "rev-parse --short HEAD"
        let gitXml =
            if String.IsNullOrWhiteSpace branch then ""
            else $"<git_branch>{branch}</git_branch>
<git_sha>{sha}</git_sha>"

        let extraXml = extraReadRoots cfg.Home

        $"""{CodexTags.EnvironmentContextOpen}
<cwd>{cfg.Cwd}</cwd>
<shell>{shell}</shell>
<os>{Environment.OSVersion}</os>
<runtime>.NET {Environment.Version}</runtime>
{gitXml}
{extraXml}
{CodexTags.EnvironmentContextClose}"""

    let loadAgentsMarkdown (cwd: string) =
        let names = [| "AGENTS.override.md"; "AGENTS.md"; "CLAUDE.md" |]
        let acc = StringBuilder()
        let dir = DirectoryInfo(cwd)

        let rec findGit (d: DirectoryInfo) =
            if isNull d then None
            elif Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")) then Some d
            else findGit d.Parent

        let root = findGit dir |> Option.defaultValue dir
        let chain = ResizeArray<DirectoryInfo>()
        let mutable cur = Some dir
        while cur.IsSome do
            let folder = cur.Value
            chain.Insert(0, folder)
            if folder.FullName.Equals(root.FullName, StringComparison.OrdinalIgnoreCase) then
                cur <- None
            else
                cur <- Option.ofObj folder.Parent

        let seen = ResizeArray<string>()
        for folder in chain do
            for name in names do
                let path = Path.Combine(folder.FullName, name)
                if File.Exists path && not (seen.Contains path) then
                    seen.Add path
                    let text = File.ReadAllText path
                    if acc.Length + text.Length < 32 * 1024 then
                        acc.AppendLine($"# {path}").AppendLine(text).AppendLine() |> ignore

        acc.ToString().Trim()

    let private memoryEnabled (home: string) (threadId: string) =
        try
            let path = Path.Combine(home, "memory-modes.json")
            if not (File.Exists path) || String.IsNullOrWhiteSpace threadId then true
            else
                use doc = JsonDocument.Parse(File.ReadAllText path)
                let mutable v = Unchecked.defaultof<JsonElement>
                if doc.RootElement.TryGetProperty(threadId, &v) && v.ValueKind = JsonValueKind.String then
                    v.GetString() <> "disabled"
                else true
        with _ -> true

    let loadMemories (home: string) (threadId: string) =
        if not (memoryEnabled home threadId) then ""
        else
            let root = Path.Combine(home, "memories")
            if not (Directory.Exists root) then ""
            else
                let acc = StringBuilder()
                acc.AppendLine(CodexTags.MemoriesOpen) |> ignore
                for file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) |> Seq.truncate 24 do
                    try
                        let rel = Path.GetRelativePath(root, file).Replace('\\', '/')
                        let text = File.ReadAllText file
                        if acc.Length + text.Length < 16 * 1024 then
                            acc.AppendLine($"# {rel}").AppendLine(text).AppendLine() |> ignore
                    with _ -> ()
                acc.AppendLine(CodexTags.MemoriesClose) |> ignore
                if acc.Length <= CodexTags.MemoriesOpen.Length + CodexTags.MemoriesClose.Length + 4 then ""
                else acc.ToString().Trim()

    let private disabledSkills (home: string) =
        try
            let path = Path.Combine(home, "skills-config.json")
            if not (File.Exists path) then Set.empty
            else
                use doc = JsonDocument.Parse(File.ReadAllText path)
                if doc.RootElement.ValueKind <> JsonValueKind.Array then Set.empty
                else
                    doc.RootElement.EnumerateArray()
                    |> Seq.choose (fun x -> if x.ValueKind = JsonValueKind.String then Some (x.GetString()) else None)
                    |> Seq.filter (fun s -> not (String.IsNullOrWhiteSpace s))
                    |> Set.ofSeq
        with _ -> Set.empty

    let loadSkills (home: string) (cwd: string) =
        let roots =
            [| Path.Combine(home, "skills")
               Path.Combine(cwd, ".codexsharp", "skills")
               Path.Combine(cwd, ".codex", "skills")
               Path.Combine(cwd, "skills") |]
        let disabled = disabledSkills home
        let rows = ResizeArray<string>()
        for root in roots do
            if Directory.Exists root then
                for skill in Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories) do
                    try
                        let name = Path.GetFileName(Path.GetDirectoryName skill)
                        if disabled.Contains skill || (not (isNull name) && disabled.Contains name) then ()
                        else
                            let head =
                                File.ReadLines skill
                                |> Seq.truncate 40
                                |> String.concat "\n"
                            rows.Add($"- {skill}\n{head}")
                    with _ -> ()

        if rows.Count = 0 then ""
        else
            "Available skills (read the SKILL.md with read_file before using one):\n" + String.Join("\n\n", rows)

    let buildWithGoal (cfg: CodexConfig) (history: HistoryMessage[]) (extraTools: ToolSpec[]) (threadId: string) (goalObjective: string) (goalStatus: string) =
        let messages = ResizeArray<HistoryMessage>()
        messages.Add(HistoryMessage.developer (permissions cfg))

        if not (String.IsNullOrWhiteSpace cfg.DeveloperInstructions) then
            messages.Add(HistoryMessage.developer cfg.DeveloperInstructions)

        let goal = ThreadGoal.promptBlock goalObjective goalStatus
        if not (String.IsNullOrWhiteSpace goal) then
            messages.Add(HistoryMessage.developer goal)

        let agents = loadAgentsMarkdown cfg.Cwd
        if not (String.IsNullOrWhiteSpace agents) then
            messages.Add(HistoryMessage.user agents)

        if not (String.IsNullOrWhiteSpace cfg.UserInstructions) then
            messages.Add(HistoryMessage.user cfg.UserInstructions)

        let skills = loadSkills cfg.Home cfg.Cwd
        if not (String.IsNullOrWhiteSpace skills) then
            messages.Add(HistoryMessage.user skills)

        let memories = loadMemories cfg.Home threadId
        if not (String.IsNullOrWhiteSpace memories) then
            messages.Add(HistoryMessage.user memories)

        messages.Add(HistoryMessage.user (environment cfg))
        messages.AddRange history

        { Model = cfg.Model
          Instructions = defaultInstructions
          Messages = messages.ToArray()
          Tools = Array.append BuiltinTools.all extraTools
          ReasoningEffort = cfg.ReasoningEffort }

    let buildWithTools (cfg: CodexConfig) (history: HistoryMessage[]) (extraTools: ToolSpec[]) (threadId: string) =
        buildWithGoal cfg history extraTools threadId "" ""

    let build cfg history = buildWithTools cfg history [||] ""
