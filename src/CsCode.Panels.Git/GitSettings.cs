using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Panels.Git.Engine;

namespace CsCode.Panels.Git;

/// <summary>The source-control settings (VS Code's names where VS Code has them), registered with their defaults and descriptions.</summary>
public static class GitSettings
{
    public const string DiffDecorations = "scm.diffDecorations";
    public const string DiffSideBySide = "diffEditor.renderSideBySide";
    public const string DiffIgnoreTrimWhitespace = "diffEditor.ignoreTrimWhitespace";
    public const string ConfirmSync = "git.confirmSync";
    public const string EnableSmartCommit = "git.enableSmartCommit";
    public const string UseIntegratedAskPass = "git.useIntegratedAskPass";
    public const string NetworkTimeout = "git.networkTimeout";

    public static IEnumerable<ConfigurationProperty> Properties()
    {
        static JsonElement J(object? v) => JsonSerializer.SerializeToElement(v);
        static JsonElement S(string json) => JsonDocument.Parse(json).RootElement.Clone();
        yield return new ConfigurationProperty(GitService.EnabledSetting, J(true), "Whether git is enabled: repositories in the workspace folders, the Source Control window, Explorer decorations, gutter change markers.", S("""{"type":"boolean"}"""));
        yield return new ConfigurationProperty(GitService.PathSetting, J(null), "Path to the git executable. Empty: git on the PATH (after the login shell's environment on macOS), then the usual install directories.", S("""{"type":["string","null"]}""")) { Scope = ConfigurationScope.Machine };
        yield return new ConfigurationProperty(GitService.AutoRefreshSetting, J(true), "Whether the status is read again automatically when files change (the file watcher) and when the window gets focus.", S("""{"type":"boolean"}"""));
        yield return new ConfigurationProperty(GitService.DecorationsSetting, J(true), "Show git status letters and colours in the Explorer (M modified, U untracked, A added, D deleted, ! conflict; ignored files dimmed).", S("""{"type":"boolean"}"""));
        yield return new ConfigurationProperty(GitService.BlameSetting, J(false), "Show who last changed the current line, and when, after the end of the line (inline blame).", S("""{"type":"boolean"}"""));
        yield return new ConfigurationProperty(DiffDecorations, J("all"), "Gutter change markers in the editor: \"all\" shows added, modified and deleted lines against the index; \"none\" hides them.", S("""{"type":"string","enum":["all","none"]}"""));
        yield return new ConfigurationProperty(DiffSideBySide, J(true), "Whether the diff editor shows the two sides next to each other (true) or one inline view with removed lines above the lines that replaced them (false).", S("""{"type":"boolean"}"""));
        yield return new ConfigurationProperty(DiffIgnoreTrimWhitespace, J(true), "Whether the diff editor ignores changes of leading and trailing whitespace.", S("""{"type":"boolean"}"""));
        yield return new ConfigurationProperty(ConfirmSync, J(true), "Ask before pulling and pushing with Sync.", S("""{"type":"boolean"}"""));
        yield return new ConfigurationProperty(EnableSmartCommit, J(false), "Commit all changes when nothing is staged, without asking.", S("""{"type":"boolean"}"""));
        yield return new ConfigurationProperty(UseIntegratedAskPass, J(true), "Ask in CsCode for what fetch, pull and push need (ssh key passphrases and passwords, host key confirmations, HTTPS user names and passwords). On, CsCode's helper replaces your own askpass programs (GIT_ASKPASS, core.askPass, SSH_ASKPASS) for these operations. Off: your own GIT_ASKPASS or core.askPass answers HTTPS prompts, and ssh runs in batch mode and fails instead of asking (unless your ssh command or ~/.ssh/config sets BatchMode, then your SSH_ASKPASS can answer); credential helpers and ssh agents work either way.", S("""{"type":"boolean"}"""));
        yield return new ConfigurationProperty(NetworkTimeout, J(1800), "Seconds after which a fetch, pull or push is stopped however far it got (0, or more than 49.7 days: no limit). An ssh connection that is not up in 30 s or goes silent for a minute fails sooner (unless your ssh configuration sets ConnectTimeout or ServerAlive*); any operation can be cancelled from its progress notification.", S("""{"type":"number","minimum":0}"""));
    }
}
