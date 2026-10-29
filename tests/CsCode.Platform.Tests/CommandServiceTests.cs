using CsCode.Core.Platform;
using CsCode.Platform.Commands;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Platform.Tests;

public class CommandServiceTests
{
    private static Func<CommandInvocation, ValueTask<object?>> Returns(object? value) => _ => ValueTask.FromResult(value);

    [Fact]
    public async Task Executes_the_registered_handler_with_id_and_arguments()
    {
        var svc = new CommandService();
        CommandInvocation? seen = null;
        svc.Register("x.cmd", inv => { seen = inv; return ValueTask.FromResult<object?>(42); });

        var result = await svc.ExecuteAsync("x.cmd", "a", 7);

        Assert.Equal(42, result);
        Assert.Equal("x.cmd", seen!.Id);
        Assert.Equal(new object?[] { "a", 7 }, seen.Args);
        Assert.Equal(7, seen.Arg<int>(1));
    }

    [Fact]
    public async Task Later_registration_shadows_and_disposing_it_restores_the_previous_handler()
    {
        var svc = new CommandService();
        svc.Register("c", Returns("base"));
        var top = svc.Register("c", Returns("override"));
        Assert.Equal("override", await svc.ExecuteAsync("c"));

        top.Dispose();
        Assert.Equal("base", await svc.ExecuteAsync("c"));
    }

    [Fact]
    public async Task Disposing_a_buried_registration_does_not_change_the_top_and_double_dispose_is_harmless()
    {
        var svc = new CommandService();
        var bottom = svc.Register("c", Returns("bottom"));
        var middle = svc.Register("c", Returns("middle"));
        svc.Register("c", Returns("top"));

        middle.Dispose();
        middle.Dispose();
        Assert.Equal("top", await svc.ExecuteAsync("c"));
        bottom.Dispose();
        Assert.Equal("top", await svc.ExecuteAsync("c"));
    }

    [Fact]
    public async Task Removing_the_last_registration_makes_the_command_unknown()
    {
        var svc = new CommandService();
        var reg = svc.Register("gone", Returns(1));
        Assert.True(svc.Has("gone"));
        reg.Dispose();
        Assert.False(svc.Has("gone"));
        await Assert.ThrowsAsync<CommandNotFoundException>(async () => await svc.ExecuteAsync("gone"));
    }

    [Fact]
    public async Task Unknown_command_throws_CommandNotFoundException_carrying_the_id()
    {
        var svc = new CommandService();
        var ex = await Assert.ThrowsAsync<CommandNotFoundException>(async () => await svc.ExecuteAsync("nope.nothing"));
        Assert.Equal("nope.nothing", ex.CommandId);
    }

    [Fact]
    public async Task Unknown_command_resolvers_run_in_order_then_the_lookup_is_retried_once()
    {
        var svc = new CommandService();
        var order = new List<string>();
        svc.AddUnknownCommandResolver((id, _) => { order.Add("r1"); return ValueTask.FromResult(false); });
        svc.AddUnknownCommandResolver((id, _) =>
        {
            order.Add("r2");
            svc.Register(id, Returns("lazy:" + id));
            return ValueTask.FromResult(true);
        });
        svc.AddUnknownCommandResolver((id, _) => { order.Add("r3"); return ValueTask.FromResult(true); });

        Assert.Equal("lazy:late.bound", await svc.ExecuteAsync("late.bound"));
        Assert.Equal(["r1", "r2"], order); // r3 never consulted: r2 claimed it

        order.Clear();
        Assert.Equal("lazy:late.bound", await svc.ExecuteAsync("late.bound")); // now known: resolvers not consulted
        Assert.Empty(order);
    }

    [Fact]
    public async Task A_resolver_that_claims_success_but_registers_nothing_still_ends_in_CommandNotFound()
    {
        var svc = new CommandService();
        var calls = 0;
        svc.AddUnknownCommandResolver((_, _) => { calls++; return ValueTask.FromResult(true); });
        await Assert.ThrowsAsync<CommandNotFoundException>(async () => await svc.ExecuteAsync("liar"));
        Assert.Equal(1, calls); // retried the lookup once, not the resolver
    }

    [Fact]
    public async Task A_throwing_resolver_is_skipped_not_fatal()
    {
        var svc = new CommandService();
        svc.AddUnknownCommandResolver((_, _) => throw new InvalidOperationException("resolver bug"));
        svc.AddUnknownCommandResolver((id, _) => { svc.Register(id, Returns("ok")); return ValueTask.FromResult(true); });
        Assert.Equal("ok", await svc.ExecuteAsync("c"));
    }

    [Fact]
    public async Task Removed_resolvers_are_no_longer_consulted()
    {
        var svc = new CommandService();
        var calls = 0;
        var reg = svc.AddUnknownCommandResolver((_, _) => { calls++; return ValueTask.FromResult(false); });
        await Assert.ThrowsAsync<CommandNotFoundException>(async () => await svc.ExecuteAsync("a"));
        reg.Dispose();
        await Assert.ThrowsAsync<CommandNotFoundException>(async () => await svc.ExecuteAsync("a"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Handler_exceptions_propagate_with_original_type_and_the_command_id_attached()
    {
        var svc = new CommandService();
        svc.Register("fails", _ => throw new IOException("disk on fire"));
        svc.Register("fails.async", async _ => { await Task.Yield(); throw new FormatException("bad"); });

        var io = await Assert.ThrowsAsync<IOException>(async () => await svc.ExecuteAsync("fails"));
        Assert.Equal("fails", CommandService.GetFailedCommandId(io));
        var fe = await Assert.ThrowsAsync<FormatException>(async () => await svc.ExecuteAsync("fails.async"));
        Assert.Equal("fails.async", CommandService.GetFailedCommandId(fe));
    }

    [Fact]
    public async Task Nested_command_failure_keeps_the_innermost_command_id()
    {
        var svc = new CommandService();
        svc.Register("inner", _ => throw new InvalidOperationException("x"));
        svc.Register("outer", async _ => await svc.ExecuteAsync("inner"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await svc.ExecuteAsync("outer"));
        Assert.Equal("inner", CommandService.GetFailedCommandId(ex));
    }

    [Fact]
    public async Task Cancellation_exceptions_are_not_wrapped()
    {
        var svc = new CommandService();
        svc.Register("cancel", _ => throw new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await svc.ExecuteAsync("cancel"));
    }

    [Fact]
    public async Task Handlers_can_reenter_the_service_without_deadlocking()
    {
        var svc = new CommandService();
        svc.Register("leaf", Returns(1));
        svc.Register("mid", async _ => (int)(await svc.ExecuteAsync("leaf"))! + 1);
        svc.Register("root", async _ =>
        {
            svc.Register("registered.inside", Returns(0)); // registering from a running handler
            return (int)(await svc.ExecuteAsync("mid"))! + 1;
        });

        var result = await svc.ExecuteAsync("root").AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(3, result);
        Assert.True(svc.Has("registered.inside"));
    }

    [Fact]
    public async Task No_lock_is_held_while_a_handler_is_awaiting()
    {
        var svc = new CommandService();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.Register("slow", async _ => { await release.Task; return "done"; });

        var running = svc.ExecuteAsync("slow").AsTask();
        // While "slow" is suspended, other threads must be able to register/execute/dispose freely.
        var other = Task.Run(async () =>
        {
            using var reg = svc.Register("quick", Returns("q"));
            return await svc.ExecuteAsync("quick");
        }, TestContext.Current.CancellationToken);
        Assert.Equal("q", await other.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        release.SetResult();
        Assert.Equal("done", await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Registered_event_fires_for_every_registration()
    {
        var svc = new CommandService();
        var seen = new List<string>();
        using var sub = svc.Registered.Subscribe(seen.Add);
        svc.Register("a", Returns(null));
        svc.Register("a", Returns(null));
        svc.Register("b", Returns(null));
        Assert.Equal(["a", "a", "b"], seen);
    }

    [Fact]
    public void Metadata_table_reports_the_effective_registration_and_inherits_when_shadowed_without_metadata()
    {
        var svc = new CommandService();
        svc.Register("file.save", Returns(null), new CommandMetadata("ignored-id", "Save", "File", Icon: "save", Enablement: "editorIsDirty"));
        svc.Register("plain", Returns(null));
        var shadow = svc.Register("file.save", Returns(null)); // no metadata of its own

        var all = svc.All;
        Assert.Equal(["file.save", "plain"], all.Select(m => m.Id).ToArray());
        var save = all.Single(m => m.Id == "file.save");
        Assert.Equal("Save", save.Title);
        Assert.Equal("File", save.Category);
        Assert.Equal("editorIsDirty", save.Enablement);
        Assert.Null(all.Single(m => m.Id == "plain").Title);

        shadow.Dispose();
        Assert.Equal("Save", svc.All.Single(m => m.Id == "file.save").Title);
    }

    [Fact]
    public async Task Parallel_register_execute_dispose_smoke_has_no_deadlocks_or_exceptions()
    {
        var svc = new CommandService();
        svc.Register("shared", Returns("base"));
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var workers = Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            var i = 0;
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var id = $"w{w}.c{i++ % 5}";
                    using (svc.Register(id, Returns(i)))
                    using (svc.Register("shared", Returns("w" + w)))
                    {
                        await svc.ExecuteAsync(id);
                        Assert.NotNull(await svc.ExecuteAsync("shared"));
                        _ = svc.All;
                        _ = svc.Has(id);
                    }
                }
            }
            catch (Exception ex) { errors.Add(ex); }
        }, TestContext.Current.CancellationToken)).ToArray();

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Empty(errors);
        Assert.Equal("base", await svc.ExecuteAsync("shared")); // every override was unwound
    }

    [Fact]
    public void Disposed_registrations_do_not_keep_their_handlers_alive()
    {
        var svc = new CommandService();
        LeakAssert.NotRetained(() =>
        {
            var captured = new object();
            var reg = svc.Register("leak.test", inv => { _ = captured; return ValueTask.FromResult<object?>(null); });
            reg.Dispose();
            return [captured];
        });
        GC.KeepAlive(svc);
    }

    [Fact]
    public void Disposed_event_subscriptions_do_not_keep_subscribers_alive()
    {
        var svc = new CommandService();
        LeakAssert.NotRetained(() =>
        {
            var subscriber = new List<string>();
            var sub = svc.Registered.Subscribe(subscriber.Add);
            sub.Dispose();
            return [subscriber];
        });
        GC.KeepAlive(svc);
    }

    [Fact]
    public async Task Execute_does_not_run_the_handler_on_the_caller_thread_before_returning_when_the_handler_is_async()
    {
        // The service itself adds no thread hops (handlers pick their lane) but must not block: an awaiting handler yields the caller.
        var svc = new CommandService();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.Register("async", async _ => { await gate.Task; return 1; });
        var task = svc.ExecuteAsync("async");
        Assert.False(task.IsCompleted);
        gate.SetResult();
        Assert.Equal(1, await task);
    }
}
