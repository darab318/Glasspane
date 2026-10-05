using System;
using System.Collections.Generic;
using System.IO;
using Glasspane.Core;
using Microsoft.Data.Sqlite;

namespace Glasspane.Widgets.Clipboard
{
    /// <summary>
    /// Clipboard history on disk: a SQLite database for text and details, with images
    /// saved as PNG files next to it. Survives restarts and has no practical size limit.
    /// </summary>
    public sealed class ClipboardStore : IDisposable
    {
        private readonly SqliteConnection _db;

        public ClipboardStore(string folder)
        {
            Directory.CreateDirectory(folder);
            ImageFolder = Path.Combine(folder, "images");
            Directory.CreateDirectory(ImageFolder);

            _db = new SqliteConnection($"Data Source={Path.Combine(folder, "clipboard.db")}");
            _db.Open();
            Exec("PRAGMA journal_mode=WAL;");
            // Safe with WAL, and avoids forcing a disk flush every time you copy something
            Exec("PRAGMA synchronous=NORMAL;");
            // Small page cache (1 MB); history lookups are simple and fast anyway
            Exec("PRAGMA cache_size=-1024;");
            Exec(@"
                CREATE TABLE IF NOT EXISTS entries (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    kind          INTEGER NOT NULL,
                    text          TEXT,
                    html          TEXT,
                    rtf           TEXT,
                    files         TEXT,
                    image_file    TEXT,
                    width         INTEGER NOT NULL DEFAULT 0,
                    height        INTEGER NOT NULL DEFAULT 0,
                    hash          TEXT NOT NULL UNIQUE,
                    source_app    TEXT,
                    created_utc   INTEGER NOT NULL,
                    last_used_utc INTEGER NOT NULL,
                    pinned        INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS ix_entries_last_used ON entries(last_used_utc DESC);");
        }

        public string ImageFolder { get; }

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private static DateTime FromUnix(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

        /// <summary>Saves a new item, or if the exact same content exists, moves that one to the top.</summary>
        public long Upsert(CapturedItem item, out bool isNew)
        {
            long now = Now();

            using (var find = Cmd("SELECT id FROM entries WHERE hash = $hash"))
            {
                find.Parameters.AddWithValue("$hash", item.Hash);
                if (find.ExecuteScalar() is long existing)
                {
                    using var touch = Cmd("UPDATE entries SET last_used_utc = $now, source_app = COALESCE($app, source_app) WHERE id = $id");
                    touch.Parameters.AddWithValue("$now", now);
                    touch.Parameters.AddWithValue("$app", (object?)item.SourceApp ?? DBNull.Value);
                    touch.Parameters.AddWithValue("$id", existing);
                    touch.ExecuteNonQuery();
                    isNew = false;
                    return existing;
                }
            }

            string? imageFile = null;
            if (item.Kind == ClipKind.Image && item.Png != null)
            {
                imageFile = item.Hash.Substring(0, 24) + ".png";
                File.WriteAllBytes(Path.Combine(ImageFolder, imageFile), item.Png);
            }

            using var insert = Cmd(@"
                INSERT INTO entries (kind, text, html, rtf, files, image_file, width, height, hash, source_app, created_utc, last_used_utc)
                VALUES ($kind, $text, $html, $rtf, $files, $image, $w, $h, $hash, $app, $now, $now);
                SELECT last_insert_rowid();");
            insert.Parameters.AddWithValue("$kind", (int)item.Kind);
            insert.Parameters.AddWithValue("$text", (object?)item.Text ?? DBNull.Value);
            insert.Parameters.AddWithValue("$html", (object?)item.Html ?? DBNull.Value);
            insert.Parameters.AddWithValue("$rtf", (object?)item.Rtf ?? DBNull.Value);
            insert.Parameters.AddWithValue("$files", item.Files != null ? (object)string.Join("\n", item.Files) : DBNull.Value);
            insert.Parameters.AddWithValue("$image", (object?)imageFile ?? DBNull.Value);
            insert.Parameters.AddWithValue("$w", item.Width);
            insert.Parameters.AddWithValue("$h", item.Height);
            insert.Parameters.AddWithValue("$hash", item.Hash);
            insert.Parameters.AddWithValue("$app", (object?)item.SourceApp ?? DBNull.Value);
            insert.Parameters.AddWithValue("$now", now);
            isNew = true;
            return (long)insert.ExecuteScalar()!;
        }

        private const string ListColumns =
            "id, kind, substr(text, 1, 600), length(text), files, image_file, width, height, source_app, created_utc, last_used_utc, pinned, hash";

        public List<ClipEntry> Query(string? search, ClipFilter filter, int limit, int offset)
        {
            var where = new List<string>();
            switch (filter)
            {
                case ClipFilter.Text: where.Add("kind = 0"); break;
                case ClipFilter.Images: where.Add("kind = 1"); break;
                case ClipFilter.Files: where.Add("kind = 2"); break;
                case ClipFilter.Pinned: where.Add("pinned = 1"); break;
            }
            if (!string.IsNullOrWhiteSpace(search))
                where.Add(@"(text LIKE $q ESCAPE '\' OR files LIKE $q ESCAPE '\' OR source_app LIKE $q ESCAPE '\')");

            string sql = $"SELECT {ListColumns} FROM entries" +
                         (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                         " ORDER BY last_used_utc DESC LIMIT $limit OFFSET $offset";

            using var cmd = Cmd(sql);
            if (!string.IsNullOrWhiteSpace(search))
            {
                string escaped = search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
                cmd.Parameters.AddWithValue("$q", "%" + escaped + "%");
            }
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$offset", offset);

            var list = new List<ClipEntry>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadEntry(r));
            return list;
        }

        public ClipEntry? Get(long id)
        {
            using var cmd = Cmd($"SELECT {ListColumns} FROM entries WHERE id = $id");
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadEntry(r) : null;
        }

        private ClipEntry ReadEntry(SqliteDataReader r)
        {
            string preview = r.IsDBNull(2) ? "" : r.GetString(2);
            preview = preview.Replace("\t", "    ").Trim('\r', '\n');
            string? image = r.IsDBNull(5) ? null : r.GetString(5);

            return new ClipEntry
            {
                Id = r.GetInt64(0),
                Kind = (ClipKind)r.GetInt32(1),
                Preview = preview,
                CharCount = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                Files = r.IsDBNull(4) ? Array.Empty<string>() : r.GetString(4).Split('\n', StringSplitOptions.RemoveEmptyEntries),
                ImagePath = image == null ? null : Path.Combine(ImageFolder, image),
                Width = r.GetInt32(6),
                Height = r.GetInt32(7),
                SourceApp = r.IsDBNull(8) ? null : r.GetString(8),
                CreatedUtc = FromUnix(r.GetInt64(9)),
                LastUsedUtc = FromUnix(r.GetInt64(10)),
                Pinned = r.GetInt64(11) != 0,
                Hash = r.GetString(12)
            };
        }

        public (string? text, string? html, string? rtf) GetFullText(long id)
        {
            using var cmd = Cmd("SELECT text, html, rtf FROM entries WHERE id = $id");
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return (null, null, null);
            return (r.IsDBNull(0) ? null : r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2));
        }

        public DateTime Touch(long id)
        {
            long now = Now();
            using var cmd = Cmd("UPDATE entries SET last_used_utc = $now WHERE id = $id");
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            return FromUnix(now);
        }

        public void SetPinned(long id, bool pinned)
        {
            using var cmd = Cmd("UPDATE entries SET pinned = $p WHERE id = $id");
            cmd.Parameters.AddWithValue("$p", pinned ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        public void Delete(long id)
        {
            DeleteImagesWhere("id = $id", c => c.Parameters.AddWithValue("$id", id));
            using var cmd = Cmd("DELETE FROM entries WHERE id = $id");
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        /// <summary>Clears everything except pinned items.</summary>
        public int ClearUnpinned()
        {
            DeleteImagesWhere("pinned = 0", null);
            using var cmd = Cmd("DELETE FROM entries WHERE pinned = 0");
            int n = cmd.ExecuteNonQuery();
            Exec("PRAGMA wal_checkpoint(TRUNCATE);");
            return n;
        }

        /// <summary>Keeps the newest <paramref name="max"/> unpinned items.</summary>
        public void Trim(int max)
        {
            const string condition = "pinned = 0 AND id NOT IN (SELECT id FROM entries WHERE pinned = 0 ORDER BY last_used_utc DESC LIMIT $max)";
            DeleteImagesWhere(condition, c => c.Parameters.AddWithValue("$max", max));
            using var cmd = Cmd("DELETE FROM entries WHERE " + condition);
            cmd.Parameters.AddWithValue("$max", max);
            cmd.ExecuteNonQuery();
        }

        public int Count()
        {
            using var cmd = Cmd("SELECT COUNT(*) FROM entries");
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private void DeleteImagesWhere(string condition, Action<SqliteCommand>? bind)
        {
            using var cmd = Cmd("SELECT image_file FROM entries WHERE image_file IS NOT NULL AND " + condition);
            bind?.Invoke(cmd);
            var files = new List<string>();
            using (var r = cmd.ExecuteReader())
                while (r.Read()) files.Add(r.GetString(0));

            foreach (var f in files)
            {
                try { File.Delete(Path.Combine(ImageFolder, f)); }
                catch (Exception ex) { Log.Write($"Could not delete {f}: {ex.Message}"); }
            }
        }

        private SqliteCommand Cmd(string sql)
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            return cmd;
        }

        private void Exec(string sql)
        {
            using var cmd = Cmd(sql);
            cmd.ExecuteNonQuery();
        }

        public void Dispose() => _db.Dispose();
    }
}
