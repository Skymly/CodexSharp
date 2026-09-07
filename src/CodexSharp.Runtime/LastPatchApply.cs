namespace CodexSharp.Runtime;

public sealed record LastPatchResult(bool Ok, string Output, string Cwd);

/// Apply the latest apply_patch from a local thread into that thread's cwd.
/// Codex Cloud task apply remains notConfigured.
public static class LastPatchApply
{
    public static LastPatchResult Run(string threadId)
    {
        var store = new JsonlThreadStore();
        var patch = store.FindLastPatch(threadId);
        if (string.IsNullOrWhiteSpace(patch))
        {
            return new LastPatchResult(false, "no apply_patch in that thread", "");
        }

        var info = store.Find(threadId);
        var cwd = string.IsNullOrWhiteSpace(info?.Cwd) ? Environment.CurrentDirectory : info!.Cwd;
        var cfg = ConfigService.Load(cwd);
        var result = ApplyPatchEngine.Apply(patch, new WorkspaceSandbox(cfg));
        return new LastPatchResult(result.Ok, result.Output, cwd);
    }
}
