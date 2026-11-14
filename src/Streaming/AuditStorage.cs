using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Streaming;

public sealed record UploadedDocument(string FileName, string? ContentType, byte[] Content);
public sealed record PlayAuditReportItem(string Title, IReadOnlyList<DateTime> PlayedAtUtc, IReadOnlyList<PlayAuditMonth> Monthly);
public sealed record PlayAuditMonth(string Month, int PlayCount);

public interface IAuditStorage
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<long?> StorePurchaseEvidenceAsync(UploadedDocument document, CancellationToken cancellationToken = default);
    Task<long> IndexTrackAsync(string fullPath, long? purchaseEvidenceId, UploadedDocument? license, CancellationToken cancellationToken = default);
    Task<long?> FindTrackIdByMusicUriAsync(string uri, CancellationToken cancellationToken = default);
    Task<long?> FindTrackIdByRelativePathAsync(string relativePath, CancellationToken cancellationToken = default);
    Task RecordPlayAsync(long trackId, DateTime playedAtUtc, string playbackMode, CancellationToken cancellationToken = default);
    Task RebuildMonthlySummaryAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlayAuditReportItem>> GetPlayAuditReportAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, string>> GetEffectiveGenresAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, TrackMetadata>> GetTrackMetadataAsync(CancellationToken cancellationToken = default);
    Task SetGenreOverrideAsync(long trackId, string? genre, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetGenresAsync(CancellationToken cancellationToken = default);
}

public sealed class AuditStorage (StreamingOptions options): IAuditStorage
{
    private static readonly string[] DefaultGenres = ["Blues", "Relax", "Country", "Dance", "Elektronisch", "Folk", "Funk", "Hip-hop", "Jazz", "Klassiek", "Latin", "Metal", "Pop", "Reggae", "Rock", "Soul", "Wereldmuziek"];

    public async Task<IReadOnlyList<string>> GetGenresAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM Genre ORDER BY Name";
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(0));
        return result;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS MusicFile (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Checksum TEXT NOT NULL UNIQUE,
                ChecksumAlgorithm TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                FileName TEXT NOT NULL,
                Title TEXT NOT NULL,
                MetadataJson TEXT NULL,
                FileSizeBytes INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                IndexedAtUtc TEXT NOT NULL,
                LicenseFileName TEXT NULL,
                LicenseContentType TEXT NULL,
                LicenseContent BLOB NULL,
                Genre TEXT NULL,
                GenreOverride TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS PurchaseEvidence (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FileName TEXT NOT NULL,
                ContentType TEXT NULL,
                Content BLOB NOT NULL,
                CreatedAtUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS MusicFilePurchaseEvidence (
                MusicFileId INTEGER NOT NULL REFERENCES MusicFile(Id) ON DELETE CASCADE,
                PurchaseEvidenceId INTEGER NOT NULL REFERENCES PurchaseEvidence(Id) ON DELETE RESTRICT,
                PRIMARY KEY (MusicFileId, PurchaseEvidenceId)
            );
            CREATE TABLE IF NOT EXISTS PlayAudit (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MusicFileId INTEGER NOT NULL REFERENCES MusicFile(Id) ON DELETE RESTRICT,
                PlayedAtUtc TEXT NOT NULL,
                PlaybackMode TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_PlayAudit_MusicFileId_PlayedAtUtc ON PlayAudit(MusicFileId, PlayedAtUtc);
            CREATE TABLE IF NOT EXISTS PlayAuditMonthly (
                MusicFileId INTEGER NOT NULL REFERENCES MusicFile(Id) ON DELETE RESTRICT,
                MonthUtc TEXT NOT NULL,
                PlayCount INTEGER NOT NULL,
                CalculatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (MusicFileId, MonthUtc)
            );
            CREATE TABLE IF NOT EXISTS Genre (
                Name TEXT PRIMARY KEY COLLATE NOCASE
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        // Seed only an empty table, so genres a customer removes later do not come back.
        var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Genre";
        if ((long)(await count.ExecuteScalarAsync(cancellationToken))! == 0)
        {
            var seed = connection.CreateCommand();
            seed.CommandText = "INSERT OR IGNORE INTO Genre (Name) VALUES ($name)";
            var seedName = seed.Parameters.Add("$name", SqliteType.Text);
            foreach (var name in DefaultGenres)
            {
                seedName.Value = name;
                await seed.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        // CREATE TABLE IF NOT EXISTS does not add columns to a database that already existed before they were introduced.
        await EnsureColumnAsync(connection, "MusicFile", "Genre", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "MusicFile", "GenreOverride", "TEXT NULL", cancellationToken);
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string definition, CancellationToken cancellationToken)
    {
        var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        await using (var reader = await check.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long?> StorePurchaseEvidenceAsync(UploadedDocument document, CancellationToken cancellationToken = default)
    {
        if (document.Content.Length == 0)
            return null;

        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO PurchaseEvidence (FileName, ContentType, Content, CreatedAtUtc) VALUES ($name, $type, $content, $created); SELECT last_insert_rowid();";
        var pars = command.Parameters;
        pars.AddWithValue("$name", Path.GetFileName(document.FileName));
        pars.AddWithValue("$type", (object?)document.ContentType ?? DBNull.Value);
        pars.AddWithValue("$content", document.Content);
        pars.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<long> IndexTrackAsync(string fullPath, long? purchaseEvidenceId, UploadedDocument? license, CancellationToken cancellationToken = default)
    {
        var checksum = await GetOrCreateChecksumAsync(fullPath, cancellationToken);
        var fileInfo = new FileInfo(fullPath);
        var relativePath = Path.GetRelativePath(options.MusicRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        var metadata = ReadMetadata(fullPath);
        var now = DateTime.UtcNow;

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = "SELECT Id FROM MusicFile WHERE Checksum = $checksum";
        find.Parameters.AddWithValue("$checksum", checksum);
        var existing = await find.ExecuteScalarAsync(cancellationToken);
        long id;
        if (existing is long value)
        {
            id = value;
            var storedPath = connection.CreateCommand();
            storedPath.Transaction = transaction;
            storedPath.CommandText = "SELECT RelativePath FROM MusicFile WHERE Id = $id";
            storedPath.Parameters.AddWithValue("$id", id);
            var currentPath = (string?)await storedPath.ExecuteScalarAsync(cancellationToken);
            // Identical copies share one row; keep the row on its original file so the copies do not steal it from each other.
            var isCopy = currentPath is not null && !string.Equals(currentPath, relativePath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(options.MusicRoot, currentPath));
            if (!isCopy)
            {
                var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE MusicFile SET RelativePath = $path, FileName = $name, Title = $title, MetadataJson = $metadata, FileSizeBytes = $size, IndexedAtUtc = $indexed, Genre = $genre WHERE Id = $id";
                AddTrackParameters(update, id, relativePath, fileInfo, metadata, now);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        else
        {
            var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO MusicFile (Checksum, ChecksumAlgorithm, RelativePath, FileName, Title, MetadataJson, FileSizeBytes, CreatedAtUtc, IndexedAtUtc, Genre) VALUES ($checksum, 'SHA-256', $path, $name, $title, $metadata, $size, $created, $indexed, $genre); SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$checksum", checksum);
            AddTrackParameters(insert, null, relativePath, fileInfo, metadata, now);
            insert.Parameters.AddWithValue("$created", fileInfo.CreationTimeUtc.ToString("O"));
            id = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }

        if (license is { Content.Length: > 0 })
        {
            var updateLicense = connection.CreateCommand();
            updateLicense.Transaction = transaction;
            updateLicense.CommandText = "UPDATE MusicFile SET LicenseFileName = $name, LicenseContentType = $type, LicenseContent = $content WHERE Id = $id";
            var pars = updateLicense.Parameters;
            pars.AddWithValue("$id", id);
            pars.AddWithValue("$name", Path.GetFileName(license.FileName));
            pars.AddWithValue("$type", (object?)license.ContentType ?? DBNull.Value);
            pars.AddWithValue("$content", license.Content);
            await updateLicense.ExecuteNonQueryAsync(cancellationToken);
        }

        if (purchaseEvidenceId is long evidenceId)
        {
            var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = "INSERT OR IGNORE INTO MusicFilePurchaseEvidence (MusicFileId, PurchaseEvidenceId) VALUES ($trackId, $evidenceId)";
            link.Parameters.AddWithValue("$trackId", id);
            link.Parameters.AddWithValue("$evidenceId", evidenceId);
            await link.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
        return id;
    }

    public async Task<long?> FindTrackIdByMusicUriAsync(string uri, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.AbsolutePath.StartsWith("/music/", StringComparison.OrdinalIgnoreCase))
            return null;

        var relativePath = string.Join('/', parsed.AbsolutePath[7..].Split('/').Select(Uri.UnescapeDataString));
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM MusicFile WHERE RelativePath = $path";
        command.Parameters.AddWithValue("$path", relativePath);
        return await command.ExecuteScalarAsync(cancellationToken) is long id ? id : null;
    }

    public async Task<long?> FindTrackIdByRelativePathAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM MusicFile WHERE RelativePath = $path";
        command.Parameters.AddWithValue("$path", relativePath);
        if (await command.ExecuteScalarAsync(cancellationToken) is long id)
            return id;

        // An identical copy shares the row of the original file via its checksum.
        var fullPath = Path.Combine(options.MusicRoot, relativePath);
        if (!File.Exists(fullPath))
            return null;
        var byChecksum = connection.CreateCommand();
        byChecksum.CommandText = "SELECT Id FROM MusicFile WHERE Checksum = $checksum";
        byChecksum.Parameters.AddWithValue("$checksum", await GetOrCreateChecksumAsync(fullPath, cancellationToken));
        return await byChecksum.ExecuteScalarAsync(cancellationToken) is long copyId ? copyId : null;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetEffectiveGenresAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT RelativePath, Genre, GenreOverride, Checksum FROM MusicFile";
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byChecksum = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var relativePath = reader.GetString(0);
                var genre = reader.IsDBNull(1) ? null : reader.GetString(1);
                var genreOverride = reader.IsDBNull(2) ? null : reader.GetString(2);
                result[relativePath] = byChecksum[reader.GetString(3)] = genreOverride ?? genre ?? "onbekend";
            }
        }

        // Identical copies of a track show the genre of the shared row.
        if (Directory.Exists(options.MusicRoot))
        {
            foreach (var sidecar in Directory.EnumerateFiles(options.MusicRoot, "*.checksum", SearchOption.AllDirectories))
            {
                var trackPath = Path.GetRelativePath(options.MusicRoot, sidecar[..^".checksum".Length]).Replace(Path.DirectorySeparatorChar, '/');
                if (!result.ContainsKey(trackPath) && byChecksum.TryGetValue((await File.ReadAllTextAsync(sidecar, cancellationToken)).Trim(), out var shared))
                    result[trackPath] = shared;
            }
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<string, TrackMetadata>> GetTrackMetadataAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT RelativePath, MetadataJson FROM MusicFile";
        var result = new Dictionary<string, TrackMetadata>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var metadata = reader.IsDBNull(1)
                ? null
                : JsonSerializer.Deserialize(reader.GetString(1), StreamingJsonContext.Default.TrackMetadata);
            result[reader.GetString(0)] = metadata ?? EmptyMetadata;
        }
        return result;
    }

    public async Task SetGenreOverrideAsync(long trackId, string? genre, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE MusicFile SET GenreOverride = $genre WHERE Id = $id";
        command.Parameters.AddWithValue("$genre", (object?)genre ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", trackId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordPlayAsync(long trackId, DateTime playedAtUtc, string playbackMode, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PlayAudit (MusicFileId, PlayedAtUtc, PlaybackMode)
            SELECT $trackId, $playedAt, $playbackMode
            WHERE NOT EXISTS (
                SELECT 1 FROM PlayAudit WHERE MusicFileId = $trackId AND PlayedAtUtc >= $deduplicationStart
            );
            """;
        var pars = command.Parameters;
        pars.AddWithValue("$trackId", trackId);
        pars.AddWithValue("$playedAt", playedAtUtc.ToString("O"));
        pars.AddWithValue("$playbackMode", playbackMode);
        pars.AddWithValue("$deduplicationStart", playedAtUtc.AddMinutes(-1).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RebuildMonthlySummaryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = "DELETE FROM PlayAuditMonthly";
        await clear.ExecuteNonQueryAsync(cancellationToken);
        var aggregate = connection.CreateCommand();
        aggregate.Transaction = transaction;
        aggregate.CommandText = "INSERT INTO PlayAuditMonthly (MusicFileId, MonthUtc, PlayCount, CalculatedAtUtc) SELECT MusicFileId, substr(PlayedAtUtc, 1, 7), COUNT(*), $calculated FROM PlayAudit GROUP BY MusicFileId, substr(PlayedAtUtc, 1, 7)";
        aggregate.Parameters.AddWithValue("$calculated", DateTime.UtcNow.ToString("O"));
        await aggregate.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    public async Task<IReadOnlyList<PlayAuditReportItem>> GetPlayAuditReportAsync(CancellationToken cancellationToken = default)
    {
        var plays = new Dictionary<long, (string Title, List<DateTime> Dates, List<PlayAuditMonth> Months)>();
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT f.Id, f.Title, a.PlayedAtUtc FROM MusicFile f JOIN PlayAudit a ON a.MusicFileId = f.Id ORDER BY f.Title, a.PlayedAtUtc";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                if (!plays.TryGetValue(id, out var item))
                    item = (reader.GetString(1), [], []);
                item.Dates.Add(DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind));
                plays[id] = item;
            }
        }

        var months = connection.CreateCommand();
        months.CommandText = "SELECT f.Id, m.MonthUtc, m.PlayCount FROM MusicFile f JOIN PlayAuditMonthly m ON m.MusicFileId = f.Id ORDER BY f.Title, m.MonthUtc";
        await using (var reader = await months.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                if (!plays.TryGetValue(id, out var item))
                    continue;
                item.Months.Add(new PlayAuditMonth(reader.GetString(1), reader.GetInt32(2)));
                plays[id] = item;
            }
        }

        return [.. plays.Values.Select(item => new PlayAuditReportItem(item.Title, item.Dates, item.Months))];
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
        string _connectionString=new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, ForeignKeys = true }.ToString();
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void AddTrackParameters(SqliteCommand command, long? id, string relativePath, FileInfo fileInfo, (string Title, string MetadataJson, string? Genre) metadata, DateTime indexedAt)
    {
        var pars = command.Parameters;
        if (id is long value)
            pars.AddWithValue("$id", value);
        pars.AddWithValue("$path", relativePath);
        pars.AddWithValue("$name", fileInfo.Name);
        pars.AddWithValue("$title", metadata.Title);
        pars.AddWithValue("$metadata", metadata.MetadataJson);
        pars.AddWithValue("$size", fileInfo.Length);
        pars.AddWithValue("$indexed", indexedAt.ToString("O"));
        pars.AddWithValue("$genre", (object?)metadata.Genre ?? DBNull.Value);
    }

    private static (string Title, string MetadataJson, string? Genre) ReadMetadata(string fullPath)
    {
        try
        {
            using var file = TagLib.File.Create(fullPath);
            var title = string.IsNullOrWhiteSpace(file.Tag.Title) ? Path.GetFileNameWithoutExtension(fullPath) : file.Tag.Title.Trim();
            var performers = string.Join(", ", file.Tag.Performers);
            var album = file.Tag.Album ?? string.Empty;
            var genre = MusicReportService.FirstTagValue(file.Tag.Genres, file.Tag.FirstGenre);
            var trackMetadata = new TrackMetadata(
                performers,
                album,
                file.Tag.Year,
                string.Join(", ", file.Tag.Genres),
                file.Tag.Copyright ?? string.Empty,
                file.Tag.Publisher ?? string.Empty,
                file.Tag.ISRC ?? string.Empty,
                ReadTextField(file.Tag, "Mood"),
                ReadIntegerField(file.Tag, "Energy"),
                ReadIntegerField(file.Tag, "Tempo", "TempoBPM", "BPM"),
                ReadBooleanField(file.Tag, "Instrumental"));
            var metadata = JsonSerializer.Serialize(trackMetadata, StreamingJsonContext.Default.TrackMetadata);
            return (title, metadata, genre);
        }
        catch (Exception)
        {
            return (Path.GetFileNameWithoutExtension(fullPath), JsonSerializer.Serialize(EmptyMetadata, StreamingJsonContext.Default.TrackMetadata), null);
        }
    }

    private static readonly TrackMetadata EmptyMetadata = new("", "", 0, "", "", "", "", null, null, null, null);

    private static string? ReadTextField(TagLib.Tag tag, params string[] names)
    {
        foreach (var name in names)
        {
            var values = tag switch
            {
                TagLib.Ogg.XiphComment xiph => xiph.GetField(name),
                TagLib.Id3v2.Tag id3 => TagLib.Id3v2.UserTextInformationFrame.Get(id3, name, false)?.Text ?? [],
                _ => []
            };
            var value = values.Select(item => item.Trim()).FirstOrDefault(item => item.Length > 0);
            if (value is not null)
                return value;
        }

        return null;
    }

    private static int? ReadIntegerField(TagLib.Tag tag, params string[] names)
    {
        var text = ReadTextField(tag, names);
        var number = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static bool? ReadBooleanField(TagLib.Tag tag, params string[] names)
    {
        var value = ReadTextField(tag, names);
        if (value is null)
            return null;
        if (bool.TryParse(value, out var boolean))
            return boolean;
        return value switch
        {
            "1" or "yes" or "y" => true,
            "0" or "no" or "n" => false,
            _ => null
        };
    }

    internal static string EscapeJson(string? value) =>
        (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

    private static async Task<string> GetOrCreateChecksumAsync(string fullPath, CancellationToken cancellationToken)
    {
        var cachePath = fullPath + ".checksum";
        if (File.Exists(cachePath))
        {
            var cached = await File.ReadAllTextAsync(cachePath, cancellationToken);
            if (cached.Length == 64 && cached.All(Uri.IsHexDigit))
                return cached;
        }

        await using var stream = File.OpenRead(fullPath);
        var checksum = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        await File.WriteAllTextAsync(cachePath, checksum, cancellationToken);
        return checksum;
    }
}
