namespace CsCode.Remote.Protocol;

/// <summary>JSON-RPC error codes: the standard ones plus the file-system outcomes that must survive the wire as the same .NET exception types.</summary>
public static class RpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int RequestCancelled = -32800; // LSP's value

    public const int FileNotFound = -33001;
    public const int DirectoryNotFound = -33002;
    public const int AlreadyExists = -33003;
    public const int AccessDenied = -33004;
    public const int IoError = -33005;
    public const int NotSupported = -33006;
    /// <summary>A conditional write found the file changed since the caller's etag (mtime/size).</summary>
    public const int FileModified = -33007;
    /// <summary>A bounded server-side resource (uploads, search sessions) is exhausted; retry later.</summary>
    public const int Busy = -33008;
    public const int Timeout = -33009;
}

/// <summary>An error answer from the other end that has no more specific .NET type.</summary>
public class RemoteException(int code, string message, Exception? inner = null) : IOException(message, inner)
{
    public int Code { get; } = code;
}

/// <summary>The connection closed (or was closed) while the call was pending or before it was sent.</summary>
public sealed class RemoteDisconnectedException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>A conditional write was refused because the remote file changed (etag mismatch).</summary>
public sealed class RemoteFileModifiedException(string message) : IOException(message);

/// <summary>Maps exceptions to error codes on the answering side and back to the same exception types on the calling side.</summary>
public static class RpcErrorMapping
{
    public static int CodeOf(Exception ex) => ex switch
    {
        FileNotFoundException => RpcErrorCodes.FileNotFound,
        DirectoryNotFoundException => RpcErrorCodes.DirectoryNotFound,
        UnauthorizedAccessException => RpcErrorCodes.AccessDenied,
        RemoteFileModifiedException => RpcErrorCodes.FileModified,
        NotSupportedException => RpcErrorCodes.NotSupported,
        TimeoutException => RpcErrorCodes.Timeout,
        OperationCanceledException => RpcErrorCodes.RequestCancelled,
        System.Text.Json.JsonException or ArgumentException or FormatException => RpcErrorCodes.InvalidParams,
        RemoteException r => r.Code,
        IOException io when io.HResult == AlreadyExistsHResult || io.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) => RpcErrorCodes.AlreadyExists,
        IOException => RpcErrorCodes.IoError,
        _ => RpcErrorCodes.InternalError,
    };

    // EEXIST as surfaced by .NET on Unix (and ERROR_FILE_EXISTS on Windows)
    private const int AlreadyExistsHResult = unchecked((int)0x80070050);

    public static Exception ToException(int code, string message, string method) => code switch
    {
        RpcErrorCodes.FileNotFound => new FileNotFoundException(message),
        RpcErrorCodes.DirectoryNotFound => new DirectoryNotFoundException(message),
        RpcErrorCodes.AccessDenied => new UnauthorizedAccessException(message),
        RpcErrorCodes.FileModified => new RemoteFileModifiedException(message),
        RpcErrorCodes.NotSupported => new NotSupportedException(message),
        RpcErrorCodes.Timeout => new TimeoutException(message),
        RpcErrorCodes.AlreadyExists => new RemoteException(code, message),
        RpcErrorCodes.IoError => new RemoteException(code, message),
        RpcErrorCodes.MethodNotFound => new RemoteException(code, $"The remote end does not implement '{method}'"),
        _ => new RemoteException(code, message),
    };
}
