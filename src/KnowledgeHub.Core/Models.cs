using System.ComponentModel.DataAnnotations;

namespace KnowledgeHub.Core;
// Change the visible brand here; technical project names can stay unchanged.
public static class AppInfo
{
    public const string Name = "Rasuli's Knowledge Hub";
}

public sealed class Book
{
    public int Id { get; set; }

    [Required, StringLength(160)]
    public string Title { get; set; } = "";

    [Required, StringLength(100)]
    public string Author { get; set; } = "";

    [Required, RegularExpression(@"\d{13}", ErrorMessage = "Enter a 13-digit ISBN.")]
    public string Isbn { get; set; } = "";

    [Required, StringLength(50)]
    public string Category { get; set; } = "";

    [Range(1000, 2100)]
    public int Year { get; set; } = 2024;

    [StringLength(2000)]
    public string Description { get; set; } = "";

    [Required, StringLength(30)]
    public string Shelf { get; set; } = "A-01";

    [Range(0, 5)]
    public int CoverStyle { get; set; }
    public bool Archived { get; set; }
    public int Version { get; set; }
}

public sealed record Actor(string Id, bool IsLibrarian)
{
    public void RequireLibrarian()
    {
        if (!IsLibrarian)
            throw new LibraryException("Only a librarian can perform this action.");
    }

    public void RequireOwner(string id)
    {
        if (!IsLibrarian && Id != id)
            throw new LibraryException("You cannot access another member's records.");
    }
}

public sealed class LibraryException(string message) : Exception(message);
public enum HoldStatus
{
    Queued,
    Ready,
    Fulfilled,
    Cancelled,
    Expired
}

// Every screen uses these same borrowing rules.
public static class LendingRules
{
    public const int LoanDays = 14, HoldHours = 48, MaximumLoans = 5, MaximumReservations = 3;
    public static void CanBorrow(int active, bool overdue)
    {
        if (overdue)
            throw new LibraryException("Return overdue books before borrowing again.");
        if (active >= MaximumLoans)
            throw new LibraryException("A member can borrow up to five books at a time.");
    }

    public static DateTime Renew(DateTime due, bool returned, int renewals, bool waiting, DateTime now)
    {
        if (returned)
            throw new LibraryException("This book has already been returned.");
        if (due < now)
            throw new LibraryException("Overdue loans cannot be renewed.");
        if (renewals >= 1)
            throw new LibraryException("A loan can only be renewed once.");
        if (waiting)
            throw new LibraryException("Another member is waiting for this title.");
        return due.AddDays(LoanDays);
    }
}

public sealed record CatalogQuery(string Search = "", string Category = "", bool AvailableOnly = false, string Sort = "title", int Page = 1, int PageSize = 8);
public sealed record PageResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}

public sealed record BookCard(int Id, string Title, string Author, string Category, string Isbn, int Year, string Shelf, int CoverStyle, int Copies, int Available);
public sealed record BookDetails(Book Book, IReadOnlyList<CopyView> Copies, int QueueLength);
public sealed record CopyView(int Id, string Barcode, string Status);
public sealed record LoanView(int Id, int BookId, string Title, string Author, string Barcode, string MemberId, string MemberName, DateTime BorrowedAt, DateTime DueAt, DateTime? ReturnedAt, int Renewals);
public sealed record HoldView(int Id, int BookId, string Title, string MemberId, string MemberName, HoldStatus Status, DateTime CreatedAt, DateTime? ExpiresAt, int Position);
public sealed record MemberView(string Id, string Name, string Username, string Email, int ActiveLoans, int OverdueLoans);
public sealed record CategoryCount(string Category, int Count);
public sealed record PopularBook(string Title, int Count);
public sealed record AuditEntry(long Id, string Action, string Detail, DateTime At);
public sealed record Dashboard(int Titles, int Copies, int Available, int ActiveLoans, int OverdueLoans, int Reservations, int Members, IReadOnlyList<CategoryCount> Categories, IReadOnlyList<PopularBook> Popular, IReadOnlyList<AuditEntry> Recent);
public sealed record UserAccount(string Id, string Name, string Username, string Email, string Role, string SecurityStamp);
public interface ILibraryService
{
    PageResult<BookCard> Catalog(CatalogQuery query);
    IReadOnlyList<string> Categories();
    BookDetails? Book(int id);
    IReadOnlyList<LoanView> Loans(Actor actor, bool includeReturned = false);
    IReadOnlyList<HoldView> Holds(Actor actor);
    IReadOnlyList<MemberView> Members(Actor actor);
    Dashboard Dashboard(Actor actor);
    int SaveBook(Actor actor, Book book, int initialCopies = 1);
    void AddCopy(Actor actor, int bookId);
    void RetireCopy(Actor actor, int copyId);
    void ArchiveBook(Actor actor, int bookId);
    int Issue(Actor actor, int bookId, string memberId);
    void Return(Actor actor, int loanId);
    void Renew(Actor actor, int loanId);
    void Reserve(Actor actor, int bookId);
    void CancelHold(Actor actor, int holdId);
    void MaintainHolds();
}

public interface IAccountService
{
    UserAccount Register(string name, string email, string password, string? username = null);
    UserAccount? Authenticate(string login, string password);
    void ChangePassword(string id, string currentPassword, string newPassword);
    UserAccount? Get(string id);
}

// Invalidation contains no private data. Subscribers reload only their own authorized records.
public sealed class LibraryChangeBus
{
    public event Action? Changed;
    public void Publish()
    {
        foreach (Action action in Changed?.GetInvocationList() ?? [])
            try
            {
                action();
            }
            catch (ObjectDisposedException)
            {
            }
    }
}
