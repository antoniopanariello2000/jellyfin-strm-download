using System;
using System.Threading;

namespace Jellyfin.Plugin.StrmDownload.Web;

/// <summary>
/// Caps how many .strm downloads are proxied at the same time. Registered as a
/// singleton so the cap spans the whole server, not a single request.
/// <para>
/// The upstream a .strm points at usually has a far smaller connection budget
/// than Jellyfin has clients; without a cap a handful of parallel downloads can
/// exhaust it and break playback for everyone.
/// </para>
/// </summary>
public sealed class DownloadConcurrencyLimiter
{
    private static readonly IDisposable Unlimited = new Slot(null);

    private readonly Lock _lock = new();

    private SemaphoreSlim? _semaphore;
    private int _limit;

    /// <summary>
    /// Tries to take one of the configured slots without waiting.
    /// </summary>
    /// <returns>
    /// A handle whose disposal returns the slot, or <c>null</c> when all slots
    /// are currently taken. A configured limit of 0 or less means unlimited and
    /// always yields a handle.
    /// </returns>
    public IDisposable? TryAcquire()
    {
        var semaphore = GetSemaphore();
        if (semaphore is null)
        {
            return Unlimited;
        }

        return semaphore.Wait(0) ? new Slot(semaphore) : null;
    }

    private SemaphoreSlim? GetSemaphore()
    {
        var limit = Plugin.Instance?.Configuration.MaxConcurrentDownloads ?? 0;
        if (limit <= 0)
        {
            return null;
        }

        lock (_lock)
        {
            if (_semaphore is null || _limit != limit)
            {
                // The option can be changed while downloads are in flight, and
                // SemaphoreSlim cannot be resized. The old instance is replaced
                // rather than disposed: downloads still running hold a reference
                // to it and release into it, and it is collected once the last
                // one finishes. The new limit applies from the next request on.
                _semaphore = new SemaphoreSlim(limit, limit);
                _limit = limit;
            }

            return _semaphore;
        }
    }

    private sealed class Slot : IDisposable
    {
        private readonly SemaphoreSlim? _semaphore;
        private int _released;

        public Slot(SemaphoreSlim? semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if (_semaphore is not null && Interlocked.Exchange(ref _released, 1) == 0)
            {
                _semaphore.Release();
            }
        }
    }
}
