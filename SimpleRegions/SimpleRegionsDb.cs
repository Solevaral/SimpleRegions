using System;
using System.Collections.Generic;
using System.Data;
using TShockAPI;
using TShockAPI.DB;
using TShockAPI.DB.Queries;

namespace SimpleRegions
{
    /// <summary>Plugin-owned metadata for one claim. Keyed by region name + world, never by id.</summary>
    public class ClaimMeta
    {
        public string RegionName;
        public string WorldId;
        public string Owner;
        public DateTime CreatedUtc;
    }

    /// <summary>
    /// Stores which TShock regions belong to this plugin, and who owns them.
    ///
    /// Data-safety rules enforced here (claims must survive plugin updates):
    ///  * only CREATE TABLE IF NOT EXISTS — never DROP or recreate;
    ///  * a schema version row drives future migrations via ALTER TABLE;
    ///  * rows are keyed by (RegionName, WorldId), not by a region id that changes whenever
    ///    a region is recreated;
    ///  * a startup consistency check only WARNS about drift, it never deletes anything.
    ///
    /// This lives in tshock.sqlite deliberately: claim operations are a handful per day, so
    /// the write-lock contention that matters for high-volume logging is a non-issue here.
    /// </summary>
    public class SimpleRegionsDb
    {
        private const string MetaTable = "SimpleRegionsClaims";
        private const string SchemaTable = "SimpleRegionsSchema";
        private const int CurrentSchemaVersion = 1;

        private readonly IDbConnection _db;

        public SimpleRegionsDb(IDbConnection db)
        {
            _db = db;

            IQueryBuilder builder;
            switch (_db.GetSqlType())
            {
                case SqlType.Sqlite: builder = new SqliteQueryBuilder(); break;
                case SqlType.Postgres: builder = new PostgresQueryBuilder(); break;
                default: builder = new MysqlQueryBuilder(); break;
            }

            var creator = new SqlTableCreator(_db, builder);

            // EnsureTableStructure only ADDS missing columns; it never drops existing data.
            creator.EnsureTableStructure(new SqlTable(SchemaTable,
                new SqlColumn("Id", MySql.Data.MySqlClient.MySqlDbType.Int32) { Primary = true },
                new SqlColumn("Version", MySql.Data.MySqlClient.MySqlDbType.Int32)));

            creator.EnsureTableStructure(new SqlTable(MetaTable,
                new SqlColumn("RegionName", MySql.Data.MySqlClient.MySqlDbType.VarChar, 100),
                new SqlColumn("WorldId", MySql.Data.MySqlClient.MySqlDbType.VarChar, 50),
                new SqlColumn("Owner", MySql.Data.MySqlClient.MySqlDbType.VarChar, 100),
                new SqlColumn("CreatedUtc", MySql.Data.MySqlClient.MySqlDbType.Text)));

            MigrateSchema();
        }

        /// <summary>
        /// Applies any pending schema migrations. Version 1 is the initial layout; future
        /// versions must ALTER the existing table and bump the number, never recreate it.
        /// </summary>
        private void MigrateSchema()
        {
            var version = GetSchemaVersion();

            if (version == 0)
            {
                // Fresh install (or a pre-versioning install — same layout either way).
                SetSchemaVersion(CurrentSchemaVersion);
                TShock.Log.ConsoleInfo("[SimpleRegions] Схема БД инициализирована, версия " + CurrentSchemaVersion + ".");
                return;
            }

            if (version > CurrentSchemaVersion)
            {
                TShock.Log.ConsoleError(
                    "[SimpleRegions] В базе схема версии " + version + ", а плагин рассчитан на " + CurrentSchemaVersion +
                    ". Похоже, установлена более старая версия плагина поверх новой — данные не тронуты, " +
                    "но возможны ошибки. Обновите плагин.");
                return;
            }

            // if (version < 2) { ALTER TABLE ...; SetSchemaVersion(2); }  <- future migrations go here

            if (version < CurrentSchemaVersion)
                SetSchemaVersion(CurrentSchemaVersion);
        }

        private int GetSchemaVersion()
        {
            try
            {
                using var reader = _db.QueryReader("SELECT Version FROM " + SchemaTable + " WHERE Id = 1");
                return reader.Read() ? reader.Get<int>("Version") : 0;
            }
            catch
            {
                return 0;
            }
        }

        private void SetSchemaVersion(int version)
        {
            _db.Query("DELETE FROM " + SchemaTable + " WHERE Id = 1");
            _db.Query("INSERT INTO " + SchemaTable + " (Id, Version) VALUES (1, @0)", version);
        }

        public void AddClaim(string regionName, string worldId, string owner)
        {
            _db.Query(
                "INSERT INTO " + MetaTable + " (RegionName, WorldId, Owner, CreatedUtc) VALUES (@0, @1, @2, @3)",
                regionName, worldId, owner, DateTime.UtcNow.Ticks.ToString());
        }

        public void RemoveClaim(string regionName, string worldId)
        {
            _db.Query("DELETE FROM " + MetaTable + " WHERE RegionName = @0 AND WorldId = @1", regionName, worldId);
        }

        public List<ClaimMeta> GetClaims(string worldId)
        {
            var list = new List<ClaimMeta>();
            using var reader = _db.QueryReader(
                "SELECT RegionName, WorldId, Owner, CreatedUtc FROM " + MetaTable + " WHERE WorldId = @0", worldId);
            while (reader.Read())
                list.Add(Read(reader));
            return list;
        }

        public ClaimMeta GetClaim(string regionName, string worldId)
        {
            using var reader = _db.QueryReader(
                "SELECT RegionName, WorldId, Owner, CreatedUtc FROM " + MetaTable +
                " WHERE RegionName = @0 AND WorldId = @1", regionName, worldId);
            return reader.Read() ? Read(reader) : null;
        }

        private static ClaimMeta Read(QueryResult reader)
        {
            var raw = reader.Get<string>("CreatedUtc");
            var created = long.TryParse(raw, out var ticks) && ticks > 0 && ticks <= DateTime.MaxValue.Ticks
                ? new DateTime(ticks, DateTimeKind.Utc)
                : DateTime.UtcNow;

            return new ClaimMeta
            {
                RegionName = reader.Get<string>("RegionName"),
                WorldId = reader.Get<string>("WorldId"),
                Owner = reader.Get<string>("Owner"),
                CreatedUtc = created
            };
        }

        /// <summary>
        /// Reports drift between plugin metadata and TShock's own region list. Deliberately
        /// read-only: a region missing on one side is far more likely to be an admin action
        /// (or a botched restore) than garbage, and silently deleting either side would
        /// destroy player claims. Warn loudly, change nothing.
        /// </summary>
        public void ReportIntegrity(string worldId, ICollection<string> tshockRegionNames)
        {
            try
            {
                var claims = GetClaims(worldId);
                var orphanedMeta = new List<string>();

                foreach (var claim in claims)
                    if (!tshockRegionNames.Contains(claim.RegionName))
                        orphanedMeta.Add(claim.RegionName);

                if (orphanedMeta.Count > 0)
                {
                    TShock.Log.ConsoleError(
                        "[SimpleRegions] ВНИМАНИЕ: в базе плагина есть приваты, которых больше нет в списке регионов TShock (" +
                        orphanedMeta.Count + "): " + string.Join(", ", orphanedMeta) +
                        ". Записи НЕ удалены. Если регионы удалили намеренно — уберите их через /rg delete от имени владельца " +
                        "или вручную из таблицы " + MetaTable + ".");
                }

                TShock.Log.ConsoleInfo("[SimpleRegions] Приватов в базе для этого мира: " + claims.Count + ".");
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError("[SimpleRegions] Ошибка проверки целостности: " + ex.Message);
            }
        }
    }
}
