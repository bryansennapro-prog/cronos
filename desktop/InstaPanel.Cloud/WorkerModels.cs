namespace InstaPanel.Cloud;

// Espelham as respostas JSON do Worker (snake_case). Ver worker/src/account.ts.

public sealed record HealthInfo(bool Ok, string Version);

public sealed record AccountStatus(AccountInfo Account, PublishingLimit PublishingLimit);

public sealed record AccountInfo(
    string IgUserId,
    string Username,
    string? AccountType,
    string? ProfilePictureUrl,
    TokenInfo Token);

public sealed record TokenInfo(
    DateTimeOffset ExpiresAt,
    bool ExpiryKnown,
    int DaysLeft,
    DateTimeOffset ObtainedAt,
    string? LastError);

/// <summary>Uso do limite de publicação. Se a Meta falhar ao responder, vem só <see cref="Error"/>.</summary>
public sealed record PublishingLimit(
    int QuotaUsage,
    int? QuotaTotal,
    int? QuotaDuration,
    MetaErrorInfo? Error);

public sealed record MetaErrorInfo(string Message, int? Code, int? Subcode, string? Type, string? FbtraceId);

/// <summary>Início do "Entrar com Instagram": URL para abrir no navegador interno.</summary>
public sealed record OAuthStart(string AuthorizeUrl, string State, string RedirectUri);

/// <summary>Status do login: PENDING, CONNECTED ou ERROR.</summary>
public sealed record OAuthStatus(string Status, string? Username, string? Error)
{
    public bool IsConnected => Status == "CONNECTED";
    public bool IsError => Status == "ERROR";
}

internal sealed record ConnectTokenRequest(string AccessToken);

internal sealed record ConnectTokenResponse(AccountInfo Account);

internal sealed record ErrorEnvelope(ErrorBody? Error);

internal sealed record ErrorBody(string? Code, string? Message);
