using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using KnowledgeHub.Core;

namespace KnowledgeHub.Infrastructure;
public sealed class AccountService(LibraryDatabase database, TimeProvider clock) : IAccountService
{
    private readonly PasswordHasher<string> hasher = new();
    private readonly string dummyHash = new PasswordHasher<string>().HashPassword("dummy", Guid.NewGuid().ToString());
    // Public registration can only create a member, never an administrator.
    public UserAccount Register(string name, string email, string password, string? username = null) => Create(name, email, password, "Member", username);
    public UserAccount Create(string name, string email, string password, string role, string? username = null)
    {
        name = name.Trim();
        email = email.Trim().ToLowerInvariant();
        if (name.Length is < 2 or > 80)
            throw new LibraryException("Name must contain 2–80 characters.");
        if (email.Length > 200 || !new EmailAddressAttribute().IsValid(email))
            throw new LibraryException("Enter a valid email address.");
        username = (username ?? email.Split('@')[0]).Trim().ToLowerInvariant();
        if (!Regex.IsMatch(username, "^[a-z0-9][a-z0-9._-]{2,29}$", RegexOptions.CultureInvariant))
            throw new LibraryException("Use a username of 3–30 letters, numbers, dots, underscores, or hyphens.");
        ValidatePassword(password);
        if (role is not ("Member" or "Librarian"))
            throw new ArgumentException("Unknown role.");
        var id = Guid.NewGuid().ToString("N");
        var stamp = Guid.NewGuid().ToString("N");
        var hash = hasher.HashPassword(id, password);
        return database.Write(db =>
        {
            if (db.Scalar("SELECT COUNT(*) FROM Users WHERE Email=?", email) != 0)
                throw new LibraryException("An account with this email already exists.");
            if (db.Scalar("SELECT COUNT(*) FROM Users WHERE Username=? COLLATE NOCASE", username) != 0)
                throw new LibraryException("This username is already taken.");
            db.Exec("INSERT INTO Users(Id,Name,Username,Email,PasswordHash,Role,Stamp) VALUES(?,?,?,?,?,?,?)", id, name, username, email, hash, role, stamp);
            return new UserAccount(id, name, username, email, role, stamp);
        });
    }

    public UserAccount? Authenticate(string login, string password)
    {
        if (login.Length > 200 || password.Length > 128)
            return null;
        using var read = database.Open();
        var initial = read.Query("SELECT * FROM Users WHERE Email=? OR Username=? COLLATE NOCASE", login.Trim(), login.Trim()).FirstOrDefault();
        // Verify a dummy hash too, so unknown accounts still do hashing work.
        var result = hasher.VerifyHashedPassword(initial?.Text("Id") ?? "dummy", initial?.Text("PasswordHash") ?? dummyHash, password);
        if (initial is null)
            return null;
        return database.Write(db =>
        {
            var row = db.Query("SELECT * FROM Users WHERE Id=?", initial.Text("Id")).Single();
            if (row.NullableDate("LockedUntil") > clock.GetUtcNow().UtcDateTime)
                return null;
            if (result == PasswordVerificationResult.Failed || row.Text("PasswordHash") != initial.Text("PasswordHash"))
            {
                var failures = row.Int("FailedLogins") + 1;
                db.Exec("UPDATE Users SET FailedLogins=?,LockedUntil=? WHERE Id=?", failures >= 5 ? 0 : failures, failures >= 5 ? clock.GetUtcNow().UtcDateTime.AddMinutes(5) : null, row.Text("Id"));
                return null;
            }

            db.Exec("UPDATE Users SET FailedLogins=0,LockedUntil=NULL WHERE Id=?", row.Text("Id"));
            if (result == PasswordVerificationResult.SuccessRehashNeeded)
                db.Exec("UPDATE Users SET PasswordHash=? WHERE Id=?", hasher.HashPassword(row.Text("Id"), password), row.Text("Id"));
            return Map(row);
        });
    }

    public UserAccount? Get(string id)
    {
        using var db = database.Open();
        var row = db.Query("SELECT * FROM Users WHERE Id=?", id).FirstOrDefault();
        return row is null ? null : Map(row);
    }

    public void ChangePassword(string id, string currentPassword, string newPassword)
    {
        ValidatePassword(newPassword);
        database.Write(db =>
        {
            var row = db.Query("SELECT * FROM Users WHERE Id=?", id).FirstOrDefault() ?? throw new LibraryException("Account not found.");
            if (currentPassword.Length > 128 || hasher.VerifyHashedPassword(id, row.Text("PasswordHash"), currentPassword) == PasswordVerificationResult.Failed)
                throw new LibraryException("The current password is incorrect.");
            db.Exec("UPDATE Users SET PasswordHash=?,Stamp=? WHERE Id=?", hasher.HashPassword(id, newPassword), Guid.NewGuid().ToString("N"), id);
            return 0;
        });
    }

    private static UserAccount Map(Row r) => new(r.Text("Id"), r.Text("Name"), r.Text("Username"), r.Text("Email"), r.Text("Role"), r.Text("Stamp"));
    private static void ValidatePassword(string value)
    {
        if (value.Length is < 10 or > 128 || !value.Any(char.IsUpper) || !value.Any(char.IsLower) || !value.Any(char.IsDigit))
            throw new LibraryException("Use 10–128 characters, including uppercase, lowercase, and a number.");
    }
}
