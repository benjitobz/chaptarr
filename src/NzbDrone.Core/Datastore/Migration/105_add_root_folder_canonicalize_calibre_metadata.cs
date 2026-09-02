using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(105)]
    public class add_root_folder_canonicalize_calibre_metadata : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("RootFolders").Column("CanonicalizeCalibreMetadata").Exists())
            {
                Alter.Table("RootFolders")
                    .AddColumn("CanonicalizeCalibreMetadata")
                    .AsBoolean()
                    .NotNullable()
                    .WithDefaultValue(true);
            }
        }
    }
}
