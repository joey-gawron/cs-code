using System.Collections.Concurrent;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Platform.Commands;

/// <summary>
/// The global command table. Registrations form a STACK per id: the latest registration shadows earlier ones and disposing it
/// restores the previous handler (so a focused editor can temporarily override a global command).
/// Lookups are lock-free (every keybinding, menu click and palette entry goes through <see cref="ExecuteAsync"/>); only
/// registration/unregistration take a short lock. No lock is ever held while a handler runs or while awaiting, so handlers may
/// freely register or execute other commands (re-entrancy cannot deadlock).
/// </summary>
public sealed class CommandService : ICommandService
{
    /// <summary>Key under which <see cref="Exception.Data"/> carries the id of the command whose handler threw.</summary>
    public const string FailedCommandIdDataKey = "cscode.commandId";

    private sealed class Entry(string id, Func<CommandInvocation, ValueTask<object?>> handler, CommandMetadata? metadata)
    {
        public string Id { get; } = id;
        public Func<CommandInvocation, ValueTask<object?>> Handler { get; } = handler;
        public CommandMetadata? Metadata { get; } = metadata;
    }

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, Entry[]> _table = new(StringComparer.Ordinal);
    private readonly Emitter<string> _registered = new();
    private readonly ILogger _log;
    private Func<string, CancellationToken, ValueTask<bool>>[] _resolvers = [];

    public CommandService(ILogger<CommandService>? logger = null)
    {
        _log = logger ?? NullLogger<CommandService>.Instance;
    }

    public IEventSource<string> Registered => _registered;

    public IDisposable Register(string id, Func<CommandInvocation, ValueTask<object?>> handler, CommandMetadata? metadata = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(handler);
        var entry = new Entry(id, handler, metadata is null ? null : metadata with { Id = id });

        lock (_gate)
        {
            _table.AddOrUpdate(id, static (_, e) => [e], static (_, old, e) => [.. old, e], entry);
        }
        _registered.Fire(id);

        return Disposable.Create(() =>
        {
            lock (_gate)
            {
                if (!_table.TryGetValue(id, out var stack)) return;
                var idx = Array.IndexOf(stack, entry);
                if (idx < 0) return;
                if (stack.Length == 1) { _table.TryRemove(id, out _); return; }
                var next = new Entry[stack.Length - 1];
                Array.Copy(stack, 0, next, 0, idx);
                Array.Copy(stack, idx + 1, next, idx, stack.Length - idx - 1);
                _table[id] = next;
            }
        });
    }

    public bool Has(string id) => id is not null && _table.TryGetValue(id, out var s) && s.Length > 0;

    /// <summary>Palette/menu metadata of the effective (top) registration of every command; a shadowing registration without metadata inherits the nearest lower one.</summary>
    public IReadOnlyList<CommandMetadata> All
    {
        get
        {
            var list = new List<CommandMetadata>(_table.Count);
            foreach (var (id, stack) in _table)
            {
                CommandMetadata? meta = null;
                for (var i = stack.Length - 1; i >= 0 && meta is null; i--) meta = stack[i].Metadata;
                list.Add(meta ?? new CommandMetadata(id));
            }
            list.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
            return list;
        }
    }

    /// <summary>
    /// Adds a fallback consulted (in registration order) when an unknown id is executed, e.g. to lazily register a command on first
    /// use. A resolver returns true if it registered the command; after the first <c>true</c> the lookup is retried exactly once.
    /// A resolver that throws is logged and skipped.
    /// </summary>
    public IDisposable AddUnknownCommandResolver(Func<string, CancellationToken, ValueTask<bool>> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (_gate) _resolvers = [.. _resolvers, resolver];
        return Disposable.Create(() =>
        {
            lock (_gate)
            {
                var idx = Array.IndexOf(_resolvers, resolver);
                if (idx < 0) return;
                var next = new Func<string, CancellationToken, ValueTask<bool>>[_resolvers.Length - 1];
                Array.Copy(_resolvers, 0, next, 0, idx);
                Array.Copy(_resolvers, idx + 1, next, idx, _resolvers.Length - idx - 1);
                _resolvers = next;
            }
        });
    }

    public async ValueTask<object?> ExecuteAsync(string id, params object?[] args)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var entry = Top(id);
        if (entry is null)
        {
            if (await TryResolveAsync(id).ConfigureAwait(false)) entry = Top(id);
            if (entry is null) throw new CommandNotFoundException(id);
        }

        try
        {
            return await entry.Handler(new CommandInvocation(id, args ?? [])).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Keep the original exception type and stack (callers catch IOException, OperationCanceledException, ...); innermost command wins.
            try { if (!ex.Data.IsReadOnly && !ex.Data.Contains(FailedCommandIdDataKey)) ex.Data[FailedCommandIdDataKey] = id; }
            catch (ArgumentException) { /* non-serialisable key rules on exotic exceptions: the id is a convenience, never worth masking the failure */ }
            if (ex is not OperationCanceledException) _log.LogDebug(ex, "Command {CommandId} failed", id);
            throw;
        }
    }

    /// <summary>The id of the command whose handler raised <paramref name="ex"/> (innermost), if it came through <see cref="ExecuteAsync"/>.</summary>
    public static string? GetFailedCommandId(Exception ex) => ex.Data[FailedCommandIdDataKey] as string;

    private Entry? Top(string id) => _table.TryGetValue(id, out var s) && s.Length > 0 ? s[^1] : null;

    private async ValueTask<bool> TryResolveAsync(string id)
    {
        foreach (var resolver in Volatile.Read(ref _resolvers))
        {
            try
            {
                if (await resolver(id, CancellationToken.None).ConfigureAwait(false)) return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Unknown-command resolver failed for {CommandId}", id);
            }
        }
        return false;
    }
}
