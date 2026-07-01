using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace QuickLooker.Core;

public sealed class ThumbnailCache
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    private readonly string _cacheRoot;
    private readonly string _thumbnailDirectory;
    private readonly string _databasePath;
    private readonly CacheMaintenanceOptions _maintenanceOptions;

    public ThumbnailCache(string? cacheRoot = null, CacheMaintenanceOptions? maintenanceOptions = null)
    {
        SqliteBootstrap.EnsureInitialized();

        _cacheRoot = cacheRoot ?? AppStoragePaths.CacheRoot;
        _thumbnailDirectory = Path.Combine(_cacheRoot, "thumbs");
        _databasePath = Path.Combine(_cacheRoot, "picpreview-cache.db");
        _maintenanceOptions = maintenanceOptions ?? CacheMaintenanceOptions.Default;

        Directory.CreateDirectory(_cacheRoot);
        Directory.CreateDirectory(_thumbnailDirectory);

        InitializeDatabase();
    }

    public async Task<ThumbnailRecord> GetOrCreateAsync(
        string sourcePath,
        int size = 256,
        CancellationToken cancellationToken = default)
    {
        await RunMaintenanceIfDueAsync(cancellationToken).ConfigureAwait(false);

        if (!SupportedImageFormats.IsSupported(sourcePath))
        {
            throw new NotSupportedException($"暂不支持这个图片格式：{Path.GetExtension(sourcePath)}");
        }

        var fingerprint = FileFingerprint.FromPath(sourcePath);
        var cached = await TryGetAsync(fingerprint, size, cancellationToken).ConfigureAwait(false);

        if (cached is not null)
        {
            return cached;
        }

        await WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            cached = await TryGetAsync(fingerprint, size, cancellationToken).ConfigureAwait(false);

            if (cached is not null)
            {
                return cached;
            }

            var thumbnailPath = GetThumbnailPath(fingerprint, size);
            var tempPath = $"{thumbnailPath}.{Guid.NewGuid():N}.tmp";

            try
            {
                var rendered = await ImageRenderer.RenderThumbnailAsync(
                    fingerprint.FullPath,
                    tempPath,
                    size,
                    cancellationToken).ConfigureAwait(false);

                File.Move(tempPath, thumbnailPath, true);

                var record = new ThumbnailRecord(
                    fingerprint.FullPath,
                    thumbnailPath,
                    size,
                    rendered.Width,
                    rendered.Height,
                    fingerprint.Length,
                    fingerprint.LastWriteUtcTicks);

                await UpsertAsync(record, cancellationToken).ConfigureAwait(false);
                return record;
            }
            finally
            {
                TryDelete(tempPath);
            }
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<ThumbnailRecord?> TryGetAsync(
        FileFingerprint fingerprint,
        int size,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT thumbnail_path, width, height
            FROM thumbnails
            WHERE source_path = $source_path
              AND size = $size
              AND source_length = $source_length
              AND source_last_write_utc_ticks = $source_last_write_utc_ticks
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$source_path", fingerprint.FullPath);
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$source_length", fingerprint.Length);
        command.Parameters.AddWithValue("$source_last_write_utc_ticks", fingerprint.LastWriteUtcTicks);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var thumbnailPath = reader.GetString(0);
        var width = reader.GetInt32(1);
        var height = reader.GetInt32(2);
        var currentThumbnailPath = GetThumbnailPath(fingerprint, size);

        if (!string.Equals(
            Path.GetFullPath(thumbnailPath),
            Path.GetFullPath(currentThumbnailPath),
            StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!File.Exists(thumbnailPath))
        {
            return null;
        }

        await TouchAsync(fingerprint.FullPath, size, cancellationToken).ConfigureAwait(false);

        return new ThumbnailRecord(
            fingerprint.FullPath,
            thumbnailPath,
            size,
            width,
            height,
            fingerprint.Length,
            fingerprint.LastWriteUtcTicks);
    }

    private void InitializeDatabase()
    {
        using var connection = CreateConnection();
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            """;
        pragma.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS thumbnails (
                source_path TEXT NOT NULL,
                size INTEGER NOT NULL,
                source_length INTEGER NOT NULL,
                source_last_write_utc_ticks INTEGER NOT NULL,
                thumbnail_path TEXT NOT NULL,
                width INTEGER NOT NULL,
                height INTEGER NOT NULL,
                created_utc_ticks INTEGER NOT NULL,
                last_access_utc_ticks INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (source_path, size)
            );

            CREATE INDEX IF NOT EXISTS ix_thumbnails_file_state
                ON thumbnails(source_path, source_length, source_last_write_utc_ticks);

            CREATE TABLE IF NOT EXISTS cache_metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        using var migration = connection.CreateCommand();
        migration.CommandText = """
            PRAGMA table_info(thumbnails);
            """;

        var hasLastAccess = false;

        using (var reader = migration.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "last_access_utc_ticks", StringComparison.OrdinalIgnoreCase))
                {
                    hasLastAccess = true;
                    break;
                }
            }
        }

        if (!hasLastAccess)
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = """
                ALTER TABLE thumbnails ADD COLUMN last_access_utc_ticks INTEGER NOT NULL DEFAULT 0;
                UPDATE thumbnails SET last_access_utc_ticks = created_utc_ticks WHERE last_access_utc_ticks = 0;
                """;
            alter.ExecuteNonQuery();
        }
    }

    private async Task UpsertAsync(ThumbnailRecord record, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO thumbnails (
                source_path,
                size,
                source_length,
                source_last_write_utc_ticks,
                thumbnail_path,
                width,
                height,
                last_access_utc_ticks,
                created_utc_ticks
            )
            VALUES (
                $source_path,
                $size,
                $source_length,
                $source_last_write_utc_ticks,
                $thumbnail_path,
                $width,
                $height,
                $last_access_utc_ticks,
                $created_utc_ticks
            )
            ON CONFLICT(source_path, size) DO UPDATE SET
                source_length = excluded.source_length,
                source_last_write_utc_ticks = excluded.source_last_write_utc_ticks,
                thumbnail_path = excluded.thumbnail_path,
                width = excluded.width,
                height = excluded.height,
                last_access_utc_ticks = excluded.last_access_utc_ticks,
                created_utc_ticks = excluded.created_utc_ticks;
            """;

        var nowTicks = DateTime.UtcNow.Ticks;
        command.Parameters.AddWithValue("$source_path", record.SourcePath);
        command.Parameters.AddWithValue("$size", record.Size);
        command.Parameters.AddWithValue("$source_length", record.SourceLength);
        command.Parameters.AddWithValue("$source_last_write_utc_ticks", record.SourceLastWriteUtcTicks);
        command.Parameters.AddWithValue("$thumbnail_path", record.ThumbnailPath);
        command.Parameters.AddWithValue("$width", record.Width);
        command.Parameters.AddWithValue("$height", record.Height);
        command.Parameters.AddWithValue("$last_access_utc_ticks", nowTicks);
        command.Parameters.AddWithValue("$created_utc_ticks", nowTicks);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TouchAsync(string sourcePath, int size, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE thumbnails
            SET last_access_utc_ticks = $last_access_utc_ticks
            WHERE source_path = $source_path AND size = $size;
            """;
        command.Parameters.AddWithValue("$last_access_utc_ticks", DateTime.UtcNow.Ticks);
        command.Parameters.AddWithValue("$source_path", sourcePath);
        command.Parameters.AddWithValue("$size", size);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunMaintenanceIfDueAsync(CancellationToken cancellationToken)
    {
        await WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!await IsMaintenanceDueAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await RemoveExpiredEntriesAsync(cancellationToken).ConfigureAwait(false);
            await TrimThumbnailDirectoryAsync(cancellationToken).ConfigureAwait(false);
            await StoreLastMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    private async Task<bool> IsMaintenanceDueAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT value FROM cache_metadata WHERE key = 'last_maintenance_utc_ticks';
            """;

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (value is null || !long.TryParse(Convert.ToString(value), out var ticks))
        {
            return true;
        }

        return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) >= _maintenanceOptions.MinimumCleanupInterval;
    }

    private async Task StoreLastMaintenanceAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cache_metadata (key, value)
            VALUES ('last_maintenance_utc_ticks', $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$value", DateTime.UtcNow.Ticks.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveExpiredEntriesAsync(CancellationToken cancellationToken)
    {
        var cutoffTicks = (DateTime.UtcNow - _maintenanceOptions.MaxEntryAge).Ticks;
        var toDelete = new List<string>();

        await using (var connection = CreateConnection())
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var select = connection.CreateCommand();
            select.CommandText = """
                SELECT thumbnail_path
                FROM thumbnails
                WHERE last_access_utc_ticks > 0 AND last_access_utc_ticks < $cutoff_ticks;
                """;
            select.Parameters.AddWithValue("$cutoff_ticks", cutoffTicks);

            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                toDelete.Add(reader.GetString(0));
            }
        }

        foreach (var path in toDelete)
        {
            TryDelete(path);
        }

        await using var deleteConnection = CreateConnection();
        await deleteConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var delete = deleteConnection.CreateCommand();
        delete.CommandText = """
            DELETE FROM thumbnails
            WHERE last_access_utc_ticks > 0 AND last_access_utc_ticks < $cutoff_ticks;
            """;
        delete.Parameters.AddWithValue("$cutoff_ticks", cutoffTicks);

        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TrimThumbnailDirectoryAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_thumbnailDirectory))
        {
            return;
        }

        var files = Directory.EnumerateFiles(_thumbnailDirectory, "*.png")
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .OrderBy(file => file.LastAccessTimeUtc)
            .ThenBy(file => file.LastWriteTimeUtc)
            .ToList();

        var totalBytes = files.Sum(file => file.Length);

        if (totalBytes <= _maintenanceOptions.MaxThumbnailBytes)
        {
            return;
        }

        var deleted = new List<string>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (totalBytes <= _maintenanceOptions.MaxThumbnailBytes)
            {
                break;
            }

            totalBytes -= file.Length;
            deleted.Add(file.FullName);
            TryDelete(file.FullName);
        }

        if (deleted.Count == 0)
        {
            return;
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (var path in deleted)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM thumbnails WHERE thumbnail_path = $thumbnail_path;
                """;
            command.Parameters.AddWithValue("$thumbnail_path", path);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        return new SqliteConnection(builder.ToString());
    }

    private string GetThumbnailPath(FileFingerprint fingerprint, int size)
    {
        var material = $"{fingerprint.FullPath}|{fingerprint.Length}|{fingerprint.LastWriteUtcTicks}|{size}|{ImageRenderer.RenderCacheVersion}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return Path.Combine(_thumbnailDirectory, $"{hash}.png");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cache cleanup is best effort.
        }
    }
}
