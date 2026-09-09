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
            var now = DateTime.UtcNow;

            foreach (var stale in RecentPushes.Where(p => now - p.Value > Window).Select(p => p.Key).ToList())
            {
                RecentPushes.TryRemove(stale, out _);
            }

            return RecentPushes.TryGetValue(bookId, out var pushed) && now - pushed <= Window;
        }

        public static void Clear()
        {
            RecentPushes.Clear();
        }
    }
}
