using System.Diagnostics.CodeAnalysis;

namespace CsCode.Lsp.Protocol;

/// <summary>Non-generic view of a union value (used by converters and shape matching).</summary>
public interface IOneOf
{
    /// <summary>Zero-based index of the active arm, or -1 for the default (empty) value.</summary>
    int Index { get; }
    object? Value { get; }
}

public interface IOneOfFactory<TSelf> where TSelf : struct, IOneOf
{
    static abstract TSelf Create(int index, object? value);
}

// LSP union types (`A | B | ...`). Three small structs instead of a deep generic hierarchy: LSP never uses more than 4 non-null arms
// except LSPAny, which is JsonElement. Equality is by active arm + Equals(value) (arrays compare by reference, like records).

public readonly struct OneOf<T0, T1> : IOneOf, IOneOfFactory<OneOf<T0, T1>>, IEquatable<OneOf<T0, T1>>
{
    private readonly object? _value;
    private readonly int _index; // arm + 1; 0 = default

    private OneOf(int index, object? value) { _index = index + 1; _value = value; }
    public static OneOf<T0, T1> Create(int index, object? value) => new(index, value);

    public int Index => _index - 1;
    public object? Value => _value;
    public bool IsT0 => _index == 1;
    public bool IsT1 => _index == 2;
    public T0 AsT0 => IsT0 ? (T0)_value! : throw new InvalidOperationException("Not T0");
    public T1 AsT1 => IsT1 ? (T1)_value! : throw new InvalidOperationException("Not T1");
    public bool TryGetT0([MaybeNullWhen(false)] out T0 v) { if (IsT0) { v = (T0)_value!; return true; } v = default; return false; }
    public bool TryGetT1([MaybeNullWhen(false)] out T1 v) { if (IsT1) { v = (T1)_value!; return true; } v = default; return false; }

    public TResult Match<TResult>(Func<T0, TResult> f0, Func<T1, TResult> f1) =>
        _index switch { 1 => f0((T0)_value!), 2 => f1((T1)_value!), _ => throw new InvalidOperationException("Empty union") };

    public static implicit operator OneOf<T0, T1>(T0 v) => new(0, v);
    public static implicit operator OneOf<T0, T1>(T1 v) => new(1, v);

    public bool Equals(OneOf<T0, T1> other) => _index == other._index && Equals(_value, other._value);
    public override bool Equals(object? obj) => obj is OneOf<T0, T1> o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(_index, _value);
    public override string ToString() => _index == 0 ? "<empty>" : $"{_value}";
}

public readonly struct OneOf<T0, T1, T2> : IOneOf, IOneOfFactory<OneOf<T0, T1, T2>>, IEquatable<OneOf<T0, T1, T2>>
{
    private readonly object? _value;
    private readonly int _index;

    private OneOf(int index, object? value) { _index = index + 1; _value = value; }
    public static OneOf<T0, T1, T2> Create(int index, object? value) => new(index, value);

    public int Index => _index - 1;
    public object? Value => _value;
    public bool IsT0 => _index == 1;
    public bool IsT1 => _index == 2;
    public bool IsT2 => _index == 3;
    public T0 AsT0 => IsT0 ? (T0)_value! : throw new InvalidOperationException("Not T0");
    public T1 AsT1 => IsT1 ? (T1)_value! : throw new InvalidOperationException("Not T1");
    public T2 AsT2 => IsT2 ? (T2)_value! : throw new InvalidOperationException("Not T2");
    public bool TryGetT0([MaybeNullWhen(false)] out T0 v) { if (IsT0) { v = (T0)_value!; return true; } v = default; return false; }
    public bool TryGetT1([MaybeNullWhen(false)] out T1 v) { if (IsT1) { v = (T1)_value!; return true; } v = default; return false; }
    public bool TryGetT2([MaybeNullWhen(false)] out T2 v) { if (IsT2) { v = (T2)_value!; return true; } v = default; return false; }

    public TResult Match<TResult>(Func<T0, TResult> f0, Func<T1, TResult> f1, Func<T2, TResult> f2) =>
        _index switch { 1 => f0((T0)_value!), 2 => f1((T1)_value!), 3 => f2((T2)_value!), _ => throw new InvalidOperationException("Empty union") };

    public static implicit operator OneOf<T0, T1, T2>(T0 v) => new(0, v);
    public static implicit operator OneOf<T0, T1, T2>(T1 v) => new(1, v);
    public static implicit operator OneOf<T0, T1, T2>(T2 v) => new(2, v);

    public bool Equals(OneOf<T0, T1, T2> other) => _index == other._index && Equals(_value, other._value);
    public override bool Equals(object? obj) => obj is OneOf<T0, T1, T2> o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(_index, _value);
    public override string ToString() => _index == 0 ? "<empty>" : $"{_value}";
}

public readonly struct OneOf<T0, T1, T2, T3> : IOneOf, IOneOfFactory<OneOf<T0, T1, T2, T3>>, IEquatable<OneOf<T0, T1, T2, T3>>
{
    private readonly object? _value;
    private readonly int _index;

    private OneOf(int index, object? value) { _index = index + 1; _value = value; }
    public static OneOf<T0, T1, T2, T3> Create(int index, object? value) => new(index, value);

    public int Index => _index - 1;
    public object? Value => _value;
    public bool IsT0 => _index == 1;
    public bool IsT1 => _index == 2;
    public bool IsT2 => _index == 3;
    public bool IsT3 => _index == 4;
    public T0 AsT0 => IsT0 ? (T0)_value! : throw new InvalidOperationException("Not T0");
    public T1 AsT1 => IsT1 ? (T1)_value! : throw new InvalidOperationException("Not T1");
    public T2 AsT2 => IsT2 ? (T2)_value! : throw new InvalidOperationException("Not T2");
    public T3 AsT3 => IsT3 ? (T3)_value! : throw new InvalidOperationException("Not T3");
    public bool TryGetT0([MaybeNullWhen(false)] out T0 v) { if (IsT0) { v = (T0)_value!; return true; } v = default; return false; }
    public bool TryGetT1([MaybeNullWhen(false)] out T1 v) { if (IsT1) { v = (T1)_value!; return true; } v = default; return false; }
    public bool TryGetT2([MaybeNullWhen(false)] out T2 v) { if (IsT2) { v = (T2)_value!; return true; } v = default; return false; }
    public bool TryGetT3([MaybeNullWhen(false)] out T3 v) { if (IsT3) { v = (T3)_value!; return true; } v = default; return false; }

    public TResult Match<TResult>(Func<T0, TResult> f0, Func<T1, TResult> f1, Func<T2, TResult> f2, Func<T3, TResult> f3) =>
        _index switch { 1 => f0((T0)_value!), 2 => f1((T1)_value!), 3 => f2((T2)_value!), 4 => f3((T3)_value!), _ => throw new InvalidOperationException("Empty union") };

    public static implicit operator OneOf<T0, T1, T2, T3>(T0 v) => new(0, v);
    public static implicit operator OneOf<T0, T1, T2, T3>(T1 v) => new(1, v);
    public static implicit operator OneOf<T0, T1, T2, T3>(T2 v) => new(2, v);
    public static implicit operator OneOf<T0, T1, T2, T3>(T3 v) => new(3, v);

    public bool Equals(OneOf<T0, T1, T2, T3> other) => _index == other._index && Equals(_value, other._value);
    public override bool Equals(object? obj) => obj is OneOf<T0, T1, T2, T3> o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(_index, _value);
    public override string ToString() => _index == 0 ? "<empty>" : $"{_value}";
}
