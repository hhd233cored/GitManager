using System.Security.Claims;
using GitManager.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 12 * 1024 * 1024);

var databasePath = Path.GetFullPath(builder.Configuration["DatabasePath"] ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitManager", "server.db"));
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
var connectionString = new SqliteConnectionStringBuilder
{
    DataSource = databasePath,
    ForeignKeys = true,
    DefaultTimeout = 10
}.ToString();
builder.Services.AddDbContext<CloudDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddIdentityApiEndpoints<IdentityUser>(options => options.User.RequireUniqueEmail = true)
    .AddEntityFrameworkStores<CloudDbContext>();
builder.Services.AddAuthorization();

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<CloudDbContext>().Database.EnsureCreatedAsync();

app.MapGroup("/api/auth").MapIdentityApi<IdentityUser>();
var account = app.MapGroup("/api").RequireAuthorization(policy => policy
    .AddAuthenticationSchemes(IdentityConstants.BearerScheme)
    .RequireAuthenticatedUser()
    .RequireClaim(ClaimTypes.NameIdentifier));
account.MapCloudEndpoints();
await app.RunAsync();
