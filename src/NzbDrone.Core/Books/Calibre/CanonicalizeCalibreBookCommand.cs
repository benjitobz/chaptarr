using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books.Calibre
{
    public class CanonicalizeCalibreBookCommand : Command
    {
        public int BookId { get; set; }

        public override bool SendUpdatesToClient => true;
    }
}
