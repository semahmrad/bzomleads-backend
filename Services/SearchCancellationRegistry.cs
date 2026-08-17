using System.Collections.Concurrent;

namespace Backend.Services;

public sealed class SearchCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();

    public SearchCancellationLease Register(
        string userId,
        string searchSessionId,
        CancellationToken requestCancellationToken)
    {
        var key = BuildKey(userId, searchSessionId);
        var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(requestCancellationToken);

        _sessions.AddOrUpdate(
            key,
            cancellationSource,
            (_, previousSource) =>
            {
                previousSource.Cancel();
                previousSource.Dispose();
                return cancellationSource;
            });

        return new SearchCancellationLease(
            cancellationSource.Token,
            () => Remove(key, cancellationSource));
    }

    public bool Cancel(string userId, string searchSessionId)
    {
        var key = BuildKey(userId, searchSessionId);
        if (!_sessions.TryGetValue(key, out var cancellationSource))
        {
            return false;
        }

        cancellationSource.Cancel();
        return true;
    }

    private void Remove(string key, CancellationTokenSource cancellationSource)
    {
        var entry = new KeyValuePair<string, CancellationTokenSource>(key, cancellationSource);
        ((ICollection<KeyValuePair<string, CancellationTokenSource>>)_sessions).Remove(entry);
        cancellationSource.Dispose();
    }

    private static string BuildKey(string userId, string searchSessionId)
    {
        if (!Guid.TryParse(searchSessionId, out var parsedSessionId))
        {
            throw new ArgumentException("L identifiant de la recherche est invalide.", nameof(searchSessionId));
        }

        return $"{userId}:{parsedSessionId:N}";
    }
}

public sealed class SearchCancellationLease : IDisposable
{
    private Action? _dispose;

    public SearchCancellationLease(CancellationToken cancellationToken, Action dispose)
    {
        CancellationToken = cancellationToken;
        _dispose = dispose;
    }

    public CancellationToken CancellationToken { get; }

    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
