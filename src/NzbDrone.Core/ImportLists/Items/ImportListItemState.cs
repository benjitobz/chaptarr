namespace NzbDrone.Core.ImportLists.Items
{
    public enum ImportListItemState
    {
        NotInLibrary,
        PendingAuthor,
        Excluded,
        Unmonitored,
        Missing,
        Queued,
        Downloaded
    }
}
