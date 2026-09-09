using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Notifications.Grimmory
{
    public class PushGrimmoryMetadataCommand : Command
    {
        public List<int> BookIds { get; set; } = new List<int>();

        public List<string> Fields { get; set; } = new List<string>();

        // Set for pushes queued right after an import, when Grimmory may not have scanned the
        // new files yet - the executor then waits for the book to appear before giving up.
        public bool WaitForBook { get; set; }

        public override bool SendUpdatesToClient => true;
    }
}
