namespace EprRegisterEnrolBackend.ReEx;

public sealed class ReExResult<T>
{
    private ReExResult(bool isSuccess, T? value, int? statusCode, ReExError? error)
    {
        IsSuccess = isSuccess;
        Value = value;
        StatusCode = statusCode;
        Error = error;
    }

    public bool IsSuccess { get; }
    public int? StatusCode { get; }
    public T? Value { get; }
    public ReExError? Error { get; }

    public static ReExResult<T> Success(T value, int statusCode) =>
        new(true, value, statusCode, null);

    public static ReExResult<T> Fail(ReExError error, int? statusCode = null) =>
        new(false, default, statusCode, error);
}

public sealed class ReExError
{
    public ReExError(ReExErrorKind kind, string? message = null)
    {
        Kind = kind;
        Message = message;
    }

    public ReExErrorKind Kind { get; }
    public string? Message { get; }
}

public enum ReExErrorKind
{
    AuthError,
    NotFound,
    ClientError,
    ServerError,
    Timeout,
    TransportError,
    DeserializationError,
}
