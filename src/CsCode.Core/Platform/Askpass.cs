namespace CsCode.Core.Platform;

/// <summary>
/// Routes the prompts of a child process (ssh's key passphrase, password and host-key confirmation; git's HTTPS user name and password) to the
/// GUI: an askpass endpoint that lives for one operation. Implemented by the Remote module (its <c>AskpassServer</c>, the one remote connections
/// use); git's network operations use it when it is registered (docs/SOURCE-CONTROL.md, docs/REMOTE.md).
/// </summary>
public interface IAskpassService
{
    /// <summary>Starts an endpoint for one operation; null when prompts cannot be shown (no window service, an unsupported OS).</summary>
    IAskpassSession? Start();
}

/// <summary>One operation's askpass endpoint; disposing it ends it (its socket, script and token are deleted).</summary>
public interface IAskpassSession : IDisposable
{
    /// <summary>A program that prints the answer to the prompt given as its arguments (<c>SSH_ASKPASS</c>, <c>GIT_ASKPASS</c>).</summary>
    string Program { get; }

    /// <summary>The variables the child needs: <c>SSH_ASKPASS</c>, <c>SSH_ASKPASS_REQUIRE=force</c> and the endpoint's own.</summary>
    IReadOnlyDictionary<string, string?> Environment { get; }
}
