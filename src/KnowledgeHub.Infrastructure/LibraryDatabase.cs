namespace KnowledgeHub.Infrastructure;
public sealed class LibraryDatabase
{
    public string Path { get; }

    public LibraryDatabase(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
    }

    public Sqlite Open() => new(Path);
    public void Initialize()
    {
        using var db = Open();
        db.Exec("PRAGMA journal_mode=WAL");
        db.Exec("BEGIN IMMEDIATE");
        try
        {
            foreach (var statement in Schema.Split(';', StringSplitOptions.RemoveEmptyEntries))
                db.Exec(statement);
            // Preserve existing accounts when upgrading the earlier database.
            if (!db.Query("PRAGMA table_info(Users)").Any(column => column.Text("name") == "Username"))
                db.Exec("ALTER TABLE Users ADD COLUMN Username TEXT COLLATE NOCASE");
            db.Exec("UPDATE Users SET Username=Email WHERE Username IS NULL");
            db.Exec("CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_Username ON Users(Username COLLATE NOCASE)");
            db.Exec("PRAGMA user_version=2");
            db.Exec("COMMIT");
        }
        catch
        {
            db.Exec("ROLLBACK");
            throw;
        }
    }

    public T Write<T>(Func<Sqlite, T> action)
    {
        using var db = Open();
        db.Exec("BEGIN IMMEDIATE"); // Acquire the database write lock before checking business invariants.
        try
        {
            var result = action(db);
            db.Exec("COMMIT");
            return result;
        }
        catch
        {
            db.Exec("ROLLBACK");
            throw;
        }
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS Users(
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            Username TEXT NOT NULL COLLATE NOCASE,
            Email TEXT NOT NULL COLLATE NOCASE UNIQUE,
            PasswordHash TEXT NOT NULL,
            Role TEXT NOT NULL CHECK(Role IN ('Member','Librarian')),
            Stamp TEXT NOT NULL,
            FailedLogins INTEGER NOT NULL DEFAULT 0,
            LockedUntil TEXT
        );
        CREATE TABLE IF NOT EXISTS Books(
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Title TEXT NOT NULL,
            Author TEXT NOT NULL,
            Isbn TEXT NOT NULL UNIQUE,
            Category TEXT NOT NULL,
            Year INTEGER NOT NULL,
            Description TEXT NOT NULL,
            Shelf TEXT NOT NULL,
            CoverStyle INTEGER NOT NULL,
            Archived INTEGER NOT NULL DEFAULT 0,
            Version INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS Copies(
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            BookId INTEGER NOT NULL REFERENCES Books(Id),
            Barcode TEXT NOT NULL UNIQUE,
            Retired INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS Loans(
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CopyId INTEGER NOT NULL REFERENCES Copies(Id),
            MemberId TEXT NOT NULL REFERENCES Users(Id),
            BorrowedAt TEXT NOT NULL,
            DueAt TEXT NOT NULL,
            ReturnedAt TEXT,
            Renewals INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS Holds(
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            BookId INTEGER NOT NULL REFERENCES Books(Id),
            MemberId TEXT NOT NULL REFERENCES Users(Id),
            CreatedAt TEXT NOT NULL,
            Status INTEGER NOT NULL DEFAULT 0 CHECK(Status BETWEEN 0 AND 4),
            CopyId INTEGER REFERENCES Copies(Id),
            ExpiresAt TEXT
        );
        CREATE TABLE IF NOT EXISTS Audit(
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ActorId TEXT NOT NULL,
            Action TEXT NOT NULL,
            Detail TEXT NOT NULL,
            At TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Loan_ActiveCopy ON Loans(CopyId) WHERE ReturnedAt IS NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Hold_ActiveMember ON Holds(BookId,MemberId) WHERE Status IN (0,1);
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Hold_ReadyCopy ON Holds(CopyId) WHERE Status=1;
        CREATE INDEX IF NOT EXISTS IX_Loans_Member ON Loans(MemberId,ReturnedAt);
        CREATE INDEX IF NOT EXISTS IX_Loans_Due ON Loans(DueAt) WHERE ReturnedAt IS NULL;
        CREATE INDEX IF NOT EXISTS IX_Holds_Queue ON Holds(BookId,Status,CreatedAt,Id);
        CREATE INDEX IF NOT EXISTS IX_Copies_Book ON Copies(BookId,Retired);
        CREATE INDEX IF NOT EXISTS IX_Books_Category ON Books(Category,Archived);
        CREATE INDEX IF NOT EXISTS IX_Books_Title ON Books(Title COLLATE NOCASE);
        """;
}
