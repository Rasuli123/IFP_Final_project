using System.ComponentModel.DataAnnotations;
using KnowledgeHub.Core;

namespace KnowledgeHub.Infrastructure;
public sealed class LibraryService(LibraryDatabase database, TimeProvider clock, LibraryChangeBus changes) : ILibraryService
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private const string FreeCopy = "c.Retired=0 AND NOT EXISTS(SELECT 1 FROM Loans l WHERE l.CopyId=c.Id AND l.ReturnedAt IS NULL) AND NOT EXISTS(SELECT 1 FROM Holds h WHERE h.CopyId=c.Id AND h.Status=1)";
    private const string BookProjection = "SELECT b.*, (SELECT COUNT(*) FROM Copies c WHERE c.BookId=b.Id AND c.Retired=0) Copies, (SELECT COUNT(*) FROM Copies c WHERE c.BookId=b.Id AND " + FreeCopy + ") Available FROM Books b";
    public PageResult<BookCard> Catalog(CatalogQuery query)
    {
        using var db = database.Open();
        var size = Math.Clamp(query.PageSize, 1, 50);
        var search = query.Search.Trim();
        if (search.Length > 160)
            search = search[..160];
        var where = " WHERE Archived=0 AND (?='' OR instr(lower(Title),lower(?))>0 OR instr(lower(Author),lower(?))>0 OR instr(Isbn,?)>0) AND (?='' OR Category=?) AND (?=0 OR Available>0)";
        object? [] args = [search, search, search, search, query.Category, query.Category, query.AvailableOnly];
        var count = db.Scalar("SELECT COUNT(*) FROM (" + BookProjection + ")" + where, args);
        var page = Math.Clamp(query.Page, 1, Math.Max(1, (int)Math.Ceiling(count / (double)size)));
        var sort = query.Sort switch
        {
            "author" => "Author COLLATE NOCASE,Title",
            "newest" => "Year DESC,Title",
            "available" => "Available DESC,Title",
            _ => "Title COLLATE NOCASE"
        };
        var rows = db.Query("SELECT * FROM (" + BookProjection + ")" + where + " ORDER BY " + sort + " LIMIT ? OFFSET ?", [..args, size, (page - 1) * size]);
        return new(rows.Select(r => new BookCard(r.Int("Id"), r.Text("Title"), r.Text("Author"), r.Text("Category"), r.Text("Isbn"), r.Int("Year"), r.Text("Shelf"), r.Int("CoverStyle"), r.Int("Copies"), r.Int("Available"))).ToList(), count, page, size);
    }

    public IReadOnlyList<string> Categories()
    {
        using var db = database.Open();
        return db.Query("SELECT DISTINCT Category FROM Books WHERE Archived=0 ORDER BY Category").Select(r => r.Text("Category")).ToList();
    }

    public BookDetails? Book(int id)
    {
        using var db = database.Open();
        var row = db.Query("SELECT * FROM Books WHERE Id=? AND Archived=0", id).FirstOrDefault();
        if (row is null)
            return null;
        var copies = db.Query("SELECT c.Id,c.Barcode,CASE WHEN c.Retired=1 THEN 'Retired' WHEN EXISTS(SELECT 1 FROM Loans l WHERE l.CopyId=c.Id AND l.ReturnedAt IS NULL) THEN 'On loan' WHEN EXISTS(SELECT 1 FROM Holds h WHERE h.CopyId=c.Id AND h.Status=1) THEN 'On hold' ELSE 'Available' END Status FROM Copies c WHERE BookId=? ORDER BY c.Id", id);
        return new(MapBook(row), copies.Select(r => new CopyView(r.Int("Id"), r.Text("Barcode"), r.Text("Status"))).ToList(), db.Scalar("SELECT COUNT(*) FROM Holds WHERE BookId=? AND Status=0", id));
    }

    public IReadOnlyList<LoanView> Loans(Actor actor, bool includeReturned = false)
    {
        using var db = database.Open();
        Guard(db, actor);
        return db.Query("SELECT l.*,b.Id BookId,b.Title,b.Author,c.Barcode,u.Name MemberName FROM Loans l JOIN Copies c ON c.Id=l.CopyId JOIN Books b ON b.Id=c.BookId JOIN Users u ON u.Id=l.MemberId WHERE (?=1 OR l.MemberId=?) AND (?=1 OR l.ReturnedAt IS NULL) ORDER BY l.ReturnedAt IS NOT NULL,l.DueAt LIMIT 500", actor.IsLibrarian, actor.Id, includeReturned).Select(r => new LoanView(r.Int("Id"), r.Int("BookId"), r.Text("Title"), r.Text("Author"), r.Text("Barcode"), r.Text("MemberId"), r.Text("MemberName"), r.Date("BorrowedAt"), r.Date("DueAt"), r.NullableDate("ReturnedAt"), r.Int("Renewals"))).ToList();
    }

    public IReadOnlyList<HoldView> Holds(Actor actor)
    {
        using var db = database.Open();
        Guard(db, actor);
        return db.Query("SELECT h.*,b.Title,u.Name MemberName,(SELECT COUNT(*) FROM Holds x WHERE x.BookId=h.BookId AND x.Status=0 AND (x.CreatedAt<h.CreatedAt OR (x.CreatedAt=h.CreatedAt AND x.Id<=h.Id))) Position FROM Holds h JOIN Books b ON b.Id=h.BookId JOIN Users u ON u.Id=h.MemberId WHERE (?=1 OR h.MemberId=?) AND h.Status IN(0,1) ORDER BY h.Status DESC,h.CreatedAt,h.Id LIMIT 500", actor.IsLibrarian, actor.Id).Select(r => new HoldView(r.Int("Id"), r.Int("BookId"), r.Text("Title"), r.Text("MemberId"), r.Text("MemberName"), (HoldStatus)r.Int("Status"), r.Date("CreatedAt"), r.NullableDate("ExpiresAt"), r.Int("Position"))).ToList();
    }

    public IReadOnlyList<MemberView> Members(Actor actor)
    {
        using var db = database.Open();
        Guard(db, actor);
        actor.RequireLibrarian();
        return db.Query("SELECT u.Id,u.Name,u.Username,u.Email,(SELECT COUNT(*) FROM Loans l WHERE l.MemberId=u.Id AND l.ReturnedAt IS NULL) Active,(SELECT COUNT(*) FROM Loans l WHERE l.MemberId=u.Id AND l.ReturnedAt IS NULL AND l.DueAt<?) Overdue FROM Users u WHERE Role='Member' ORDER BY u.Name LIMIT 500", Now).Select(r => new MemberView(r.Text("Id"), r.Text("Name"), r.Text("Username"), r.Text("Email"), r.Int("Active"), r.Int("Overdue"))).ToList();
    }

    public Dashboard Dashboard(Actor actor)
    {
        using var db = database.Open();
        Guard(db, actor);
        actor.RequireLibrarian();
        return new(db.Scalar("SELECT COUNT(*) FROM Books WHERE Archived=0"), db.Scalar("SELECT COUNT(*) FROM Copies WHERE Retired=0"), db.Scalar("SELECT COUNT(*) FROM Copies c WHERE " + FreeCopy), db.Scalar("SELECT COUNT(*) FROM Loans WHERE ReturnedAt IS NULL"), db.Scalar("SELECT COUNT(*) FROM Loans WHERE ReturnedAt IS NULL AND DueAt<?", Now), db.Scalar("SELECT COUNT(*) FROM Holds WHERE Status IN(0,1)"), db.Scalar("SELECT COUNT(*) FROM Users WHERE Role='Member'"), db.Query("SELECT Category,COUNT(*) Count FROM Books WHERE Archived=0 GROUP BY Category ORDER BY Count DESC").Select(r => new CategoryCount(r.Text("Category"), r.Int("Count"))).ToList(), db.Query("SELECT b.Title,COUNT(*) Count FROM Loans l JOIN Copies c ON c.Id=l.CopyId JOIN Books b ON b.Id=c.BookId GROUP BY b.Id ORDER BY Count DESC,b.Title LIMIT 5").Select(r => new PopularBook(r.Text("Title"), r.Int("Count"))).ToList(), db.Query("SELECT * FROM Audit ORDER BY Id DESC LIMIT 8").Select(r => new AuditEntry(r.Int("Id"), r.Text("Action"), r.Text("Detail"), r.Date("At"))).ToList());
    }

    public int SaveBook(Actor actor, Book book, int initialCopies = 1) => Write(actor, db =>
    {
        actor.RequireLibrarian();
        book.Title = book.Title.Trim();
        book.Author = book.Author.Trim();
        book.Category = book.Category.Trim();
        book.Isbn = book.Isbn.Trim();
        book.Shelf = book.Shelf.Trim();
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(book, new ValidationContext(book), errors, true))
            throw new LibraryException(errors[0].ErrorMessage!);
        if (initialCopies is < 1 or > 20)
            throw new LibraryException("Add between 1 and 20 copies at a time.");
        if (db.Scalar("SELECT COUNT(*) FROM Books WHERE Isbn=? AND Id<>?", book.Isbn, book.Id) > 0)
            throw new LibraryException("This ISBN is already in the catalog, including archived titles.");
        int id;
        if (book.Id == 0)
        {
            db.Exec("INSERT INTO Books(Title,Author,Isbn,Category,Year,Description,Shelf,CoverStyle) VALUES(?,?,?,?,?,?,?,?)", book.Title, book.Author, book.Isbn, book.Category, book.Year, book.Description, book.Shelf, book.CoverStyle);
            id = (int)db.LastId;
            for (var i = 0; i < initialCopies; i++)
                AddCopyInternal(db, id);
        }
        else
        {
            id = book.Id;
            db.Exec("UPDATE Books SET Title=?,Author=?,Isbn=?,Category=?,Year=?,Description=?,Shelf=?,CoverStyle=?,Version=Version+1 WHERE Id=? AND Version=? AND Archived=0", book.Title, book.Author, book.Isbn, book.Category, book.Year, book.Description, book.Shelf, book.CoverStyle, id, book.Version);
            if (db.Changes == 0)
                throw new LibraryException("This book changed in another session. Reload before editing again.");
        }

        Audit(db, actor.Id, book.Id == 0 ? "Book added" : "Book updated", book.Title);
        return id;
    });
    public void AddCopy(Actor actor, int bookId) => Write(actor, db =>
    {
        actor.RequireLibrarian();
        RequireBook(db, bookId);
        AddCopyInternal(db, bookId);
        Allocate(db);
        Audit(db, actor.Id, "Copy added", $"Book #{bookId}");
        return 0;
    });
    public void RetireCopy(Actor actor, int copyId) => Write(actor, db =>
    {
        actor.RequireLibrarian();
        if (db.Scalar("SELECT COUNT(*) FROM Copies WHERE Id=? AND Retired=0", copyId) == 0)
            throw new LibraryException("Active copy not found.");
        if (db.Scalar("SELECT COUNT(*) FROM Loans WHERE CopyId=? AND ReturnedAt IS NULL", copyId) > 0 || db.Scalar("SELECT COUNT(*) FROM Holds WHERE CopyId=? AND Status=1", copyId) > 0)
            throw new LibraryException("Return or release this copy before retiring it.");
        var bookId = db.Scalar("SELECT BookId FROM Copies WHERE Id=?", copyId);
        if (db.Scalar("SELECT COUNT(*) FROM Copies WHERE BookId=? AND Retired=0", bookId) == 1 && db.Scalar("SELECT COUNT(*) FROM Holds WHERE BookId=? AND Status=0", bookId) > 0)
            throw new LibraryException("The last copy has waiting reservations.");
        db.Exec("UPDATE Copies SET Retired=1 WHERE Id=?", copyId);
        Audit(db, actor.Id, "Copy retired", $"Copy #{copyId}");
        return 0;
    });
    public void ArchiveBook(Actor actor, int bookId) => Write(actor, db =>
    {
        actor.RequireLibrarian();
        RequireBook(db, bookId);
        if (db.Scalar("SELECT COUNT(*) FROM Loans l JOIN Copies c ON c.Id=l.CopyId WHERE c.BookId=? AND l.ReturnedAt IS NULL", bookId) > 0 || db.Scalar("SELECT COUNT(*) FROM Holds WHERE BookId=? AND Status IN(0,1)", bookId) > 0)
            throw new LibraryException("Resolve active loans and reservations before archiving this title.");
        db.Exec("UPDATE Books SET Archived=1,Version=Version+1 WHERE Id=?", bookId);
        db.Exec("UPDATE Copies SET Retired=1 WHERE BookId=?", bookId);
        Audit(db, actor.Id, "Book archived", $"Book #{bookId}. History retained.");
        return 0;
    });
    public int Issue(Actor actor, int bookId, string memberId) => Write(actor, db =>
    {
        actor.RequireLibrarian();
        RequireBook(db, bookId);
        if (db.Scalar("SELECT COUNT(*) FROM Users WHERE Id=? AND Role='Member'", memberId) != 1)
            throw new LibraryException("Choose a valid member.");
        LendingRules.CanBorrow(db.Scalar("SELECT COUNT(*) FROM Loans WHERE MemberId=? AND ReturnedAt IS NULL", memberId), db.Scalar("SELECT COUNT(*) FROM Loans WHERE MemberId=? AND ReturnedAt IS NULL AND DueAt<?", memberId, Now) > 0);
        if (db.Scalar("SELECT COUNT(*) FROM Loans l JOIN Copies c ON c.Id=l.CopyId WHERE l.MemberId=? AND c.BookId=? AND l.ReturnedAt IS NULL", memberId, bookId) > 0)
            throw new LibraryException("This member already has this title on loan.");
        var copy = db.Query("SELECT c.Id FROM Copies c WHERE c.BookId=? AND c.Retired=0 AND NOT EXISTS(SELECT 1 FROM Loans l WHERE l.CopyId=c.Id AND l.ReturnedAt IS NULL) AND NOT EXISTS(SELECT 1 FROM Holds h WHERE h.CopyId=c.Id AND h.Status=1 AND h.MemberId<>?) ORDER BY CASE WHEN EXISTS(SELECT 1 FROM Holds h WHERE h.CopyId=c.Id AND h.Status=1 AND h.MemberId=?) THEN 0 ELSE 1 END,c.Id LIMIT 1", bookId, memberId, memberId).FirstOrDefault();
        if (copy is null)
            throw new LibraryException("No copy is available for this member. Existing pickup holds take priority.");
        db.Exec("INSERT INTO Loans(CopyId,MemberId,BorrowedAt,DueAt) VALUES(?,?,?,?)", copy.Int("Id"), memberId, Now, Now.AddDays(LendingRules.LoanDays));
        var id = (int)db.LastId;
        db.Exec("UPDATE Holds SET Status=2 WHERE BookId=? AND MemberId=? AND Status IN(0,1)", bookId, memberId);
        Audit(db, actor.Id, "Book issued", $"Loan #{id}, book #{bookId}");
        return id;
    });
    public void Return(Actor actor, int loanId) => Write(actor, db =>
    {
        actor.RequireLibrarian();
        db.Exec("UPDATE Loans SET ReturnedAt=? WHERE Id=? AND ReturnedAt IS NULL", Now, loanId);
        if (db.Changes == 0)
            throw new LibraryException("This loan was already returned or does not exist.");
        Allocate(db);
        Audit(db, actor.Id, "Book returned", $"Loan #{loanId}");
        return 0;
    });
    public void Renew(Actor actor, int loanId) => Write(actor, db =>
    {
        var loan = db.Query("SELECT l.*,c.BookId FROM Loans l JOIN Copies c ON c.Id=l.CopyId WHERE l.Id=?", loanId).FirstOrDefault() ?? throw new LibraryException("Loan not found.");
        actor.RequireOwner(loan.Text("MemberId"));
        var due = LendingRules.Renew(loan.Date("DueAt"), loan["ReturnedAt"] is not null, loan.Int("Renewals"), db.Scalar("SELECT COUNT(*) FROM Holds WHERE BookId=? AND Status IN(0,1) AND MemberId<>?", loan.Int("BookId"), loan.Text("MemberId")) > 0, Now);
        db.Exec("UPDATE Loans SET DueAt=?,Renewals=Renewals+1 WHERE Id=?", due, loanId);
        Audit(db, actor.Id, "Loan renewed", $"Loan #{loanId}");
        return 0;
    });
    public void Reserve(Actor actor, int bookId) => Write(actor, db =>
    {
        if (actor.IsLibrarian)
            throw new LibraryException("Use a member account to reserve a book.");
        RequireBook(db, bookId);
        if (db.Scalar("SELECT COUNT(*) FROM Copies WHERE BookId=? AND Retired=0", bookId) == 0)
            throw new LibraryException("This title currently has no lendable copies.");
        if (db.Scalar("SELECT COUNT(*) FROM Holds WHERE BookId=? AND MemberId=? AND Status IN(0,1)", bookId, actor.Id) > 0)
            throw new LibraryException("You already have a reservation for this title.");
        if (db.Scalar("SELECT COUNT(*) FROM Holds WHERE MemberId=? AND Status IN(0,1)", actor.Id) >= LendingRules.MaximumReservations)
            throw new LibraryException("You can have up to three active reservations.");
        if (db.Scalar("SELECT COUNT(*) FROM Loans l JOIN Copies c ON c.Id=l.CopyId WHERE l.MemberId=? AND c.BookId=? AND l.ReturnedAt IS NULL", actor.Id, bookId) > 0)
            throw new LibraryException("You already have this title on loan.");
        LendingRules.CanBorrow(db.Scalar("SELECT COUNT(*) FROM Loans WHERE MemberId=? AND ReturnedAt IS NULL", actor.Id), db.Scalar("SELECT COUNT(*) FROM Loans WHERE MemberId=? AND ReturnedAt IS NULL AND DueAt<?", actor.Id, Now) > 0);
        db.Exec("INSERT INTO Holds(BookId,MemberId,CreatedAt) VALUES(?,?,?)", bookId, actor.Id, Now);
        Allocate(db);
        Audit(db, actor.Id, "Reservation placed", $"Book #{bookId}");
        return 0;
    });
    public void CancelHold(Actor actor, int holdId) => Write(actor, db =>
    {
        var hold = db.Query("SELECT * FROM Holds WHERE Id=? AND Status IN(0,1)", holdId).FirstOrDefault() ?? throw new LibraryException("This reservation is no longer active.");
        actor.RequireOwner(hold.Text("MemberId"));
        db.Exec("UPDATE Holds SET Status=3 WHERE Id=?", holdId);
        Allocate(db);
        Audit(db, actor.Id, "Reservation cancelled", $"Reservation #{holdId}");
        return 0;
    });
    public void MaintainHolds()
    {
        var changed = database.Write(db => Allocate(db));
        if (changed)
            changes.Publish();
    }

    private T Write<T>(Actor actor, Func<Sqlite, T> action)
    {
        // Validate and save atomically; notify browsers only after commit.
        var result = database.Write(db =>
        {
            Guard(db, actor);
            Allocate(db);
            return action(db);
        });
        changes.Publish();
        return result;
    }

    private bool Allocate(Sqlite db)
    {
        // Expire pickup holds, then allocate free copies in queue order.
        db.Exec("UPDATE Holds SET Status=4 WHERE Status=1 AND ExpiresAt<=?", Now);
        var changed = db.Changes > 0;
        foreach (var hold in db.Query("SELECT * FROM Holds WHERE Status=0 ORDER BY CreatedAt,Id"))
        {
            var copy = db.Query("SELECT c.Id FROM Copies c JOIN Books b ON b.Id=c.BookId WHERE c.BookId=? AND b.Archived=0 AND " + FreeCopy + " ORDER BY c.Id LIMIT 1", hold.Int("BookId")).FirstOrDefault();
            if (copy is null)
                continue;
            db.Exec("UPDATE Holds SET Status=1,CopyId=?,ExpiresAt=? WHERE Id=?", copy.Int("Id"), Now.AddHours(LendingRules.HoldHours), hold.Int("Id"));
            Audit(db, "system", "Ready for pickup", $"Reservation #{hold.Int("Id")}");
            changed = true;
        }

        return changed;
    }

    private static void Guard(Sqlite db, Actor actor)
    {
        var role = db.Query("SELECT Role FROM Users WHERE Id=?", actor.Id).FirstOrDefault()?.Text("Role");
        if (role is null || (actor.IsLibrarian && role != "Librarian"))
            throw new LibraryException("Please sign in with an authorized account.");
    }

    private static void RequireBook(Sqlite db, int id)
    {
        if (db.Scalar("SELECT COUNT(*) FROM Books WHERE Id=? AND Archived=0", id) == 0)
            throw new LibraryException("Book not found.");
    }

    private static void AddCopyInternal(Sqlite db, int bookId) => db.Exec("INSERT INTO Copies(BookId,Barcode) VALUES(?,?)", bookId, $"KH-{bookId:D4}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}");
    private void Audit(Sqlite db, string actor, string action, string detail) => db.Exec("INSERT INTO Audit(ActorId,Action,Detail,At) VALUES(?,?,?,?)", actor, action, detail, Now);
    private static Book MapBook(Row r) => new()
    {
        Id = r.Int("Id"),
        Title = r.Text("Title"),
        Author = r.Text("Author"),
        Isbn = r.Text("Isbn"),
        Category = r.Text("Category"),
        Year = r.Int("Year"),
        Description = r.Text("Description"),
        Shelf = r.Text("Shelf"),
        CoverStyle = r.Int("CoverStyle"),
        Archived = r.Int("Archived") != 0,
        Version = r.Int("Version")
    };
}
