namespace NzbDrone.Core.ImportLists.Items
{
    public enum ImportListItemState
    {
        Unknown,
        NotInLibrary,
        PendingAuthor,
        Excluded,
        Unmonitored,
        Missing,
        Queued,
        Downloaded
    }
}
