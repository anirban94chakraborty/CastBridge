using Microsoft.Data.Sqlite;

namespace CastBridge.Media.Library;

public sealed record TrackRecord
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public int? TrackNumber { get; init; }
    public int? DiscNumber { get; init; }
    public int? Year { get; init; }
    public long? DurationMs { get; init; }
    public string? Codec { get; init; }
    public string ContentType { get; init; } = "audio/mpeg";
    public bool RequiresTranscode { get; init; }
    public string? ArtId { get; init; }
    public long FileSize { get; init; }
    public long ModifiedTicks { get; init; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? System.IO.Path.GetFileNameWithoutExtension(Path) : Title!;

    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "Unknown artist" : Artist!;

    public string DisplayDuration => DurationMs is { } ms
        ? TimeSpan.FromMilliseconds(ms) is { TotalHours: >= 1 } t ? $"{t:h\\:mm\\:ss}" : $"{TimeSpan.FromMilliseconds(ms):m\\:ss}"
        : "--:--";

    public string FormatBadge => RequiresTranscode ? $"{(Codec ?? "convert")} → FLAC" : Codec ?? "audio";
}

/// <summary>
/// SQLite index of the music folders. Kept separate from the media server: the server only needs a
/// URL, while the UI needs search, albums and "which files are unplayable until converted".
/// </summary>
public sealed class LibraryIndex : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public LibraryIndex(string? databasePath = null)
    {
        DatabasePath = databasePath ?? CastBridge.Core.Discovery.AppPaths.LibraryDatabase;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DatabasePath)!);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS tracks (
                    id TEXT PRIMARY KEY,
                    path TEXT NOT NULL UNIQUE,
                    title TEXT, artist TEXT, album TEXT, album_artist TEXT,
                    track_number INTEGER, disc_number INTEGER, year INTEGER,
                    duration_ms INTEGER, codec TEXT, content_type TEXT NOT NULL,
                    requires_transcode INTEGER NOT NULL, art_id TEXT,
                    file_size INTEGER NOT NULL, modified_ticks INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_tracks_album ON tracks(album, disc_number, track_number);
                CREATE INDEX IF NOT EXISTS ix_tracks_artist ON tracks(artist);
                CREATE TABLE IF NOT EXISTS folders (path TEXT PRIMARY KEY, added_utc TEXT NOT NULL);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // WAL keeps reads fast while a scan is writing.
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var folders = new List<string>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM folders ORDER BY path;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            folders.Add(reader.GetString(0));

        return folders;
    }

    public async Task AddFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var full = System.IO.Path.GetFullPath(path);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO folders (path, added_utc) VALUES ($path, $added);";
        command.Parameters.AddWithValue("$path", full);
        command.Parameters.AddWithValue("$added", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var full = System.IO.Path.GetFullPath(path);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var deleteFolder = connection.CreateCommand())
        {
            deleteFolder.CommandText = "DELETE FROM folders WHERE path = $path;";
            deleteFolder.Parameters.AddWithValue("$path", full);
            await deleteFolder.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteTracks = connection.CreateCommand())
        {
            deleteTracks.CommandText = "DELETE FROM tracks WHERE path LIKE $prefix;";
            deleteTracks.Parameters.AddWithValue("$prefix", full.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar + "%");
            await deleteTracks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tracks;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyDictionary<string, TrackRecord>> GetSignaturesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, TrackRecord>(StringComparer.OrdinalIgnoreCase);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, file_size, modified_ticks, content_type, requires_transcode, codec FROM tracks;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var path = reader.GetString(0);
            result[path] = new TrackRecord
            {
                Id = path,
                Path = path,
                FileSize = reader.GetInt64(1),
                ModifiedTicks = reader.GetInt64(2),
                ContentType = reader.IsDBNull(3) ? "audio/mpeg" : reader.GetString(3),
                RequiresTranscode = !reader.IsDBNull(4) && reader.GetInt64(4) != 0,
                Codec = reader.IsDBNull(5) ? null : reader.GetString(5),
            };
        }

        return result;
    }

    public async Task UpsertAsync(IEnumerable<TrackRecord> tracks, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO tracks (id, path, title, artist, album, album_artist, track_number, disc_number, year,
                                duration_ms, codec, content_type, requires_transcode, art_id, file_size, modified_ticks)
            VALUES ($id, $path, $title, $artist, $album, $albumArtist, $track, $disc, $year,
                    $duration, $codec, $contentType, $requiresTranscode, $artId, $size, $modified)
            ON CONFLICT(path) DO UPDATE SET
                title = excluded.title, artist = excluded.artist, album = excluded.album,
                album_artist = excluded.album_artist, track_number = excluded.track_number,
                disc_number = excluded.disc_number, year = excluded.year, duration_ms = excluded.duration_ms,
                codec = excluded.codec, content_type = excluded.content_type,
                requires_transcode = excluded.requires_transcode, art_id = excluded.art_id,
                file_size = excluded.file_size, modified_ticks = excluded.modified_ticks;
            """;

        var idParameter = command.Parameters.Add("$id", SqliteType.Text);
        var pathParameter = command.Parameters.Add("$path", SqliteType.Text);
        var titleParameter = command.Parameters.Add("$title", SqliteType.Text);
        var artistParameter = command.Parameters.Add("$artist", SqliteType.Text);
        var albumParameter = command.Parameters.Add("$album", SqliteType.Text);
        var albumArtistParameter = command.Parameters.Add("$albumArtist", SqliteType.Text);
        var trackParameter = command.Parameters.Add("$track", SqliteType.Integer);
        var discParameter = command.Parameters.Add("$disc", SqliteType.Integer);
        var yearParameter = command.Parameters.Add("$year", SqliteType.Integer);
        var durationParameter = command.Parameters.Add("$duration", SqliteType.Integer);
        var codecParameter = command.Parameters.Add("$codec", SqliteType.Text);
        var contentTypeParameter = command.Parameters.Add("$contentType", SqliteType.Text);
        var transcodeParameter = command.Parameters.Add("$requiresTranscode", SqliteType.Integer);
        var artParameter = command.Parameters.Add("$artId", SqliteType.Text);
        var sizeParameter = command.Parameters.Add("$size", SqliteType.Integer);
        var modifiedParameter = command.Parameters.Add("$modified", SqliteType.Integer);

        foreach (var track in tracks)
        {
            idParameter.Value = track.Id;
            pathParameter.Value = track.Path;
            titleParameter.Value = (object?)track.Title ?? DBNull.Value;
            artistParameter.Value = (object?)track.Artist ?? DBNull.Value;
            albumParameter.Value = (object?)track.Album ?? DBNull.Value;
            albumArtistParameter.Value = (object?)track.AlbumArtist ?? DBNull.Value;
            trackParameter.Value = (object?)track.TrackNumber ?? DBNull.Value;
            discParameter.Value = (object?)track.DiscNumber ?? DBNull.Value;
            yearParameter.Value = (object?)track.Year ?? DBNull.Value;
            durationParameter.Value = (object?)track.DurationMs ?? DBNull.Value;
            codecParameter.Value = (object?)track.Codec ?? DBNull.Value;
            contentTypeParameter.Value = track.ContentType;
            transcodeParameter.Value = track.RequiresTranscode ? 1 : 0;
            artParameter.Value = (object?)track.ArtId ?? DBNull.Value;
            sizeParameter.Value = track.FileSize;
            modifiedParameter.Value = track.ModifiedTicks;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var list = paths.ToArray();
        if (list.Length == 0)
            return;

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM tracks WHERE path = $path;";
        var parameter = command.Parameters.Add("$path", SqliteType.Text);

        foreach (var path in list)
        {
            parameter.Value = path;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TrackRecord>> QueryAsync(
        string? search = null,
        int limit = 500,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var tracks = new List<TrackRecord>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var where = string.Empty;
        if (!string.IsNullOrWhiteSpace(search))
        {
            where = "WHERE title LIKE $search OR artist LIKE $search OR album LIKE $search OR path LIKE $search";
            command.Parameters.AddWithValue("$search", $"%{search.Trim()}%");
        }

        command.CommandText = $"""
            SELECT id, path, title, artist, album, album_artist, track_number, disc_number, year,
                   duration_ms, codec, content_type, requires_transcode, art_id, file_size, modified_ticks
            FROM tracks
            {where}
            ORDER BY album_artist, album, disc_number, track_number, title
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            tracks.Add(ReadTrack(reader));

        return tracks;
    }

    public async Task<IReadOnlyList<TrackRecord>> GetAlbumTracksAsync(string album, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var tracks = new List<TrackRecord>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, path, title, artist, album, album_artist, track_number, disc_number, year,
                   duration_ms, codec, content_type, requires_transcode, art_id, file_size, modified_ticks
            FROM tracks WHERE album = $album ORDER BY disc_number, track_number;
            """;
        command.Parameters.AddWithValue("$album", album);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            tracks.Add(ReadTrack(reader));

        return tracks;
    }

    public async Task<IReadOnlyList<TrackRecord>> GetTracksByPathAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var list = paths.ToArray();
        if (list.Length == 0)
            return Array.Empty<TrackRecord>();

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var tracks = new List<TrackRecord>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (var path in list)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, path, title, artist, album, album_artist, track_number, disc_number, year,
                       duration_ms, codec, content_type, requires_transcode, art_id, file_size, modified_ticks
                FROM tracks WHERE path = $path;
                """;
            command.Parameters.AddWithValue("$path", path);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                tracks.Add(ReadTrack(reader));
        }

        return tracks;
    }

    private static TrackRecord ReadTrack(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Path = reader.GetString(1),
        Title = reader.IsDBNull(2) ? null : reader.GetString(2),
        Artist = reader.IsDBNull(3) ? null : reader.GetString(3),
        Album = reader.IsDBNull(4) ? null : reader.GetString(4),
        AlbumArtist = reader.IsDBNull(5) ? null : reader.GetString(5),
        TrackNumber = reader.IsDBNull(6) ? null : reader.GetInt32(6),
        DiscNumber = reader.IsDBNull(7) ? null : reader.GetInt32(7),
        Year = reader.IsDBNull(8) ? null : reader.GetInt32(8),
        DurationMs = reader.IsDBNull(9) ? null : reader.GetInt64(9),
        Codec = reader.IsDBNull(10) ? null : reader.GetString(10),
        ContentType = reader.IsDBNull(11) ? "audio/mpeg" : reader.GetString(11),
        RequiresTranscode = !reader.IsDBNull(12) && reader.GetInt64(12) != 0,
        ArtId = reader.IsDBNull(13) ? null : reader.GetString(13),
        FileSize = reader.GetInt64(14),
        ModifiedTicks = reader.GetInt64(15),
    };

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
