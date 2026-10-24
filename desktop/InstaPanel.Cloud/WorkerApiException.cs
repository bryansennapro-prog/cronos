namespace InstaPanel.Cloud;

/// <summary>Falha ao falar com o Worker. <see cref="Code"/> é o código do Worker ou "unreachable"/"timeout".</summary>
public sealed class WorkerApiException : Exception
{
    public WorkerApiException(int statusCode, string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Code = code;
    }

    /// <summary>Status HTTP; 0 quando não houve resposta.</summary>
    public int StatusCode { get; }

    public string Code { get; }

    public bool IsNotConnected => Code == "not_connected";

    public bool IsUnauthorized => StatusCode == 401;
}
