using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using KnowledgeHub.Core;

namespace KnowledgeHub.Web.Services;
public sealed class CatalogState
{
    public string Search { get; set; } = "";
    public string Category { get; set; } = "";
    public bool AvailableOnly { get; set; }
    public string Sort { get; set; } = "title";
    public int Page { get; set; } = 1;
    public CatalogQuery Query => new(Search, Category, AvailableOnly, Sort, Page);
}

public sealed class HoldMaintenance(ILibraryService library, ILogger<HoldMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            do
            {
                try
                {
                    library.MaintainHolds();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Hold maintenance failed; will retry.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

public sealed class SessionAuthenticationStateProvider(ILoggerFactory logger, IAccountService accounts) : RevalidatingServerAuthenticationStateProvider(logger)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken token)
    {
        var account = accounts.Get(state.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
        return Task.FromResult(account is not null && account.SecurityStamp == state.User.FindFirstValue("stamp"));
    }
}

public abstract class LibraryPage : ComponentBase, IDisposable
{
    [Inject]
    protected ILibraryService Library { get; set; } = null!;

    [Inject]
    protected AuthenticationStateProvider Authentication { get; set; } = null!;

    [Inject]
    protected LibraryChangeBus Changes { get; set; } = null!;

    [Inject]
    protected ILogger<LibraryPage> Logger { get; set; } = null!;
    protected Actor Actor { get; private set; } = new("", false);
    protected string DisplayName { get; private set; } = "Reader";
    protected string? Message { get; set; }
    protected bool IsError { get; set; }
    protected bool Ready { get; private set; }
    protected bool Busy { get; private set; }

    private bool disposed;
    protected override async Task OnInitializedAsync()
    {
        var user = (await Authentication.GetAuthenticationStateAsync()).User;
        Actor = new(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", user.IsInRole("Librarian"));
        DisplayName = user.Identity?.Name ?? "Reader";
        Changes.Changed += Changed;
        Reload();
        Ready = true;
    }

    protected abstract void Load();
    protected void Reload()
    {
        try
        {
            Load();
        }
        catch (LibraryException ex)
        {
            Message = ex.Message;
            IsError = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Page data load failed");
            Message = "We could not load this page. Please reload and try again.";
            IsError = true;
        }
    }

    protected void Run(Action action, string success)
    {
        if (Busy)
            return;
        Busy = true;
        try
        {
            action();
            Message = success;
            IsError = false;
            Reload();
        }
        catch (LibraryException ex)
        {
            Message = ex.Message;
            IsError = true;
            Reload();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Library action failed");
            Message = "The change could not be saved. Refresh and try again.";
            IsError = true;
        }
        finally
        {
            Busy = false;
        }
    }

    private void Changed() => _ = RefreshAsync();
    private async Task RefreshAsync()
    {
        try
        {
            await InvokeAsync(() =>
            {
                if (!disposed)
                {
                    Reload();
                    StateHasChanged();
                }
            });
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)when (disposed)
        {
        }
    }

    public void Dispose()
    {
        disposed = true;
        Changes.Changed -= Changed;
        GC.SuppressFinalize(this);
    }
}
