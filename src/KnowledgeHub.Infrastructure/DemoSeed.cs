using KnowledgeHub.Core;

namespace KnowledgeHub.Infrastructure;
public static class DemoSeed
{
    public static void Initialize(LibraryDatabase database, AccountService accounts, ILibraryService library, TimeProvider clock)
    {
        using var db = database.Open();
        if (db.Scalar("SELECT COUNT(*) FROM Users") > 0)
            return;
        // These two accounts are for a fresh local demonstration only.
        var admin = accounts.Create("Admin", "admin@knowledgehub.local", "Admin2026!", "Librarian", "admin");
        var first = accounts.Register("Rasuli", "rasuli@knowledgehub.local", "Rasuli2026!", "rasuli");
        var actor = new Actor(admin.Id, true);
        (string Title, string Author, string Isbn, string Category, int Year, string Description)[] books =
        [
            ("The Design of Everyday Things", "Don Norman", "9780465050659", "Design", 2013, "An exploration of how thoughtful design makes everyday objects understandable and useful."),
            ("Clean Code", "Robert C. Martin", "9780132350884", "Technology", 2008, "Practical perspectives on naming, functions, classes, and maintaining readable software."),
            ("Atomic Habits", "James Clear", "9780735211292", "Personal growth", 2018, "An accessible guide to building routines through small, consistent changes."),
            ("The Midnight Library", "Matt Haig", "9780525559474", "Fiction", 2020, "A novel about possibilities, decisions, and the lives we imagine we could have lived."),
            ("A Brief History of Time", "Stephen Hawking", "9780553380163", "Science", 1998, "An introduction to questions about time, the universe, and the laws of physics."),
            ("The Pragmatic Programmer", "David Thomas & Andrew Hunt", "9780135957059", "Technology", 2019, "Approaches to software craftsmanship, practical problem solving, and professional growth."),
            ("Thinking, Fast and Slow", "Daniel Kahneman", "9780374533557", "Psychology", 2011, "An examination of intuitive thinking, deliberate reasoning, and human judgment."),
            ("The Hobbit", "J. R. R. Tolkien", "9780547928227", "Fiction", 2012, "Bilbo Baggins leaves his quiet home for an unexpected journey through Middle-earth."),
            ("Deep Work", "Cal Newport", "9781455586691", "Personal growth", 2016, "Ideas for focused work and protecting attention in a distracted world."),
            ("The Elements of Typographic Style", "Robert Bringhurst", "9780881792126", "Design", 2012, "A study of typography, page composition, and the relationship between language and form."),
            ("Cosmos", "Carl Sagan", "9780345539434", "Science", 2013, "A journey through scientific discovery and humanity's place in the universe."),
            ("The Psychology of Money", "Morgan Housel", "9780857197689", "Psychology", 2020, "Stories about behavior, uncertainty, and how people relate to money.")
        ];
        for (var i = 0; i < books.Length; i++)
        {
            var b = books[i];
            library.SaveBook(actor, new Book { Title = b.Title, Author = b.Author, Isbn = b.Isbn, Category = b.Category, Year = b.Year, Description = b.Description, Shelf = $"{(char)('A' + i % 4)}-{i + 1:D2}", CoverStyle = i % 6 }, i == 1 ? 1 : 2);
        }

        library.Issue(actor, 2, first.Id);
        library.Issue(actor, 4, first.Id);
        library.Reserve(new Actor(first.Id, false), 7);
    }
}
