using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeHub.Core;
using KnowledgeHub.Infrastructure;

var tests = new List<(string Name, Action Body)>();
void Test(string name, Action body) => tests.Add((name, body));
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}; got {actual}.");
}

static void True(bool value)
{
    if (!value)
        throw new Exception("Expected true.");
}

static void Reject(Action action)
{
    try
    {
        action();
    }
    catch (LibraryException)
    {
        return;
    }

    throw new Exception("Expected a domain rejection.");
}

static void RejectSql(Action action)
{
    try
    {
        action();
    }
    catch (InvalidOperationException)
    {
        return;
    }

    throw new Exception("Expected a database constraint rejection.");
}

var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
Test("UNIT: Borrowing accepts a member below the limit", () => LendingRules.CanBorrow(4, false));
Test("UNIT: Borrowing rejects the fifth active loan limit", () => Reject(() => LendingRules.CanBorrow(5, false)));
Test("UNIT: Overdue loans prevent borrowing", () => Reject(() => LendingRules.CanBorrow(1, true)));
Test("UNIT: Renewal extends the existing due date by 14 days", () => Equal(now.AddDays(20), LendingRules.Renew(now.AddDays(6), false, 0, false, now)));
Test("UNIT: Returned loans cannot be renewed", () => Reject(() => LendingRules.Renew(now.AddDays(6), true, 0, false, now)));
Test("UNIT: Overdue loans cannot be renewed", () => Reject(() => LendingRules.Renew(now.AddSeconds(-1), false, 0, false, now)));
Test("UNIT: Renewal is limited to once", () => Reject(() => LendingRules.Renew(now.AddDays(6), false, 1, false, now)));
Test("UNIT: Waiting readers block renewal", () => Reject(() => LendingRules.Renew(now.AddDays(6), false, 0, true, now)));
Test("UNIT: Due date boundary is inclusive", () => Equal(now.AddDays(14), LendingRules.Renew(now, false, 0, false, now)));
Test("UNIT: Member cannot access another member", () => Reject(() => new Actor("one", false).RequireOwner("two")));
Test("UNIT: Librarian can manage a member record", () => new Actor("admin", true).RequireOwner("two"));
Test("UNIT: Page count rounds up", () => Equal(3, new PageResult<int>([], 17, 1, 8).Pages));
Test("UNIT: Empty results retain one page", () => Equal(1, new PageResult<int>([], 0, 1, 8).Pages));
Test("DB: SQLite opens and persists UTF-8 text", () =>
{
    using var f = new Fixture();
    var id = f.Add("C# and café — کتاب");
    Equal("C# and café — کتاب", f.Library.Book(id)!.Book.Title);
});
Test("DB: Foreign keys reject an orphan loan", () =>
{
    using var f = new Fixture();
    using var db = f.Database.Open();
    RejectSql(() => db.Exec("INSERT INTO Loans(CopyId,MemberId,BorrowedAt,DueAt) VALUES(999,'missing','x','x')"));
});
Test("DB: Transaction rolls back every change on failure", () =>
{
    using var f = new Fixture();
    try
    {
        f.Database.Write<int>(db =>
        {
            db.Exec("UPDATE Users SET Name='Changed' WHERE Id=?", f.Member.Id);
            throw new LibraryException("test");
        });
    }
    catch (LibraryException)
    {
    }

    Equal("First Reader", f.Accounts.Get(f.Member.Id)!.Name);
});
Test("ACCOUNT: Passwords are hashed and credentials authenticate", () =>
{
    using var f = new Fixture();
    Equal(f.Member.Id, f.Accounts.Authenticate("READER@EXAMPLE.COM", "Reader2026!")!.Id);
    using var db = f.Database.Open();
    True(db.Query("SELECT PasswordHash FROM Users WHERE Id=?", f.Member.Id)[0].Text("PasswordHash") != "Reader2026!");
});
Test("ACCOUNT: Wrong and nonexistent credentials fail", () =>
{
    using var f = new Fixture();
    True(f.Accounts.Authenticate("reader@example.com", "incorrect")is null);
    True(f.Accounts.Authenticate("missing@example.com", "incorrect")is null);
});
Test("ACCOUNT: Email uniqueness ignores case", () =>
{
    using var f = new Fixture();
    Reject(() => f.Accounts.Register("Another Reader", "READER@EXAMPLE.COM", "Reader2026!"));
});
Test("ACCOUNT: Registration validates name, email, and password", () =>
{
    using var f = new Fixture();
    Reject(() => f.Accounts.Register("A", "a@b.com", "Reader2026!"));
    Reject(() => f.Accounts.Register("Reader", "invalid", "Reader2026!"));
    Reject(() => f.Accounts.Register("Reader", "a@b.com", "short"));
});
Test("ACCOUNT: Five failed logins trigger a timed lockout", () =>
{
    using var f = new Fixture();
    for (int i = 0; i < 5; i++)
        f.Accounts.Authenticate("reader@example.com", "wrong");
    True(f.Accounts.Authenticate("reader@example.com", "Reader2026!")is null);
    f.Clock.Advance(TimeSpan.FromMinutes(6));
    True(f.Accounts.Authenticate("reader@example.com", "Reader2026!")is not null);
});
Test("ACCOUNT: Password change rotates session stamp", () =>
{
    using var f = new Fixture();
    var old = f.Accounts.Get(f.Member.Id)!.SecurityStamp;
    f.Accounts.ChangePassword(f.Member.Id, "Reader2026!", "Different2026!");
    True(old != f.Accounts.Get(f.Member.Id)!.SecurityStamp);
    True(f.Accounts.Authenticate("reader@example.com", "Reader2026!")is null);
    True(f.Accounts.Authenticate("reader@example.com", "Different2026!")is not null);
});
Test("ACCOUNT: Wrong current password cannot change credentials", () =>
{
    using var f = new Fixture();
    Reject(() => f.Accounts.ChangePassword(f.Member.Id, "wrong", "Different2026!"));
});
// Username checks protect both sign-in and administrator-created accounts.
Test("ACCOUNT: Username and email both authenticate", () =>
{
    using var f = new Fixture();
    Equal(f.Member.Id, f.Accounts.Authenticate("READER", "Reader2026!")!.Id);
    Equal(f.Member.Id, f.Accounts.Authenticate("reader@example.com", "Reader2026!")!.Id);
});
Test("ACCOUNT: Duplicate usernames ignore case", () =>
{
    using var f = new Fixture();
    Reject(() => f.Accounts.Register("Another Reader", "another@example.com", "Reader2026!", "READER"));
});
Test("ACCOUNT: Invalid usernames are rejected", () =>
{
    using var f = new Fixture();
    foreach (var name in new[]
    {
        "ab",
        "has space",
        "user@example.com",
        "_leading",
        new string ('a', 31)
    }

    )
        Reject(() => f.Accounts.Register("Another Reader", "another@example.com", "Reader2026!", name));
});
Test("ACCOUNT: Public registration always creates a member", () =>
{
    using var f = new Fixture();
    var user = f.Accounts.Register("New Member", "new@example.com", "Reader2026!", "new.member");
    Equal("Member", user.Role);
    Equal("new.member", user.Username);
});
Test("DB: Legacy accounts survive the username migration", () =>
{
    var database = new LibraryDatabase(Path.Combine(Path.GetTempPath(), "KnowledgeHub-tests", Guid.NewGuid().ToString("N"), "legacy.db"));
    using (var old = database.Open())
    {
        old.Exec("CREATE TABLE Users(Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Email TEXT NOT NULL COLLATE NOCASE UNIQUE, PasswordHash TEXT NOT NULL, Role TEXT NOT NULL, Stamp TEXT NOT NULL, FailedLogins INTEGER NOT NULL DEFAULT 0, LockedUntil TEXT)");
        old.Exec("INSERT INTO Users(Id,Name,Email,PasswordHash,Role,Stamp) VALUES('old','Existing Member','old@example.com','hash','Member','stamp')");
    }

    database.Initialize();
    database.Initialize();
    using var db = database.Open();
    Equal("old@example.com", db.Query("SELECT Username FROM Users WHERE Id='old'")[0].Text("Username"));
    Equal(1, db.Scalar("SELECT COUNT(*) FROM Users"));
});
Test("DEMO: Only admin and rasuli are seeded with working passwords", () =>
{
    var database = new LibraryDatabase(Path.Combine(Path.GetTempPath(), "KnowledgeHub-tests", Guid.NewGuid().ToString("N"), "demo.db"));
    database.Initialize();
    var accounts = new AccountService(database, TimeProvider.System);
    var library = new LibraryService(database, TimeProvider.System, new LibraryChangeBus());
    DemoSeed.Initialize(database, accounts, library, TimeProvider.System);
    Equal("Librarian", accounts.Authenticate("admin", "Admin2026!")!.Role);
    Equal("Rasuli", accounts.Authenticate("rasuli", "Rasuli2026!")!.Name);
    accounts.ChangePassword(accounts.Authenticate("rasuli", "Rasuli2026!")!.Id, "Rasuli2026!", "Changed2026!");
    DemoSeed.Initialize(database, accounts, library, TimeProvider.System);
    True(accounts.Authenticate("rasuli", "Changed2026!")is not null);
    using var db = database.Open();
    Equal(2, db.Scalar("SELECT COUNT(*) FROM Users"));
});
Test("CATALOG: Search, category, and pagination compose", () =>
{
    using var f = new Fixture();
    for (int i = 0; i < 12; i++)
        f.Add($"Design {i:D2}");
    Equal(12, f.Library.Catalog(new(Search: "design", Category: "Technology")).Total);
    Equal(4, f.Library.Catalog(new(Page: 2)).Items.Count);
    Equal(2, f.Library.Catalog(new(Page: 99)).Page);
});
Test("CATALOG: Search treats SQL punctuation as literal text", () =>
{
    using var f = new Fixture();
    f.Add();
    Equal(0, f.Library.Catalog(new(Search: "' OR 1=1 --")).Total);
    Equal(1, f.Library.Catalog(new(Sort: "Title; DROP TABLE Books")).Total);
});
Test("CATALOG: ISBN duplication and invalid input fail cleanly", () =>
{
    using var f = new Fixture();
    var id = f.Add();
    var book = f.Library.Book(id)!.Book;
    book.Id = 0;
    Reject(() => f.Library.SaveBook(f.Admin, book));
    book.Isbn = "bad";
    Reject(() => f.Library.SaveBook(f.Admin, book));
});
Test("CATALOG: Optimistic version rejects stale edits", () =>
{
    using var f = new Fixture();
    var id = f.Add();
    var first = f.Library.Book(id)!.Book;
    var stale = f.Library.Book(id)!.Book;
    first.Title = "Edited";
    f.Library.SaveBook(f.Admin, first);
    stale.Title = "Stale";
    Reject(() => f.Library.SaveBook(f.Admin, stale));
    Equal("Edited", f.Library.Book(id)!.Book.Title);
});
Test("ACCESS: Member cannot change catalog or issue loans", () =>
{
    using var f = new Fixture();
    var id = f.Add();
    Reject(() => f.Library.SaveBook(f.Reader, new Book()));
    Reject(() => f.Library.AddCopy(f.Reader, id));
    Reject(() => f.Library.Issue(f.Reader, id, f.Member.Id));
    Reject(() => f.Library.Members(f.Reader));
    Reject(() => f.Library.Dashboard(f.Reader));
});
Test("ACCESS: Forged librarian role is checked against database", () =>
{
    using var f = new Fixture();
    Reject(() => f.Library.Dashboard(new Actor(f.Member.Id, true)));
    Reject(() => f.Library.Loans(new Actor("missing", false)));
});
Test("LOAN: Issue and return update availability and history", () =>
{
    using var f = new Fixture();
    var id = f.Add();
    var loan = f.Library.Issue(f.Admin, id, f.Member.Id);
    Equal(0, f.Library.Catalog(new()).Items[0].Available);
    Equal(f.Clock.GetUtcNow().UtcDateTime.AddDays(14), f.Library.Loans(f.Reader)[0].DueAt);
    f.Library.Return(f.Admin, loan);
    Equal(1, f.Library.Catalog(new()).Items[0].Available);
    Equal(0, f.Library.Loans(f.Reader).Count);
    Equal(1, f.Library.Loans(f.Reader, true).Count);
});
Test("LOAN: Double return fails without changing state", () =>
{
    using var f = new Fixture();
    var loan = f.Library.Issue(f.Admin, f.Add(), f.Member.Id);
    f.Library.Return(f.Admin, loan);
    Reject(() => f.Library.Return(f.Admin, loan));
});
Test("LOAN: Duplicate title and exhausted copies are rejected", () =>
{
    using var f = new Fixture();
    var id = f.Add();
    f.Library.Issue(f.Admin, id, f.Member.Id);
    Reject(() => f.Library.Issue(f.Admin, id, f.Member.Id));
    Reject(() => f.Library.Issue(f.Admin, id, f.Second.Id));
});
Test("LOAN: Fifth loan limit is enforced through the service", () =>
{
    using var f = new Fixture();
    for (int i = 0; i < 5; i++)
        f.Library.Issue(f.Admin, f.Add(), f.Member.Id);
    var next = f.Add();
    Reject(() => f.Library.Issue(f.Admin, next, f.Member.Id));
});
Test("LOAN: Overdue borrower is blocked", () =>
{
    using var f = new Fixture();
    f.Library.Issue(f.Admin, f.Add(), f.Member.Id);
    f.Clock.Advance(TimeSpan.FromDays(15));
    var next = f.Add();
    Reject(() => f.Library.Issue(f.Admin, next, f.Member.Id));
});
Test("LOAN: Member only reads their own loans", () =>
{
    using var f = new Fixture();
    f.Library.Issue(f.Admin, f.Add(), f.Member.Id);
    f.Library.Issue(f.Admin, f.Add(), f.Second.Id);
    Equal(1, f.Library.Loans(f.Reader).Count);
    Equal(f.Member.Id, f.Library.Loans(f.Reader)[0].MemberId);
    Equal(2, f.Library.Loans(f.Admin).Count);
});
Test("LOAN: Member cannot return or renew another member's loan", () =>
{
    using var f = new Fixture();
    var loan = f.Library.Issue(f.Admin, f.Add(), f.Second.Id);
    Reject(() => f.Library.Renew(f.Reader, loan));
    Reject(() => f.Library.Return(f.Reader, loan));
});
Test("LOAN: Renewal persists and cannot repeat", () =>
{
    using var f = new Fixture();
    var loan = f.Library.Issue(f.Admin, f.Add(), f.Member.Id);
    f.Library.Renew(f.Reader, loan);
    Equal(1, f.Library.Loans(f.Reader)[0].Renewals);
    Reject(() => f.Library.Renew(f.Reader, loan));
});
Test("HOLD: Available copy becomes a 48-hour pickup hold", () =>
{
    using var f = new Fixture();
    var id = f.Add();
    f.Library.Reserve(f.Reader, id);
    var h = f.Library.Holds(f.Reader).Single();
    Equal(HoldStatus.Ready, h.Status);
    Equal(f.Clock.GetUtcNow().UtcDateTime.AddHours(48), h.ExpiresAt!.Value);
    Equal(0, f.Library.Catalog(new(AvailableOnly: true)).Total);
});
Test("HOLD: Duplicate reservation and reservation limit fail", () =>
{
    using var f = new Fixture();
    var id = f.Add();
    f.Library.Reserve(f.Reader, id);
    Reject(() => f.Library.Reserve(f.Reader, id));
    f.Library.Reserve(f.Reader, f.Add());
    f.Library.Reserve(f.Reader, f.Add());
    var fourth = f.Add();
    Reject(() => f.Library.Reserve(f.Reader, fourth));
});
Test("HOLD: Borrowed title cannot be reserved by same member", () =>
{
    using var f = new Fixture();
    var id = f.Add();
    f.Library.Issue(f.Admin, id, f.Member.Id);
    Reject(() => f.Library.Reserve(f.Reader, id));
});
Test("HOLD: Ready copy cannot be issued to a different member", () =>
{
    using var f = new Fixture();
    var id = f.Add();
    f.Library.Reserve(f.Reader, id);
    Reject(() => f.Library.Issue(f.Admin, id, f.Second.Id));
    f.Library.Issue(f.Admin, id, f.Member.Id);
    Equal(0, f.Library.Holds(f.Reader).Count);
});
Test("HOLD: Return promotes earliest waiting reservation", () =>
{
    using var f = new Fixture();
    var id = f.Add();
    var loan = f.Library.Issue(f.Admin, id, f.Member.Id);
    f.Library.Reserve(f.Other, id);
    Equal(HoldStatus.Queued, f.Library.Holds(f.Other)[0].Status);
    Reject(() => f.Library.Renew(f.Reader, loan));
    f.Library.Return(f.Admin, loan);
    Equal(HoldStatus.Ready, f.Library.Holds(f.Other)[0].Status);
});
Test("HOLD: Cancellation promotes the next reader", () =>
{
    using var f = new Fixture();
    var id = f.Add();
    f.Library.Reserve(f.Reader, id);
    f.Library.Reserve(f.Other, id);
    Equal(1, f.Library.Holds(f.Other)[0].Position);
    f.Library.CancelHold(f.Reader, f.Library.Holds(f.Reader)[0].Id);
    Equal(HoldStatus.Ready, f.Library.Holds(f.Other)[0].Status);
});
Test("HOLD: Expiry at exactly 48 hours passes the copy onward", () =>
{
    using var f = new Fixture();
    var id = f.Add();
    f.Library.Reserve(f.Reader, id);
    f.Library.Reserve(f.Other, id);
    f.Clock.Advance(TimeSpan.FromHours(48));
    f.Library.MaintainHolds();
    Equal(0, f.Library.Holds(f.Reader).Count);
    Equal(HoldStatus.Ready, f.Library.Holds(f.Other)[0].Status);
});
Test("HOLD: Member cannot cancel another member's hold", () =>
{
    using var f = new Fixture();
    f.Library.Reserve(f.Other, f.Add());
    Reject(() => f.Library.CancelHold(f.Reader, f.Library.Holds(f.Other)[0].Id));
});
Test("HOLD: Adding a copy serves a waiting reservation", () =>
{
    using var f = new Fixture();
    var id = f.Add();
    f.Library.Issue(f.Admin, id, f.Member.Id);
    f.Library.Reserve(f.Other, id);
    f.Library.AddCopy(f.Admin, id);
    Equal(HoldStatus.Ready, f.Library.Holds(f.Other)[0].Status);
});
Test("CATALOG: Active loans prevent archiving and retirement", () =>
{
    using var f = new Fixture();
    var id = f.Add();
    f.Library.Issue(f.Admin, id, f.Member.Id);
    Reject(() => f.Library.ArchiveBook(f.Admin, id));
    Reject(() => f.Library.RetireCopy(f.Admin, f.Library.Book(id)!.Copies[0].Id));
});
Test("CATALOG: Archiving preserves history and removes availability", () =>
{
    using var f = new Fixture();
    var id = f.Add();
    var loan = f.Library.Issue(f.Admin, id, f.Member.Id);
    f.Library.Return(f.Admin, loan);
    f.Library.ArchiveBook(f.Admin, id);
    True(f.Library.Book(id)is null);
    Equal(0, f.Library.Catalog(new()).Total);
    Equal(1, f.Library.Loans(f.Reader, true).Count);
});
Test("DB: Unique index prevents two active loans on one copy", () =>
{
    using var f = new Fixture();
    var id = f.Add();
    var copy = f.Library.Book(id)!.Copies[0].Id;
    f.Library.Issue(f.Admin, id, f.Member.Id);
    using var db = f.Database.Open();
    RejectSql(() => db.Exec("INSERT INTO Loans(CopyId,MemberId,BorrowedAt,DueAt) VALUES(?,?,?,?)", copy, f.Second.Id, now, now.AddDays(14)));
});
Test("CONCURRENCY: Two borrowers racing for one copy yield one loan", () =>
{
    using var f = new Fixture();
    var id = f.Add();
    var successes = 0;
    Parallel.ForEach(new[] { f.Member.Id, f.Second.Id }, member =>
    {
        try
        {
            f.Library.Issue(f.Admin, id, member);
            Interlocked.Increment(ref successes);
        }
        catch (LibraryException)
        {
        }
    });
    Equal(1, successes);
    Equal(1, f.Library.Loans(f.Admin).Count);
});
Test("CONCURRENCY: Duplicate reservation race yields one hold", () =>
{
    using var f = new Fixture();
    var id = f.Add();
    var successes = 0;
    Parallel.For(0, 4, _ =>
    {
        try
        {
            f.Library.Reserve(f.Reader, id);
            Interlocked.Increment(ref successes);
        }
        catch (LibraryException)
        {
        }
    });
    Equal(1, successes);
    Equal(1, f.Library.Holds(f.Reader).Count);
});
Test("REPORT: Counts and audit reflect committed operations", () =>
{
    using var f = new Fixture();
    f.Library.Issue(f.Admin, f.Add(), f.Member.Id);
    var report = f.Library.Dashboard(f.Admin);
    Equal(1, report.Titles);
    Equal(1, report.ActiveLoans);
    Equal(2, report.Members);
    True(report.Recent.Any(x => x.Action == "Book issued"));
});
Test("STATE: Successful writes publish and subscribers can detach", () =>
{
    using var f = new Fixture();
    var count = 0;
    void Handler() => count++;
    f.Changes.Changed += Handler;
    f.Add();
    f.Changes.Changed -= Handler;
    f.Add();
    Equal(1, count);
});
Test("RESILIENCE: Failed writes do not publish or create records", () =>
{
    using var f = new Fixture();
    var count = 0;
    f.Changes.Changed += () => count++;
    Reject(() => f.Library.Issue(f.Admin, 999, f.Member.Id));
    Equal(0, count);
    Equal(0, f.Library.Loans(f.Admin).Count);
});
Test("PERFORMANCE: Paged catalog over 2,000 titles stays bounded", () =>
{
    using var f = new Fixture();
    f.Database.Write(db =>
    {
        for (int i = 0; i < 2000; i++)
            db.Exec("INSERT INTO Books(Title,Author,Isbn,Category,Year,Description,Shelf,CoverStyle) VALUES(?,?,?,?,?,?,?,?)", $"Book {i:D4}", "Author", $"{i:D13}", "Test", 2026, "", "A", 0);
        return 0;
    });
    var watch = Stopwatch.StartNew();
    var page = f.Library.Catalog(new(Page: 50));
    watch.Stop();
    Equal(8, page.Items.Count);
    Equal(2000, page.Total);
    True(watch.Elapsed < TimeSpan.FromSeconds(3));
    Console.WriteLine($"  2,000-title query: {watch.Elapsed.TotalMilliseconds:F1} ms");
});
var results = new List<object>();
var failed = 0;
var total = Stopwatch.StartNew();
foreach (var(name, body)in tests)
{
    var sw = Stopwatch.StartNew();
    try
    {
        body();
        results.Add(new { name, passed = true, milliseconds = sw.Elapsed.TotalMilliseconds });
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failed++;
        results.Add(new { name, passed = false, error = ex.Message });
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

if (args.Contains("--http"))
{
    var index = Array.IndexOf(args, "--http");
    var baseUrl = args[index + 1];
    async Task HttpTest(string name, Func<Task> body)
    {
        try
        {
            await body();
            results.Add(new { name, passed = true });
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            failed++;
            results.Add(new { name, passed = false, error = ex.Message });
            Console.WriteLine("FAIL " + name + ": " + ex.Message);
        }
    }

    using var handler = new HttpClientHandler
    {
        AllowAutoRedirect = false,
        CookieContainer = new CookieContainer()
    };
    using var client = new HttpClient(handler)
    {
        BaseAddress = new Uri(baseUrl)
    };
    async Task<string> Token(string path)
    {
        var html = await client.GetStringAsync(path);
        return WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    }

    await HttpTest("HTTP: Health endpoint succeeds", async () => Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode));
    await HttpTest("HTTP: Anonymous member data redirects to login", async () => Equal(HttpStatusCode.Redirect, (await client.GetAsync("/members")).StatusCode));
    await HttpTest("HTTP: Catalog is publicly readable", async () => True((await client.GetStringAsync("/catalog")).Contains("The book catalog")));
    await HttpTest("HTTP: POST without antiforgery token returns a helpful 400", async () =>
    {
        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> { { "login", "rasuli" }, { "password", "Rasuli2026!" } }));
        Equal(HttpStatusCode.BadRequest, response.StatusCode);
        True((await response.Content.ReadAsStringAsync()).Contains("Reload the page"));
    });
    await HttpTest("HTTP: Empty sign-in page has no phantom notices", async () =>
    {
        var html = await client.GetStringAsync("/Account/Login");
        True(!html.Contains("class=\"notice "));
    });
    await HttpTest("HTTP: Login feedback is rendered and HTML encoded", async () =>
    {
        var html = await client.GetStringAsync("/Account/Login?error=%3Cscript%3Eexample%3C%2Fscript%3E");
        True(html.Contains("&lt;script&gt;example&lt;/script&gt;"));
        True(!html.Contains("<script>example</script>"));
    });
    await HttpTest("HTTP: Member login sets authenticated cookie", async () =>
    {
        var token = await Token("/Account/Login");
        True(token.Length > 0);
        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> { { "login", "rasuli" }, { "password", "Rasuli2026!" }, { "__RequestVerificationToken", token } }));
        Equal(HttpStatusCode.Redirect, response.StatusCode);
        Equal("/", response.Headers.Location!.ToString());
        True((await client.GetStringAsync("/loans")).Contains("Your bookshelf"));
    });
    await HttpTest("HTTP: Member reserve and renewal buttons are enabled", async () =>
    {
        var book = await client.GetStringAsync("/books/1");
        var reserve = Regex.Match(book, @"<button\b[^>]*>\s*Reserve this book\s*</button>").Value;
        True(reserve.Length > 0 && !reserve.Contains("disabled"));
        var loans = await client.GetStringAsync("/loans");
        var renew = Regex.Match(loans, @"<button\b[^>]*>\s*Renew\s*</button>").Value;
        True(renew.Length > 0 && !renew.Contains("disabled"));
    });
    await HttpTest("HTTP: Member cannot access librarian page or CSV", async () =>
    {
        Equal(HttpStatusCode.Redirect, (await client.GetAsync("/members")).StatusCode);
        Equal(HttpStatusCode.Redirect, (await client.GetAsync("/reports/loans.csv")).StatusCode);
    });
    await HttpTest("HTTP: Blazor browser runtime is served", async () =>
    {
        var response = await client.GetAsync("/_framework/blazor.web.js");
        Equal(HttpStatusCode.OK, response.StatusCode);
        True((await response.Content.ReadAsStringAsync()).Length > 100000);
    });
    await HttpTest("HTTP: Sign out removes access", async () =>
    {
        var token = await Token("/Account/Settings");
        var response = await client.PostAsync("/auth/logout", new FormUrlEncodedContent(new Dictionary<string, string> { { "__RequestVerificationToken", token } }));
        Equal(HttpStatusCode.Redirect, response.StatusCode);
        Equal(HttpStatusCode.Redirect, (await client.GetAsync("/loans")).StatusCode);
    });
    await HttpTest("HTTP: Admin username login enables book editing", async () =>
    {
        var token = await Token("/Account/Login");
        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> { { "login", "admin" }, { "password", "Admin2026!" }, { "__RequestVerificationToken", token } }));
        Equal(HttpStatusCode.Redirect, response.StatusCode);
        Equal("/", response.Headers.Location!.ToString());
        var html = await client.GetStringAsync("/manage/books/new");
        var save = Regex.Match(html, @"<button\b[^>]*>\s*Save book\s*</button>").Value;
        True(save.Length > 0 && !save.Contains("disabled"));
    });
}

if (args.Contains("--browser"))
{
    var browserIndex = Array.IndexOf(args, "--browser");
    var httpIndex = Array.IndexOf(args, "--http");
    if (httpIndex < 0)
        throw new ArgumentException("Browser checks also require --http http://localhost:5080.");
    foreach (var check in await BrowserTests.Run(args[httpIndex + 1], args[browserIndex + 1]))
    {
        results.Add(new { name = check.Name, passed = check.Passed, error = check.Error });
        if (!check.Passed)
            failed++;
    }
}

var report = new
{
    generatedUtc = DateTime.UtcNow,
    passed = results.Count - failed,
    failed,
    totalMilliseconds = total.Elapsed.TotalMilliseconds,
    tests = results
};
var outIndex = Array.IndexOf(args, "--out");
if (outIndex >= 0)
{
    var output = Path.GetFullPath(args[outIndex + 1]);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}

Console.WriteLine($"\n{results.Count - failed}/{results.Count} passed in {total.Elapsed.TotalSeconds:F1}s");
return failed == 0 ? 0 : 1;
sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan amount) => now += amount;
}

sealed class Fixture : IDisposable
{
    public LibraryDatabase Database { get; }
    public TestClock Clock { get; } = new();
    public LibraryChangeBus Changes { get; } = new();
    public AccountService Accounts { get; }
    public ILibraryService Library { get; }
    public Actor Admin { get; }
    public UserAccount Member { get; }
    public UserAccount Second { get; }
    public Actor Reader => new(Member.Id, false);
    public Actor Other => new(Second.Id, false);

    private int sequence;
    public Fixture()
    {
        Database = new(Path.Combine(Path.GetTempPath(), "KnowledgeHub-tests", Guid.NewGuid().ToString("N"), "test.db"));
        Database.Initialize();
        Accounts = new(Database, Clock);
        Library = new LibraryService(Database, Clock, Changes);
        Admin = new(Accounts.Create("Test Librarian", "admin@example.com", "Library2026!", "Librarian").Id, true);
        Member = Accounts.Register("First Reader", "reader@example.com", "Reader2026!");
        Second = Accounts.Register("Second Reader", "second@example.com", "Reader2026!");
    }

    public int Add(string title = "Test Book") => Library.SaveBook(Admin, new Book { Title = title, Author = "Author", Isbn = $"{++sequence:D13}", Category = "Technology", Year = 2026, Shelf = "A-01" });
    public void Dispose()
    { /* Connections are operation-scoped. OS temp cleanup owns isolated test files. */
    }
}
