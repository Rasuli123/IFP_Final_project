using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

// Real Chromium checks use an isolated profile, never the user's browser session.
static class BrowserTests
{
    internal sealed record Result(string Name, bool Passed, string? Error = null);
    public static async Task<List<Result>> Run(string baseUrl, string executable)
    {
        var results = new List<Result>();
        async Task Check(string name, Func<Task> action)
        {
            try
            {
                await action();
                results.Add(new(name, true));
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                results.Add(new(name, false, ex.Message));
                Console.WriteLine("FAIL " + name + ": " + ex.Message);
                throw;
            }
        }

        var suffix = DateTime.UtcNow.Ticks.ToString()[^10..];
        var username = "reader" + suffix;
        var title = "Browser Test " + suffix;
        var bookPath = "";
        try
        {
            await using var admin = await Browser.Start(executable, baseUrl);
            await using var member = await Browser.Start(executable, baseUrl);
            await Check("BROWSER: Admin username login and interactive connection", async () =>
            {
                await admin.Login("admin", "Admin2026!");
                await admin.Wait("document.querySelector('.profile')?.textContent.includes('Admin')");
            });
            await Check("BROWSER: Search filters the catalog through Blazor", async () =>
            {
                await admin.Go("/catalog");
                await admin.Fill("#book-search", "Clean Code");
                await admin.Click("Search");
                await admin.Wait("document.querySelectorAll('.book-tile').length === 1 && document.querySelector('.book-tile').textContent.includes('Clean Code')");
                await admin.Click("Clear filters");
                await admin.Wait("document.querySelectorAll('.book-tile').length > 1");
            });
            await Check("BROWSER: Admin creates a member", async () =>
            {
                await admin.Go("/members");
                await admin.Click("＋ Add member");
                await admin.Wait("!!document.querySelector('#member-name')");
                await admin.Fill("#member-name", "Browser Reader " + suffix);
                await admin.Fill("#member-username", username);
                await admin.Fill("#member-email", username + "@example.com");
                await admin.Fill("#member-password", "Reader2026!");
                await admin.Click("Create member");
                await admin.Wait("document.querySelector('tbody')?.textContent.includes(" + J(username) + ")");
            });
            await Check("BROWSER: Admin creates and edits a book", async () =>
            {
                await admin.Go("/manage/books/new");
                await admin.FillLabel("Title", title);
                await admin.FillLabel("Author", "Browser Author");
                await admin.FillLabel("ISBN (13 digits)", "987" + suffix);
                await admin.FillLabel("Category", "Testing");
                await admin.FillLabel("Shelf location", "QA-01");
                await admin.FillLabel("Description", "Created by the browser test.");
                await admin.Click("Save book");
                await admin.Wait("location.pathname.startsWith('/books/') && document.querySelector('h1')?.textContent.includes(" + J(title) + ")");
                bookPath = (await admin.Eval("location.pathname")).GetString()!;
                await admin.Click("Edit book");
                await admin.Wait("location.pathname.startsWith('/manage/books/') && !!document.querySelector('textarea')");
                await admin.FillLabel("Description", "Updated in the browser.");
                await admin.Click("Save book");
                await admin.Wait("document.querySelector('.description')?.textContent === 'Updated in the browser.'");
            });
            await Check("BROWSER: Member reservation updates the admin session live", async () =>
            {
                await member.Login(username, "Reader2026!");
                await member.Go(bookPath);
                await member.Click("Reserve this book");
                await member.Wait("document.querySelector('.notice.success')?.textContent.includes('Reservation placed')");
                await admin.Wait("document.querySelector('tbody')?.textContent.includes('On hold')");
                await member.Go("/reservations");
                await member.Wait("document.querySelector('tbody')?.textContent.includes('Ready for pickup')");
            });
            await Check("BROWSER: Admin issues a reserved copy", async () =>
            {
                await admin.Go("/reservations");
                await admin.ClickRow(title, "Issue copy");
                await member.Wait("!document.querySelector('tbody')?.textContent.includes(" + J(title) + ")");
                await member.Go("/loans");
                await member.Wait("document.querySelector('tbody')?.textContent.includes(" + J(title) + ")");
            });
            await Check("BROWSER: Member renews once and admin records return", async () =>
            {
                await member.ClickRow(title, "Renew");
                await member.Wait("document.querySelector('tbody')?.textContent.includes('Renewed once')");
                await admin.Go("/loans");
                await admin.ClickRow(title, "Return");
                await member.Wait("!document.querySelector('tbody')?.textContent.includes(" + J(title) + ")");
                await member.Click("Return history");
                await member.Wait("document.querySelector('tbody')?.textContent.includes(" + J(title) + ")");
            });
            await Check("BROWSER: Member cancels a reservation", async () =>
            {
                await member.Go(bookPath);
                await member.Click("Reserve this book");
                await member.Wait("document.querySelector('.notice.success')?.textContent.includes('Reservation placed')");
                await member.Go("/reservations");
                await member.ClickRow(title, "Cancel");
                await member.Wait("!document.querySelector('tbody')?.textContent.includes(" + J(title) + ")");
            });
            await Check("BROWSER: Admin adds and retires a copy, then archives the title", async () =>
            {
                await admin.Go(bookPath);
                await admin.Click("＋ Add copy");
                await admin.Wait("document.querySelectorAll('tbody tr').length === 2");
                await admin.Click("Retire copy");
                await admin.Wait("document.querySelector('tbody')?.textContent.includes('Retired')");
                await admin.Click("Archive title");
                await admin.Wait("!!document.querySelector('.danger-button')");
                await admin.Click("Confirm archive");
                await admin.Wait("location.pathname === '/catalog'");
                await admin.Fill("#book-search", title);
                await admin.Click("Search");
                await admin.Wait("document.body.textContent.includes('No matching books')");
            });
            await Check("BROWSER: Member cannot open administrator screens", async () =>
            {
                await member.Go("/members", interactive: false);
                await member.Wait("document.body.textContent.includes('does not have permission') || document.body.textContent.includes('Access restricted')");
            });
            await Check("BROWSER: Public registration and password change", async () =>
            {
                await member.Go("/Account/Register", interactive: false);
                await member.Fill("#name", "Registered Reader");
                await member.Fill("#username", "signup" + suffix);
                await member.Fill("#email", "signup" + suffix + "@example.com");
                await member.Fill("#password", "Reader2026!");
                await member.Fill("#confirm", "Reader2026!");
                await member.Click("Create member account →");
                await member.Wait("location.pathname === '/' && !!document.querySelector('.profile')");
                await member.Go("/Account/Settings", interactive: false);
                await member.Fill("#current", "Reader2026!");
                await member.Fill("#password", "Updated2026!");
                await member.Fill("#confirm", "Updated2026!");
                await member.Click("Update password");
                await member.Wait("location.pathname === '/Account/Login'");
                await member.Login("signup" + suffix, "Updated2026!");
            });
            await Check("BROWSER: Reports and authorized CSV export", async () =>
            {
                await admin.Go("/reports");
                await admin.Wait("document.body.textContent.includes('Collection by category')");
                var csv = await admin.Eval("fetch('/reports/loans.csv').then(async r => ({status:r.status,text:await r.text()}))");
                if (csv.GetProperty("status").GetInt32() != 200 || !csv.GetProperty("text").GetString()!.Contains(title))
                    throw new Exception("The CSV should contain the returned test loan.");
            });
            await Check("BROWSER: Desktop, tablet and phone layouts fit the viewport", async () =>
            {
                foreach (var width in new[]
                {
                    1366,
                    820,
                    390
                }

                )
                {
                    await admin.Command("Emulation.setDeviceMetricsOverride", new { width, height = 900, deviceScaleFactor = 1, mobile = false });
                    foreach (var route in new[]
                    {
                        "/",
                        "/catalog",
                        "/members",
                        "/loans",
                        "/reservations",
                        "/reports"
                    }

                    )
                    {
                        await admin.Go(route);
                        await admin.Wait("document.documentElement.scrollWidth <= innerWidth + 1");
                    }
                }
            });
            await Check("BROWSER: Keyboard focus and Rasuli username login", async () =>
            {
                await member.Login("rasuli", "Rasuli2026!");
                await member.Command("Input.dispatchKeyEvent", new { type = "keyDown", key = "Tab", code = "Tab", windowsVirtualKeyCode = 9 });
                await member.Command("Input.dispatchKeyEvent", new { type = "keyUp", key = "Tab", code = "Tab", windowsVirtualKeyCode = 9 });
                await member.Wait("document.activeElement?.tagName !== 'BODY'");
                await member.Wait("document.querySelector('.profile')?.textContent.includes('Rasuli')");
                var directory = Environment.GetEnvironmentVariable("KNOWLEDGEHUB_QA_SCREENSHOTS");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    await member.Screenshot(Path.Combine(directory, "member-desktop.png"));
                    await admin.Screenshot(Path.Combine(directory, "admin-phone.png"));
                }
            });
        }
        catch (Exception ex)
        {
            if (results.Count == 0 || results[^1].Passed)
            {
                results.Add(new("BROWSER: Start isolated browser", false, ex.Message));
                Console.WriteLine("FAIL BROWSER: Start isolated browser: " + ex.Message);
            }
        }

        return results;
    }

    private static string J(string value) => JsonSerializer.Serialize(value);
    private sealed class Browser(Process process, string profile, string baseUrl) : IAsyncDisposable
    {
        private readonly ClientWebSocket socket = new();
        private int sequence;
        public static async Task<Browser> Start(string executable, string baseUrl)
        {
            if (!File.Exists(executable))
                throw new FileNotFoundException("Pass the installed Chrome or Edge executable to --browser.", executable);
            var profile = Path.Combine(Path.GetTempPath(), "KnowledgeHub-browser-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
            {
                "--headless=new",
                "--disable-gpu",
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-background-networking",
                "--remote-debugging-port=0",
                "--user-data-dir=" + profile,
                "--window-size=1366,900",
                "about:blank"
            }

            )
                start.ArgumentList.Add(argument);
            var browser = new Browser(Process.Start(start) ?? throw new Exception("Browser did not start."), profile, baseUrl.TrimEnd('/'));
            try
            {
                var portFile = Path.Combine(profile, "DevToolsActivePort");
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!File.Exists(portFile) && DateTime.UtcNow < deadline)
                    await Task.Delay(100);
                var port = (await File.ReadAllLinesAsync(portFile))[0];
                using var http = new HttpClient();
                var tabs = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list"));
                var page = tabs.RootElement.EnumerateArray().First(tab => tab.GetProperty("type").GetString() == "page");
                await browser.socket.ConnectAsync(new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!), CancellationToken.None);
                await browser.Command("Page.enable", new { });
                // Observe the actual Blazor socket instead of assuming HTML means interactivity.
                await browser.Command("Page.addScriptToEvaluateOnNewDocument", new { source = "window.__hubConnected=false; const OriginalSocket=window.WebSocket; window.WebSocket=class extends OriginalSocket { constructor(...args){super(...args);this.addEventListener('open',()=>window.__hubConnected=true);} };" });
                return browser;
            }
            catch
            {
                await browser.DisposeAsync();
                throw;
            }
        }

        public async Task<JsonElement> Command(string method, object parameters)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var id = ++sequence;
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
            await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, timeout.Token);
            var buffer = new byte[16384];
            while (true)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult fragment;
                do
                {
                    fragment = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                    if (fragment.MessageType == WebSocketMessageType.Close)
                        throw new Exception("Browser connection closed.");
                    message.Write(buffer, 0, fragment.Count);
                }
                while (!fragment.EndOfMessage);
                using var document = JsonDocument.Parse(message.ToArray());
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
                    continue;
                if (root.TryGetProperty("error", out var error))
                    throw new Exception(error.ToString());
                return root.GetProperty("result").Clone();
            }
        }

        public async Task<JsonElement> Eval(string expression)
        {
            var response = await Command("Runtime.evaluate", new { expression, awaitPromise = true, returnByValue = true });
            if (response.TryGetProperty("exceptionDetails", out var error))
                throw new Exception(error.ToString());
            return response.GetProperty("result").TryGetProperty("value", out var value) ? value.Clone() : default;
        }

        public async Task Wait(string condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if ((await Eval("Boolean(" + condition + ")")).ValueKind == JsonValueKind.True)
                        return;
                }
                catch (Exception ex)when (ex.Message.Contains("context", StringComparison.OrdinalIgnoreCase))
                {
                }

                await Task.Delay(100);
            }

            var body = await Eval("document.body?.innerText?.slice(0,1800)");
            throw new Exception("Timed out: " + condition + "\n" + body);
        }

        public async Task Go(string path, bool interactive = true)
        {
            await Command("Page.navigate", new { url = baseUrl + path });
            await Task.Delay(200);
            await Wait("document.readyState === 'complete'");
            if (interactive)
                await Wait("window.__hubConnected === true");
        }

        public async Task Login(string username, string password)
        {
            await Go("/Account/Login", interactive: false);
            await Fill("#login", username);
            await Fill("#password", password);
            await Click("Sign in →");
            await Wait("location.pathname === '/' && !!document.querySelector('.profile')");
            await Wait("window.__hubConnected === true");
        }

        public Task Fill(string selector, string value) => Eval("(()=>{const e=document.querySelector(" + J(selector) + ");if(!e)throw Error('Input missing');e.value=" + J(value) + ";e.dispatchEvent(new Event('input',{bubbles:true}));e.dispatchEvent(new Event('change',{bubbles:true}));})()");
        public Task FillLabel(string label, string value) => Eval("(()=>{const e=[...document.querySelectorAll('label')].find(x=>x.textContent.trim()===" + J(label) + ")?.querySelector('input,textarea,select');if(!e)throw Error('Label missing: '+" + J(label) + ");e.value=" + J(value) + ";e.dispatchEvent(new Event('input',{bubbles:true}));e.dispatchEvent(new Event('change',{bubbles:true}));})()");
        public Task Click(string text) => Eval("(()=>{const e=[...document.querySelectorAll('button,a')].find(x=>x.textContent.trim()===" + J(text) + ");if(!e||e.disabled)throw Error('Action unavailable: '+" + J(text) + ");e.click();})()");
        public Task ClickRow(string title, string action) => Eval("(()=>{const row=[...document.querySelectorAll('tbody tr')].find(x=>x.textContent.includes(" + J(title) + "));const e=[...(row?.querySelectorAll('button')??[])].find(x=>x.textContent.trim()===" + J(action) + ");if(!e)throw Error('Row action missing');e.click();})()");
        public async Task Screenshot(string path)
        {
            var result = await Command("Page.captureScreenshot", new { format = "png", captureBeyondViewport = false });
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(result.GetProperty("data").GetString()!));
        }

        public async ValueTask DisposeAsync()
        {
            socket.Dispose();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process.Dispose();
            // Delete only the isolated profile created by this test.
            var full = Path.GetFullPath(profile);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("KnowledgeHub-browser-", StringComparison.Ordinal))
                try
                {
                    Directory.Delete(full, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
        }
    }
}
