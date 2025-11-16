namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// Backup/Restore partial class for PostgreSQLDataStore
/// Note: PostgreSQL backups should use pg_dump/pg_restore command-line tools
/// </summary>
public partial class PostgreSQLDataStore
{
    /// <summary>
    /// Creates a backup of the PostgreSQL database.
    /// For PostgreSQL, use pg_dump command-line tool instead.
    /// Example: pg_dump -h localhost -p 5436 -U diffusion -d diffusion_images -F c -f backup.dump
    /// </summary>
    public void CreateBackup(string backupPath)
    {
        throw new NotSupportedException(
            "PostgreSQL backups should be created using pg_dump. " +
            "Example: pg_dump -h localhost -p 5436 -U diffusion -d diffusion_images -F c -f \"" + backupPath + "\""
        );
    }

    /// <summary>
    /// Attempts to restore a PostgreSQL database backup.
    /// For PostgreSQL, use pg_restore command-line tool instead.
    /// Example: pg_restore -h localhost -p 5436 -U diffusion -d diffusion_images -c backup.dump
    /// </summary>
    /// <returns>Always returns false as this operation is not supported</returns>
    public bool TryRestoreBackup(string backupPath)
    {
        throw new NotSupportedException(
            "PostgreSQL backups should be restored using pg_restore. " +
            "Example: pg_restore -h localhost -p 5436 -U diffusion -d diffusion_images -c \"" + backupPath + "\""
        );
    }
}
