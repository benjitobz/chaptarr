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
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

        public static void RecordPush(int bookId)
        {
            RecentPushes[bookId] = DateTime.UtcNow;
        }

        public static bool WasRecentlyPushed(int bookId)
        {
            Sweep();

            return RecentPushes.TryGetValue(bookId, out var pushed) && DateTime.UtcNow - pushed <= Window;
        }

        // One-shot: a push causes exactly one sidecar rewrite in Grimmory, so the first
        // matching sidecar event consumes the entry. A person's edit made shortly after a
        // push is then still forwarded instead of being discarded as an echo.
        public static bool TryConsumeRecentPush(int bookId)
        {
            Sweep();

            return RecentPushes.TryRemove(bookId, out var pushed) && DateTime.UtcNow - pushed <= Window;
        }

        private static void Sweep()
        {
            var now = DateTime.UtcNow;

            foreach (var stale in RecentPushes.Where(p => now - p.Value > Window).Select(p => p.Key).ToList())
            {
                RecentPushes.TryRemove(stale, out _);
            }
        }

        public static void Clear()
        {
            RecentPushes.Clear();
        }
    }
}
