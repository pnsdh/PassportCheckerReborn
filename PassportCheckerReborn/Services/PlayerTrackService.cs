using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Lumina.Excel.Sheets;

namespace PassportCheckerReborn.Services;

/// <summary>A previous name/world a player was seen under, per the PlayerTrack DB.</summary>
public sealed record PlayerTrackNameHistory(string Name, ushort WorldId, string WorldName, DateTime Changed);

/// <summary>A resolved player record read from the PlayerTrack database.</summary>
public sealed record PlayerTrackRecord(
    ulong ContentId,
    string Name,
    ushort WorldId,
    string WorldName,
    DateTime LastSeen,
    IReadOnlyList<PlayerTrackNameHistory> PreviousNames);

/// <summary>
/// Read-only integration with the <see href="https://github.com/Infiziert90/PlayerTrack">PlayerTrack</see>
/// plugin's SQLite database (<c>pluginConfigs/PlayerTrack/data.db</c>). PlayerTrack passively records
/// every player the user encounters (proximity/party/etc) as <c>content_id → name/world</c>, which lets
/// us resolve PF members whose adventure plate is hidden — as long as they were seen before.
///
/// <para>
/// The ContentId keyspace was verified to be identical to the one PassportCheckerReborn uses.
/// </para>
///
/// <para>
/// Access is strictly read-only via the Windows-bundled <c>winsqlite3.dll</c> (no NuGet/native asset is
/// added to the plugin — the project's DLL-prune target would strip such assets anyway). The database is
/// opened read-only and closed immediately per lookup; PlayerTrack's own WAL writer is never blocked.
/// Every failure path degrades gracefully to "no record".
/// </para>
/// </summary>
public sealed class PlayerTrackService : IDisposable
{
    private const string PlayerTrackInternalName = "PlayerTrack";

    private readonly string dbPath;
    private bool loggedOpenFailure;

    public PlayerTrackService()
    {
        // PCR config dir is .../pluginConfigs/PassportCheckerReborn — PlayerTrack sits alongside it.
        var configDir = PassportCheckerReborn.PluginInterface.GetPluginConfigDirectory();
        dbPath = Path.GetFullPath(Path.Combine(configDir, "..", PlayerTrackInternalName, "data.db"));
    }

    /// <summary>Full path to the PlayerTrack database this service reads from.</summary>
    public string DatabasePath => dbPath;

    /// <summary>Whether the PlayerTrack database file exists (required for any lookup).</summary>
    public bool DatabaseExists => File.Exists(dbPath);

    /// <summary>Whether lookups can actually be performed right now.</summary>
    public bool IsUsable => DatabaseExists;

    /// <summary>
    /// Reports whether the PlayerTrack plugin is installed and currently loaded, for UI status.
    /// </summary>
    public (bool Installed, bool Loaded) GetPluginStatus()
    {
        try
        {
            foreach (var p in PassportCheckerReborn.PluginInterface.InstalledPlugins)
            {
                if (string.Equals(p.InternalName, PlayerTrackInternalName, StringComparison.OrdinalIgnoreCase))
                {
                    return (true, p.IsLoaded);
                }
            }
        }
        catch
        {
            // InstalledPlugins can throw during teardown; treat as unknown.
        }

        return (false, false);
    }

    /// <summary>
    /// Resolves a single ContentId to a PlayerTrack record (name/world + last-seen + previous names),
    /// or <c>null</c> if there is no usable entry (or the database can't be read). Never throws.
    /// </summary>
    public PlayerTrackRecord? Resolve(ulong contentId)
    {
        if (contentId == 0 || !DatabaseExists)
        {
            return null;
        }

        var db = IntPtr.Zero;
        try
        {
            // Read-only open. winsqlite3 handles WAL as long as PlayerTrack keeps the -shm live.
            var pathBytes = Encoding.UTF8.GetBytes(dbPath + "\0");
            var rc = Native.sqlite3_open_v2(pathBytes, out db, Native.SQLITE_OPEN_READONLY, IntPtr.Zero);
            if (rc != Native.SQLITE_OK || db == IntPtr.Zero)
            {
                LogOpenFailureOnce(rc);
                return null;
            }

            if (!TryReadPlayer(db, contentId, out var playerId, out var name, out var worldId, out var lastSeen))
            {
                return null;
            }

            var history = ReadNameHistory(db, playerId, name);
            var worldName = WorldName(worldId);
            return new PlayerTrackRecord(contentId, name, worldId, worldName, lastSeen, history);
        }
        catch (Exception ex)
        {
            LogOpenFailureOnce(-1, ex);
            return null;
        }
        finally
        {
            if (db != IntPtr.Zero)
            {
                Native.sqlite3_close_v2(db);
            }
        }
    }

    private static bool TryReadPlayer(
        IntPtr db, ulong contentId, out long playerId, out string name, out ushort worldId, out DateTime lastSeen)
    {
        playerId = 0;
        name = string.Empty;
        worldId = 0;
        lastSeen = DateTime.MinValue;

        var sql = Encoding.UTF8.GetBytes(
            "SELECT id, name, world_id, last_seen FROM players " +
            "WHERE content_id = ? AND name IS NOT NULL AND name <> '' LIMIT 1\0");

        var stmt = IntPtr.Zero;
        try
        {
            if (Native.sqlite3_prepare_v2(db, sql, -1, out stmt, IntPtr.Zero) != Native.SQLITE_OK || stmt == IntPtr.Zero)
            {
                return false;
            }

            // ContentIds are ~0x0040_0000_xxxx_xxxx, well within positive Int64 range.
            Native.sqlite3_bind_int64(stmt, 1, unchecked((long)contentId));

            if (Native.sqlite3_step(stmt) != Native.SQLITE_ROW)
            {
                return false;
            }

            playerId = Native.sqlite3_column_int64(stmt, 0);
            name = ColumnText(stmt, 1);
            worldId = (ushort)Native.sqlite3_column_int64(stmt, 2);
            lastSeen = FromUnixMillis(Native.sqlite3_column_int64(stmt, 3));
            return !string.IsNullOrEmpty(name);
        }
        finally
        {
            if (stmt != IntPtr.Zero)
            {
                Native.sqlite3_finalize(stmt);
            }
        }
    }

    private static IReadOnlyList<PlayerTrackNameHistory> ReadNameHistory(IntPtr db, long playerId, string currentName)
    {
        var result = new List<PlayerTrackNameHistory>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var sql = Encoding.UTF8.GetBytes(
            "SELECT player_name, world_id, created FROM player_name_world_histories " +
            "WHERE player_id = ? ORDER BY created DESC\0");

        var stmt = IntPtr.Zero;
        try
        {
            if (Native.sqlite3_prepare_v2(db, sql, -1, out stmt, IntPtr.Zero) != Native.SQLITE_OK || stmt == IntPtr.Zero)
            {
                return result;
            }

            Native.sqlite3_bind_int64(stmt, 1, playerId);

            while (Native.sqlite3_step(stmt) == Native.SQLITE_ROW)
            {
                var histName = ColumnText(stmt, 0);
                if (string.IsNullOrEmpty(histName) || string.Equals(histName, currentName, StringComparison.Ordinal))
                {
                    continue; // skip the current name — we only want *previous* nicknames
                }

                if (!seen.Add(histName))
                {
                    continue; // dedupe repeated old names
                }

                var histWorld = (ushort)Native.sqlite3_column_int64(stmt, 1);
                var changed = FromUnixMillis(Native.sqlite3_column_int64(stmt, 2));
                result.Add(new PlayerTrackNameHistory(histName, histWorld, WorldName(histWorld), changed));

                if (result.Count >= 10)
                {
                    break;
                }
            }
        }
        finally
        {
            if (stmt != IntPtr.Zero)
            {
                Native.sqlite3_finalize(stmt);
            }
        }

        return result;
    }

    private static string ColumnText(IntPtr stmt, int col)
    {
        var ptr = Native.sqlite3_column_text(stmt, col);
        if (ptr == IntPtr.Zero)
        {
            return string.Empty;
        }

        // column_bytes must be read after column_text so it reflects the UTF-8 length.
        var len = Native.sqlite3_column_bytes(stmt, col);
        if (len <= 0)
        {
            return string.Empty;
        }

        var buffer = new byte[len];
        Marshal.Copy(ptr, buffer, 0, len);
        return Encoding.UTF8.GetString(buffer);
    }

    private static DateTime FromUnixMillis(long ms)
        => ms <= 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    private static string WorldName(ushort worldId)
    {
        if (worldId == 0)
        {
            return string.Empty;
        }

        try
        {
            return PassportCheckerReborn.DataManager.GetExcelSheet<World>()
                ?.GetRowOrDefault(worldId)?.Name.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void LogOpenFailureOnce(int rc, Exception? ex = null)
    {
        if (loggedOpenFailure)
        {
            return;
        }

        loggedOpenFailure = true;
        if (ex != null)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PlayerTrack] Failed to read database (further errors suppressed).");
        }
        else
        {
            PassportCheckerReborn.Log.Warning(
                $"[PlayerTrack] Failed to open database (sqlite rc={rc}); integration disabled for now (further errors suppressed).");
        }
    }

    public void Dispose()
    {
        // No persistent handle is held; connections are opened and closed per lookup.
        GC.SuppressFinalize(this);
    }

    // ── winsqlite3.dll interop (Windows-bundled SQLite; present on Win10 1703+) ──
    private static class Native
    {
        private const string Dll = "winsqlite3.dll";

        public const int SQLITE_OK = 0;
        public const int SQLITE_ROW = 100;
        public const int SQLITE_OPEN_READONLY = 0x00000001;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_close_v2(IntPtr db);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr pzTail);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_step(IntPtr stmt);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_column_bytes(IntPtr stmt, int col);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sqlite3_column_int64(IntPtr stmt, int col);
    }
}
