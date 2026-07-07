namespace FastDOM.Broker.Interfaces;

public interface IAuthProvider : IAsyncDisposable
{
    bool IsAuthenticated { get; }
    DateTime? TokenExpiresAt { get; }
    DateTime? RefreshTokenExpiresAt { get; }

    Task<bool> LoginAsync(CancellationToken ct = default);
    Task<bool> RefreshTokenAsync(CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
    Task<string?> GetAccessTokenAsync(CancellationToken ct = default);
    bool NeedsReauth { get; }
}
