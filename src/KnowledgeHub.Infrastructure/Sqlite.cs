using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace KnowledgeHub.Infrastructure;
/// <summary>Parameterized adapter over the SQLite C API. Each operation owns its connection.</summary>
public sealed class Sqlite : IDisposable
{
    private IntPtr db;
    static Sqlite() => NativeLibrary.SetDllImportResolver(typeof(Sqlite).Assembly, Resolve);
    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != "knowledgehub_sqlite")
            return IntPtr.Zero;
        string[] names = OperatingSystem.IsWindows() ? ["winsqlite3.dll", "sqlite3.dll"] : OperatingSystem.IsMacOS() ? ["/usr/lib/libsqlite3.dylib", "libsqlite3.dylib"] : ["libsqlite3.so.0", "libsqlite3.so"];
        foreach (var candidate in names)
            if (NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        throw new DllNotFoundException("SQLite is required. On Ubuntu: sudo apt install libsqlite3-0");
    }

    public Sqlite(string file)
    {
        var rc = Native.sqlite3_open_v2(file, out db, 2 | 4 | 0x10000, IntPtr.Zero);
        if (rc != 0)
        {
            var message = Error;
            Dispose();
            throw new InvalidOperationException(message);
        }

        Native.sqlite3_busy_timeout(db, 5000);
        Exec("PRAGMA foreign_keys=ON");
    }

    private string Error => Marshal.PtrToStringUTF8(Native.sqlite3_errmsg(db)) ?? "SQLite error";
    public int Changes => Native.sqlite3_changes(db);
    public long LastId => Native.sqlite3_last_insert_rowid(db);

    public void Exec(string sql, params object? [] args) => Query(sql, args);
    public List<Row> Query(string sql, params object? [] args)
    {
        if (Native.sqlite3_prepare_v2(db, sql, -1, out var stmt, IntPtr.Zero) != 0)
            throw new InvalidOperationException(Error);
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                int code;
                var value = args[i];
                if (value is null)
                    code = Native.sqlite3_bind_null(stmt, i + 1);
                else if (value is int or long or bool or Enum)
                    code = Native.sqlite3_bind_int64(stmt, i + 1, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                else
                    code = Native.sqlite3_bind_text(stmt, i + 1, value is DateTime date ? date.ToUniversalTime().ToString("O") : Convert.ToString(value, CultureInfo.InvariantCulture)!, -1, new IntPtr(-1));
                if (code != 0)
                    throw new InvalidOperationException(Error);
            }

            var rows = new List<Row>();
            int result;
            while ((result = Native.sqlite3_step(stmt)) == 100)
            {
                var row = new Row();
                for (var i = 0; i < Native.sqlite3_column_count(stmt); i++)
                {
                    var name = Marshal.PtrToStringUTF8(Native.sqlite3_column_name(stmt, i))!;
                    row[name] = Native.sqlite3_column_type(stmt, i) == 5 ? null : Marshal.PtrToStringUTF8(Native.sqlite3_column_text(stmt, i));
                }

                rows.Add(row);
            }

            if (result != 101)
                throw new InvalidOperationException(Error);
            return rows;
        }
        finally
        {
            Native.sqlite3_finalize(stmt);
        }
    }

    public int Scalar(string sql, params object? [] args) => int.Parse(Query(sql, args)[0].Values.First()!, CultureInfo.InvariantCulture);
    public void Dispose()
    {
        if (db != IntPtr.Zero)
        {
            Native.sqlite3_close_v2(db);
            db = IntPtr.Zero;
        }
    }

    private static class Native
    {
        private const string Lib = "knowledgehub_sqlite";
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr stmt, IntPtr tail);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_text(IntPtr stmt, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int length, IntPtr destructor);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_null(IntPtr stmt, int index);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_type(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_name(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long sqlite3_last_insert_rowid(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_changes(IntPtr db);
    }
}

public sealed class Row : Dictionary<string, string?>
{
    public string Text(string key) => this[key] ?? "";
    public int Int(string key) => int.Parse(Text(key), CultureInfo.InvariantCulture);
    public DateTime Date(string key) => DateTime.Parse(Text(key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    public DateTime? NullableDate(string key) => this[key] is null ? null : Date(key);
}
