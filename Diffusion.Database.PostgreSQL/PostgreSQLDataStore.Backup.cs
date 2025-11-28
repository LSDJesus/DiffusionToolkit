using System.Diagnostics;
using Npgsql;
using Diffusion.Common;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// Backup/Restore partial class for PostgreSQLDataStore
/// Uses pg_dump/pg_restore via Docker exec or local installation
/// </summary>
public partial class PostgreSQLDataStore
{
    /// <summary>
    /// Creates a backup of the PostgreSQL database using pg_dump.
    /// Attempts Docker exec first, falls back to local pg_dump if available.
    /// </summary>
    /// <param name="backupPath">Path where the backup file will be saved</param>
    /// <returns>True if backup succeeded, false otherwise</returns>
    public bool CreateBackup(string backupPath)
    {
        var connBuilder = new NpgsqlConnectionStringBuilder(_connectionString);
        var host = connBuilder.Host ?? "localhost";
        var port = connBuilder.Port;
        var database = connBuilder.Database ?? "diffusion_images";
        var username = connBuilder.Username ?? "diffusion";
        var password = connBuilder.Password ?? "";

        try
        {
            // Try Docker exec first (most common for this project)
            if (TryDockerBackup(backupPath, database, username, password))
            {
                Logger.Log($"✓ Database backup created via Docker: {backupPath}");
                return true;
            }

            // Fall back to local pg_dump
            if (TryLocalPgDump(backupPath, host, port, database, username, password))
            {
                Logger.Log($"✓ Database backup created via pg_dump: {backupPath}");
                return true;
            }

            Logger.Log("✗ Database backup failed: pg_dump not available");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Log($"✗ Database backup failed: {ex.Message}");
            return false;
        }
    }

    private bool TryDockerBackup(string backupPath, string database, string username, string password)
    {
        try
        {
            // Check if Docker container is running
            var checkProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "ps -q -f name=diffusion_toolkit_db",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            checkProcess.Start();
            var containerId = checkProcess.StandardOutput.ReadToEnd().Trim();
            checkProcess.WaitForExit();

            if (string.IsNullOrEmpty(containerId))
                return false;

            // Create backup using docker exec
            var backupFileName = Path.GetFileName(backupPath);
            var containerBackupPath = $"/tmp/{backupFileName}";

            // Run pg_dump inside container
            var dumpProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"exec -e PGPASSWORD={password} diffusion_toolkit_db pg_dump -U {username} -d {database} -F c -f {containerBackupPath}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            dumpProcess.Start();
            var error = dumpProcess.StandardError.ReadToEnd();
            dumpProcess.WaitForExit();

            if (dumpProcess.ExitCode != 0)
            {
                Logger.Log($"Docker pg_dump failed: {error}");
                return false;
            }

            // Copy backup from container to host
            var copyProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"cp diffusion_toolkit_db:{containerBackupPath} \"{backupPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            copyProcess.Start();
            copyProcess.WaitForExit();

            // Clean up container temp file
            var cleanupProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"exec diffusion_toolkit_db rm -f {containerBackupPath}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            cleanupProcess.Start();
            cleanupProcess.WaitForExit();

            return File.Exists(backupPath);
        }
        catch
        {
            return false;
        }
    }

    private bool TryLocalPgDump(string backupPath, string host, int port, string database, string username, string password)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pg_dump",
                    Arguments = $"-h {host} -p {port} -U {username} -d {database} -F c -f \"{backupPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Environment = { ["PGPASSWORD"] = password }
                }
            };

            process.Start();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                Logger.Log($"Local pg_dump failed: {error}");
                return false;
            }

            return File.Exists(backupPath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Restores a PostgreSQL database backup using pg_restore.
    /// Attempts Docker exec first, falls back to local pg_restore if available.
    /// </summary>
    /// <param name="backupPath">Path to the backup file</param>
    /// <returns>True if restore succeeded, false otherwise</returns>
    public bool TryRestoreBackup(string backupPath)
    {
        if (!File.Exists(backupPath))
        {
            Logger.Log($"✗ Backup file not found: {backupPath}");
            return false;
        }

        var connBuilder = new NpgsqlConnectionStringBuilder(_connectionString);
        var host = connBuilder.Host ?? "localhost";
        var port = connBuilder.Port;
        var database = connBuilder.Database ?? "diffusion_images";
        var username = connBuilder.Username ?? "diffusion";
        var password = connBuilder.Password ?? "";

        try
        {
            // Try Docker exec first
            if (TryDockerRestore(backupPath, database, username, password))
            {
                Logger.Log($"✓ Database restored via Docker: {backupPath}");
                return true;
            }

            // Fall back to local pg_restore
            if (TryLocalPgRestore(backupPath, host, port, database, username, password))
            {
                Logger.Log($"✓ Database restored via pg_restore: {backupPath}");
                return true;
            }

            Logger.Log("✗ Database restore failed: pg_restore not available");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Log($"✗ Database restore failed: {ex.Message}");
            return false;
        }
    }

    private bool TryDockerRestore(string backupPath, string database, string username, string password)
    {
        try
        {
            // Check if Docker container is running
            var checkProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "ps -q -f name=diffusion_toolkit_db",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            checkProcess.Start();
            var containerId = checkProcess.StandardOutput.ReadToEnd().Trim();
            checkProcess.WaitForExit();

            if (string.IsNullOrEmpty(containerId))
                return false;

            // Copy backup to container
            var backupFileName = Path.GetFileName(backupPath);
            var containerBackupPath = $"/tmp/{backupFileName}";

            var copyProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"cp \"{backupPath}\" diffusion_toolkit_db:{containerBackupPath}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            copyProcess.Start();
            copyProcess.WaitForExit();

            // Run pg_restore inside container (with --clean to drop existing objects)
            var restoreProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"exec -e PGPASSWORD={password} diffusion_toolkit_db pg_restore -U {username} -d {database} --clean --if-exists {containerBackupPath}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            restoreProcess.Start();
            var error = restoreProcess.StandardError.ReadToEnd();
            restoreProcess.WaitForExit();

            // Clean up container temp file
            var cleanupProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"exec diffusion_toolkit_db rm -f {containerBackupPath}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            cleanupProcess.Start();
            cleanupProcess.WaitForExit();

            // pg_restore may return non-zero for warnings, check if critical
            if (restoreProcess.ExitCode != 0 && error.Contains("FATAL"))
            {
                Logger.Log($"Docker pg_restore failed: {error}");
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryLocalPgRestore(string backupPath, string host, int port, string database, string username, string password)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pg_restore",
                    Arguments = $"-h {host} -p {port} -U {username} -d {database} --clean --if-exists \"{backupPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Environment = { ["PGPASSWORD"] = password }
                }
            };

            process.Start();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            // pg_restore may return non-zero for warnings
            if (process.ExitCode != 0 && error.Contains("FATAL"))
            {
                Logger.Log($"Local pg_restore failed: {error}");
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the backup command for manual execution
    /// </summary>
    public string GetBackupCommand(string backupPath)
    {
        var connBuilder = new NpgsqlConnectionStringBuilder(_connectionString);
        return $"docker exec -e PGPASSWORD={connBuilder.Password} diffusion_toolkit_db pg_dump -U {connBuilder.Username} -d {connBuilder.Database} -F c > \"{backupPath}\"";
    }

    /// <summary>
    /// Gets the restore command for manual execution
    /// </summary>
    public string GetRestoreCommand(string backupPath)
    {
        var connBuilder = new NpgsqlConnectionStringBuilder(_connectionString);
        return $"docker exec -i -e PGPASSWORD={connBuilder.Password} diffusion_toolkit_db pg_restore -U {connBuilder.Username} -d {connBuilder.Database} --clean --if-exists < \"{backupPath}\"";
    }
}
