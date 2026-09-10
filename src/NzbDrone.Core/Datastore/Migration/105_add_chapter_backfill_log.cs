using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(105)]
    public class add_chapter_backfill_log : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("ChapterBackfillLog").Exists())
            {
                Create.TableForModel("ChapterBackfillLog")
                    .WithColumn("Path").AsString().NotNullable().Indexed()
                    .WithColumn("Size").AsInt64().NotNullable()
                    .WithColumn("Outcome").AsString().NotNullable()
                    .WithColumn("Reason").AsString().Nullable()
                    .WithColumn("ProcessedAt").AsDateTime().NotNullable();
            }
        }
    }
}
