using System;
using System.Collections.Concurrent;
using System.Linq;

namespace NzbDrone.Core.Notifications.Grimmory
{
    // Remembers books Chaptarr itself just pushed to Grimmory so the sidecar watcher can tell
    // Chaptarr's own writes (Grimmory rewrites the sidecar after every metadata update, ours
    // included) apart from edits a person made in Grimmory. Static because both the push
    // service and the forwarder are singletons and notification instances are transient.
    public static class GrimmoryPushRegistry
    {
        private static readonly ConcurrentDictionary<int, DateTime> RecentPushes = new ConcurrentDictionary<int, DateTime>();
        private static readonly ConcurrentDictionary<int, DateTime> ConsumedEchoes = new ConcurrentDictionary<int, DateTime>();
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

        // The filesystem raises several events for one sidecar write, so the echo of a push is
        // absorbed for this long after it is first consumed. Settable so tests need not wait.
        public static TimeSpan EchoShadow { get; set; } = TimeSpan.FromSeconds(15);

        public static void RecordPush(int bookId)
        {
            RecentPushes[bookId] = DateTime.UtcNow;
        }

        public static bool WasRecentlyPushed(int bookId)
        {
            Sweep();

            return RecentPushes.TryGetValue(bookId, out var pushed) && DateTime.UtcNow - pushed <= Window;
        }

        // Decided per filesystem event, at arrival: the first sidecar event after a push is
        // its echo (Grimmory rewrites the sidecar in response to the push) and consumes the
        // entry; further events inside the shadow are duplicate notifications for that same
        // write. Anything later is a real edit and must be forwarded - deciding per batch
        // instead would let an echo and a genuine edit coalesce and be discarded together.
        public static bool ShouldSuppressSidecarEvent(int bookId)
        {
            Sweep();

            var now = DateTime.UtcNow;

            if (ConsumedEchoes.TryGetValue(bookId, out var consumedAt) && now - consumedAt <= EchoShadow)
            {
                return true;
            }

            if (RecentPushes.TryRemove(bookId, out var pushed) && now - pushed <= Window)
            {
                ConsumedEchoes[bookId] = now;
                return true;
            }

            return false;
        }

        private static void Sweep()
        {
            var now = DateTime.UtcNow;

            foreach (var stale in RecentPushes.Where(p => now - p.Value > Window).Select(p => p.Key).ToList())
            {
                RecentPushes.TryRemove(stale, out _);
            }

            foreach (var stale in ConsumedEchoes.Where(p => now - p.Value > EchoShadow).Select(p => p.Key).ToList())
            {
                ConsumedEchoes.TryRemove(stale, out _);
            }
        }

        public static void Clear()
        {
            RecentPushes.Clear();
            ConsumedEchoes.Clear();
        }
    }
}
