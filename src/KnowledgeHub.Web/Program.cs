using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using KnowledgeHub.Core;
using KnowledgeHub.Infrastructure;
using KnowledgeHub.Web.Components;
using KnowledgeHub.Web.Services;

var builder = WebApplication.CreateBuilder(args);
// Persistent records and cookie keys stay outside generated build folders.
var dataDirectory = builder.Configuration["KnowledgeHub:DataDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDirectory);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys"))).SetApplicationName("KnowledgeHub");
builder.Services.AddSingleton(new LibraryDatabase(Path.Combine(dataDirectory, "knowledgehub.db")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LibraryChangeBus>();
builder.Services.AddSingleton<AccountService>();
builder.Services.AddSingleton<IAccountService>(p => p.GetRequiredService<AccountService>());
builder.Services.AddSingleton<ILibraryService, LibraryService>();
builder.Services.AddHostedService<HoldMaintenance>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, SessionAuthenticationStateProvider>();
builder.Services.AddScoped<CatalogState>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/denied";
    options.Cookie.Name = "KnowledgeHub.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.Events.OnValidatePrincipal = context =>
    {
        var user = context.HttpContext.RequestServices.GetRequiredService<IAccountService>().Get(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
        if (user is null || user.SecurityStamp != context.Principal?.FindFirstValue("stamp"))
            context.RejectPrincipal();
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("accounts", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
var database = app.Services.GetRequiredService<LibraryDatabase>();
database.Initialize();
if (builder.Environment.IsDevelopment() && builder.Configuration.GetValue("KnowledgeHub:DemoData", true))
    DemoSeed.Initialize(database, app.Services.GetRequiredService<AccountService>(), app.Services.GetRequiredService<ILibraryService>(), TimeProvider.System);
else
{
    using var db = database.Open();
    if (db.Scalar("SELECT COUNT(*) FROM Users WHERE Role='Librarian'") == 0)
    {
        var email = builder.Configuration["KnowledgeHub:AdminEmail"] ?? throw new InvalidOperationException("Set KnowledgeHub__AdminEmail for the first production start.");
        var password = builder.Configuration["KnowledgeHub:AdminPassword"] ?? throw new InvalidOperationException("Set KnowledgeHub__AdminPassword for the first production start.");
        app.Services.GetRequiredService<AccountService>().Create("Admin", email, password, "Librarian", "admin");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    try
    {
        await next();
    }
    catch (AntiforgeryValidationException)when (!context.Response.HasStarted)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("This form has expired or could not be verified. Reload the page and try again.");
    }
});
app.UseStaticFiles();
app.MapStaticAssets();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();
app.MapPost("/auth/login", async (HttpContext context, IAntiforgery anti, IAccountService accounts) =>
{
    await anti.ValidateRequestAsync(context);
    var form = await context.Request.ReadFormAsync();
    var user = accounts.Authenticate(form["login"].ToString(), form["password"].ToString());
    if (user is null)
        return Results.LocalRedirect("/Account/Login?error=" + Uri.EscapeDataString("Username/email or password is incorrect, or the account is temporarily locked."));
    await SignIn(context, user);
    return Results.LocalRedirect("/");
}).RequireRateLimiting("accounts");
app.MapPost("/auth/register", async (HttpContext context, IAntiforgery anti, IAccountService accounts) =>
{
    await anti.ValidateRequestAsync(context);
    var form = await context.Request.ReadFormAsync();
    try
    {
        if (form["password"] != form["confirm"])
            throw new LibraryException("The passwords do not match.");
        var user = accounts.Register(form["name"].ToString(), form["email"].ToString(), form["password"].ToString(), form["username"].ToString());
        await SignIn(context, user);
        return Results.LocalRedirect("/");
    }
    catch (LibraryException ex)
    {
        return Results.LocalRedirect("/Account/Register?error=" + Uri.EscapeDataString(ex.Message));
    }
}).RequireRateLimiting("accounts");
app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery anti) =>
{
    await anti.ValidateRequestAsync(context);
    await context.SignOutAsync();
    return Results.LocalRedirect("/Account/Login");
}).RequireAuthorization();
app.MapPost("/auth/password", async (HttpContext context, IAntiforgery anti, IAccountService accounts) =>
{
    await anti.ValidateRequestAsync(context);
    var form = await context.Request.ReadFormAsync();
    try
    {
        if (form["password"] != form["confirm"])
            throw new LibraryException("The new passwords do not match.");
        accounts.ChangePassword(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, form["current"].ToString(), form["password"].ToString());
        await context.SignOutAsync();
        return Results.LocalRedirect("/Account/Login?message=Password%20changed.%20Please%20sign%20in%20again.");
    }
    catch (LibraryException ex)
    {
        return Results.LocalRedirect("/Account/Settings?error=" + Uri.EscapeDataString(ex.Message));
    }
}).RequireAuthorization().RequireRateLimiting("accounts");
app.MapGet("/health", () => Results.Json(new { status = "ok" }));
app.MapGet("/reports/loans.csv", (HttpContext context, ILibraryService library) =>
{
    var actor = new Actor(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, true);
    static string Csv(string value) => "\"" + ((value.Length > 0 && "=+-@\t\r".Contains(value[0])) ? "'" : "") + value.Replace("\"", "\"\"") + "\"";
    var lines = library.Loans(actor, true).Select(l => string.Join(',', l.Id, Csv(l.Title), Csv(l.Barcode), Csv(l.MemberName), l.BorrowedAt.ToString("O"), l.DueAt.ToString("O"), l.ReturnedAt?.ToString("O")));
    return Results.File(System.Text.Encoding.UTF8.GetBytes("Loan,Title,Copy,Member,BorrowedUTC,DueUTC,ReturnedUTC\r\n" + string.Join("\r\n", lines)), "text/csv", "knowledgehub-loans.csv");
}).RequireAuthorization(p => p.RequireRole("Librarian"));
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
static Task SignIn(HttpContext context, UserAccount user)
{
    // Cookies contain identity claims; passwords never enter the browser session.
    Claim[] claims = [new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Name, user.Name), new(ClaimTypes.Email, user.Email), new(ClaimTypes.Role, user.Role), new("stamp", user.SecurityStamp)];
    return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
}
