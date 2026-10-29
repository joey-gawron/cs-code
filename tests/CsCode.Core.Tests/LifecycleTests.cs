using CsCode.Core.Lifecycle;
using Xunit;

namespace CsCode.Core.Tests;

public class LifecycleTests
{
    [Fact]
    public void Emitter_delivers_to_all_subscribers_and_unsubscribes_on_dispose()
    {
        var emitter = new Emitter<int>();
        var a = new List<int>();
        var b = new List<int>();
        using var subA = emitter.Subscribe(a.Add);
        var subB = emitter.Subscribe(b.Add);

        emitter.Fire(1);
        subB.Dispose();
        emitter.Fire(2);

        Assert.Equal([1, 2], a);
        Assert.Equal([1], b);
        Assert.Equal(1, emitter.SubscriberCount);
    }

    [Fact]
    public void Emitter_isolates_throwing_handlers()
    {
        var emitter = new Emitter<int>();
        var errors = new List<Exception>();
        emitter.OnHandlerError = errors.Add;
        var got = 0;
        emitter.Subscribe(_ => throw new InvalidOperationException("boom"));
        emitter.Subscribe(v => got = v);

        emitter.Fire(7);

        Assert.Equal(7, got);
        Assert.Single(errors);
    }

    [Fact]
    public void Emitter_double_dispose_is_safe()
    {
        var emitter = new Emitter<int>();
        var sub = emitter.Subscribe(_ => { });
        sub.Dispose();
        sub.Dispose();
        Assert.Equal(0, emitter.SubscriberCount);
    }

    [Fact]
    public void DisposableStore_disposes_in_reverse_order_once_and_disposes_late_adds()
    {
        var order = new List<int>();
        var store = new DisposableStore();
        store.Add(Disposable.Create(() => order.Add(1)));
        store.Add(Disposable.Create(() => order.Add(2)));

        store.Dispose();
        store.Dispose();
        store.Add(Disposable.Create(() => order.Add(3)));

        Assert.Equal([2, 1, 3], order);
        Assert.True(store.IsDisposed);
    }

    [Fact]
    public void DisposableStore_aggregates_errors_but_still_disposes_everything()
    {
        var disposed = 0;
        var store = new DisposableStore();
        store.Add(Disposable.Create(() => disposed++));
        store.Add(Disposable.Create(() => throw new InvalidOperationException("x")));
        store.Add(Disposable.Create(() => disposed++));

        Assert.Throws<AggregateException>(store.Dispose);
        Assert.Equal(2, disposed);
    }

    [Fact]
    public void Emitter_does_not_keep_unsubscribed_handlers_alive()
    {
        var emitter = new Emitter<int>();
        var weak = Subscribe(emitter);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(weak.TryGetTarget(out _));
        GC.KeepAlive(emitter);

        static WeakReference<object> Subscribe(Emitter<int> e)
        {
            var target = new object();
            var sub = e.Subscribe(_ => GC.KeepAlive(target));
            sub.Dispose();
            return new WeakReference<object>(target);
        }
    }
}
