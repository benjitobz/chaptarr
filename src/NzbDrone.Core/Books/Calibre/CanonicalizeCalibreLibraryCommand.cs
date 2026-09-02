using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books.Calibre
{
    public class CanonicalizeCalibreLibraryCommand : Command
    {
        public int AuthorId { get; set; }

        public override bool SendUpdatesToClient => false;
    }
}
