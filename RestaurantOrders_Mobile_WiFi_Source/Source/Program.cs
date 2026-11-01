using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Components;
using RestaurantOrders.Data;
using RestaurantOrders.Models;
using RestaurantOrders.Services;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Enable legacy code pages used by ESC/POS printers (e.g. Windows-1253).
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// Επιτρέπει στην εφαρμογή να τρέχει σαν Windows Service (όταν εγκατασταθεί ως τέτοια),
// χωρίς να επηρεάζει καθόλου το κανονικό `dotnet run` σε development.
builder.Host.UseWindowsService();

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=restaurant.db"));

builder.Services.AddSingleton<PrinterService>();
builder.Services.AddHttpClient("Parochos");
builder.Services.AddHttpClient("ePay-WebECR", client =>
{
    // Match the headers observed in the successful Postman request.
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PostmanRuntime/7.53.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.DefaultRequestHeaders.CacheControl =
        new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
    client.DefaultRequestHeaders.Connection.Add("keep-alive");
})
.ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.HttpClientHandler
{
    // Imperva may issue session cookies before allowing API requests.
    // Keep the cookies automatically; never hard-code session cookies from Postman.
    UseCookies = true,
    CookieContainer = new System.Net.CookieContainer(),
    AllowAutoRedirect = true
});
builder.Services.AddScoped<EpayWebEcrService>();
builder.Services.AddHttpClient("Mellon-WebECR");
builder.Services.AddScoped<MellonWebEcrService>();
builder.Services.AddHttpClient("Viva-Cloud-Terminal");
builder.Services.AddScoped<VivaCloudTerminalService>();
builder.Services.AddHttpClient("EDPS-WebECR");
builder.Services.AddScoped<EdpsWebEcrService>();
builder.Services.AddHttpClient("AADE-Registry");
builder.Services.AddSingleton<ParochosService>();
builder.Services.AddTransient<AadeRegistryService>();
builder.Services.AddScoped<FiscalDocumentService>();


builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/";
        options.AccessDeniedPath = "/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Το Blazor Server circuit έχει προεπιλεγμένο όριο μηνύματος 32KB - το μεγαλώνουμε
// ώστε να χωράνε ανεβάσματα αρχείων Excel (μαζική εισαγωγή καταλόγου).
builder.Services.Configure<Microsoft.AspNetCore.SignalR.HubOptions>(options =>
{
    options.MaximumReceiveMessageSize = 15 * 1024 * 1024; // 15MB
});

var app = builder.Build();

// Δημιουργία βάσης + αρχικού admin χρήστη στην πρώτη εκκίνηση
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var db = factory.CreateDbContext();
    DataSeeder.Seed(db);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// ---- Login / Logout endpoints (πλαίσιο εκτός Blazor circuit) ----

async Task SignInUser(HttpContext http, User user)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Name, user.FullName),
        new(ClaimTypes.Role, user.Role.ToString())
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
}

// Σύνδεση διαχειριστή: πατάει την καρτέλα του (μόνο διαχειριστές εμφανίζονται) + δικός του κωδικός.
// Χρησιμοποιείται και για το flow "Προσθήκη χρηστών" μέσω του returnUrl.
app.MapPost("/api/login", async (HttpContext http, IDbContextFactory<AppDbContext> dbFactory) =>
{
    var form = await http.Request.ReadFormAsync();
    var returnUrl = form["returnUrl"].ToString();

    if (!int.TryParse(form["userId"], out var userId))
    {
        return Results.Redirect("/login");
    }
    var code = form["password"].ToString();

    using var db = await dbFactory.CreateDbContextAsync();
    var user = await db.Users.FindAsync(userId);

    var valid = user is not null && user.IsActive && user.Role == Role.Admin
        && (!user.HasCode || PasswordHasher.Verify(code, user.CodeHash!));

    if (!valid)
    {
        var errorReturn = string.IsNullOrWhiteSpace(returnUrl) ? "" : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        return Results.Redirect($"/login?error=1{errorReturn}");
    }

    await SignInUser(http, user!);

    // Ασφαλές μόνο αν οδηγεί σε εσωτερική σελίδα διαχειριστή
    if (!string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith("/admin/"))
    {
        return Results.Redirect(returnUrl);
    }

    return Results.Redirect("/admin/tables");
});

// Σύνδεση για "Πάρτε παραγγελία": εμφανίζονται όλοι οι ενεργοί χρήστες, ο καθένας με τον δικό του κωδικό (αν έχει)
app.MapPost("/api/staff-login", async (HttpContext http, IDbContextFactory<AppDbContext> dbFactory) =>
{
    var form = await http.Request.ReadFormAsync();
    if (!int.TryParse(form["userId"], out var userId))
    {
        return Results.Redirect("/");
    }
    var code = form["pin"].ToString();

    using var db = await dbFactory.CreateDbContextAsync();
    var user = await db.Users.FindAsync(userId);

    var valid = user is not null && user.IsActive
        && (!user.HasCode || PasswordHasher.Verify(code, user.CodeHash!));

    if (!valid)
    {
        return Results.Redirect($"/order-login?pinerror=1&uid={userId}");
    }

    await SignInUser(http, user!);

    return Results.Redirect("/waiter");
});

app.MapPost("/api/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
});

// Εξαγωγή αναφοράς πωλήσεων σε Excel (.xlsx) - μόνο για διαχειριστές
app.MapGet("/api/reports/export", async (HttpContext http, IDbContextFactory<AppDbContext> dbFactory, string? from, string? to) =>
{
    var fromDate = DateTime.TryParse(from, out var f) ? f : DateTime.Today;
    var toDate = DateTime.TryParse(to, out var t) ? t : DateTime.Today;

    using var db = await dbFactory.CreateDbContextAsync();
    var rows = await ReportService.GetSalesReport(db, fromDate, toDate);
    var bytes = ReportService.BuildExcel(rows, fromDate, toDate);

    var fileName = $"anafora-poliseon_{fromDate:yyyy-MM-dd}_{toDate:yyyy-MM-dd}.xlsx";
    return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
})
.RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { Roles = "Admin" });

// Εξαγωγή συγκεκριμένου κλεισίματος ταμείου σε PDF - μόνο για διαχειριστές
app.MapGet("/api/cashier/day-close-pdf", async (HttpContext http, IDbContextFactory<AppDbContext> dbFactory, int? id) =>
{
    if (id is null)
    {
        return Results.BadRequest("Λείπει το id του κλεισίματος.");
    }

    using var db = await dbFactory.CreateDbContextAsync();
    var record = await CashierService.GetClosingById(db, id.Value);
    if (record is null)
    {
        return Results.NotFound();
    }

    var bytes = CashierService.BuildDayClosePdf(record);
    var fileName = $"kleisimo-tameiou_{record.ClosedAt:yyyy-MM-dd_HHmm}.pdf";
    return Results.File(bytes, "application/pdf", fileName);
})
.RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { Roles = "Admin" });

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
