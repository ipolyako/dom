using FastDOM.Broker.Interfaces;
using FastDOM.Core.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.App.Services;

public sealed class AccountSummaryCache
{
    private readonly IBrokerClient _broker;
    private readonly ILogger<AccountSummaryCache> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(10);

    public AccountSummaryCache(IBrokerClient broker, ILogger<AccountSummaryCache> logger)
    {
        _broker = broker;
        _logger = logger;
    }

    public async Task<AccountSummary> GetAsync(string accountId, TimeSpan? maxAge = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("Account id is required", nameof(accountId));

        var ageLimit = maxAge ?? DefaultMaxAge;
        if (TryGetFresh(accountId, ageLimit, out var cached))
            return cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (TryGetFresh(accountId, ageLimit, out cached))
                return cached;

            var summary = await _broker.GetAccountSummaryAsync(accountId, ct);
            _cache[accountId] = new CacheEntry(summary, DateTime.UtcNow);
            return summary;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AccountSummary> RefreshAsync(string accountId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("Account id is required", nameof(accountId));

        await _gate.WaitAsync(ct);
        try
        {
            var summary = await _broker.GetAccountSummaryAsync(accountId, ct);
            _cache[accountId] = new CacheEntry(summary, DateTime.UtcNow);
            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Account summary refresh failed for {AccountId}", accountId);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate(string accountId)
    {
        if (!string.IsNullOrWhiteSpace(accountId))
            _cache.Remove(accountId);
    }

    private bool TryGetFresh(string accountId, TimeSpan maxAge, out AccountSummary summary)
    {
        if (_cache.TryGetValue(accountId, out var entry) &&
            DateTime.UtcNow - entry.FetchedAtUtc <= maxAge)
        {
            summary = entry.Summary;
            return true;
        }

        summary = default!;
        return false;
    }

    private sealed record CacheEntry(AccountSummary Summary, DateTime FetchedAtUtc);
}
