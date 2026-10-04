using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NetValue.Data;
using NetValue.Components;

var builder = WebApplication.CreateBuilder(args);
var tenantId = builder.Configuration["Authentication:TenantId"]!;
var databaseCommands = new[] { "--migrate-database", "--export-database", "--import-database" };
var selectedCommands = databaseCommands.Where(command => args.Contains(command, StringComparer.Ordinal)).ToList();
if (selectedCommands.Count > 0)
{
    if (selectedCommands.Count != 1) throw new ArgumentException("Use one database command at a time.");
    var storage = new RelationalHouseholdStorage(builder.Environment, builder.Configuration);
    var command = selectedCommands.Single();
    if (command == "--migrate-database")
    {
        storage.Migrate();
        Console.WriteLine("Database schema migrations applied. No financial data or memberships were imported.");
    }
    else
    {
        var index = Array.IndexOf(args, command);
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--")) throw new ArgumentException("Provide a backup file path after the database command.");
        if (command == "--export-database") DatabaseTransfer.Export(storage, args[index + 1]);
        else DatabaseTransfer.Import(storage, args[index + 1]);
        Console.WriteLine(command == "--export-database" ? "Operator backup exported, including household ownership." : "Operator backup imported into the empty database.");
    }
    return;
}
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSingleton<HouseholdRepository>();
builder.Services.AddScoped<HouseholdSession>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/account/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/export")) context.Response.StatusCode = 401;
            else context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = context =>
        {
            if (!long.TryParse(context.Principal?.FindFirstValue("netvalue:expires"), out var expiry) || expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) context.RejectPrincipal();
            return Task.CompletedTask;
        };
    })
    .AddOpenIdConnect(options =>
    {
        options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        options.ClientId = builder.Configuration["Authentication:ClientId"];
        options.ClientSecret = builder.Configuration["Authentication:ClientSecret"];
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.CallbackPath = "/signin-oidc";
        options.SignedOutCallbackPath = "/signout-callback-oidc";
        options.ResponseType = "code";
        options.UsePkce = true;
        options.MapInboundClaims = false;
        options.SaveTokens = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, NameClaimType = "name", RoleClaimType = "roles" };
        options.Events.OnTokenValidated = context =>
        {
            if (context.Principal?.FindFirstValue("tid") != tenantId || !Guid.TryParse(context.Principal.FindFirstValue("oid"), out var objectId) || objectId == Guid.Empty)
                context.Fail("This account is not in the configured directory.");
            else ((ClaimsIdentity)context.Principal.Identity!).AddClaim(new("netvalue:expires", DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds().ToString()));
            return Task.CompletedTask;
        };
        options.Events.OnRemoteFailure = context =>
        {
            context.HandleResponse();
            context.Response.Redirect("/account/sign-in-failed");
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
var app = builder.Build();
if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/account/error"); app.UseHsts(); app.UseHttpsRedirection(); }
else app.Use(async (context, next) =>
{
    if (context.Request.Host.Host == "127.0.0.1")
        context.Response.Redirect($"{context.Request.Scheme}://localhost:{context.Request.Host.Port}{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}");
    else await next(context);
});
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapGet("/account/login", (IConfiguration configuration) =>
{
    if (string.IsNullOrWhiteSpace(configuration["Authentication:ClientSecret"]))
        return Results.Content("<h1>NetValue sign-in setup</h1><p>Set Authentication:ClientSecret in .NET user secrets, then restart. See docs/AUTHENTICATION.md. No financial data is accessible until sign-in is configured.</p>", "text/html");
    return Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme]);
}).AllowAnonymous();
app.MapPost("/account/logout", async (HttpContext context) =>
{
    await context.RequestServices.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>().ValidateRequestAsync(context);
    return Results.SignOut(new AuthenticationProperties { RedirectUri = "/account/signed-out" }, [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
}).RequireAuthorization();
app.MapGet("/account/signed-out", () => Results.Content("<h1>Signed out</h1><a href='/account/login'>Sign in to NetValue</a>", "text/html")).AllowAnonymous();
app.MapGet("/account/sign-in-failed", () => Results.Content("<h1>Sign-in failed</h1><p>Check the app registration, redirect URI, and your directory access.</p><a href='/account/login'>Try again</a>", "text/html")).AllowAnonymous();
app.MapGet("/account/error", () => Results.Problem("The request could not be completed.")).AllowAnonymous();
app.MapGet("/export/excel", (HouseholdRepository repository, HttpContext context) => Export(repository, context, false)).RequireAuthorization();
app.MapGet("/export/backup", (HouseholdRepository repository, HttpContext context) => Export(repository, context, true)).RequireAuthorization();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization();
app.Run();

static IResult Export(HouseholdRepository repository, HttpContext context, bool backup)
{
    context.Response.Headers.CacheControl = "no-store";
    try
    {
        var access = repository.Open(context.User);
        return backup
            ? Results.File(PortfolioBackup.Create(access.Profiles), "application/json", "NetValue-backup.json")
            : Results.File(ExcelExport.Create(access.Profiles), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "NetValue.xlsx");
    }
    catch (UnauthorizedAccessException) { return Results.Forbid(); }
    catch (Exception) { return Results.Problem("The household data could not be exported."); }
}
