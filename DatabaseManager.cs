using System;
using Microsoft.Data.Sqlite;

namespace SMusicBoxAutoScheduler
{
    public static class DatabaseManager
    {
        public static void InitializeDatabase()
        {
            AppPaths.EnsureDirectories();

            using var connection = new SqliteConnection(
                $"Data Source={AppPaths.Database}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS upload_queue (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_path TEXT NOT NULL,
                    title TEXT NOT NULL,
                    description TEXT,
                    thumbnail_path TEXT,
                    scheduled_time TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'pending',
                    youtube_id TEXT,
                    error_message TEXT,
                    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                    retry_count INTEGER NOT NULL DEFAULT 0,
                    next_attempt_utc TEXT,
                    processing_started_utc TEXT,
                    upload_started_utc TEXT
                );
            ";
            command.ExecuteNonQuery();

            EnsureColumn(connection, "retry_count",
                "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "error_message", "TEXT");
            EnsureColumn(connection, "next_attempt_utc", "TEXT");
            EnsureColumn(connection, "processing_started_utc", "TEXT");
            EnsureColumn(connection, "upload_started_utc", "TEXT");
        }

        private static void EnsureColumn(
            SqliteConnection connection,
            string column,
            string definition)
        {
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA table_info(upload_queue);";

            using var reader = check.ExecuteReader();

            while (reader.Read())
            {
                if (reader.GetString(1).Equals(
                    column, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            reader.Close();

            using var alter = connection.CreateCommand();
            alter.CommandText =
                $"ALTER TABLE upload_queue ADD COLUMN {column} {definition};";
            alter.ExecuteNonQuery();
        }
    }
}
