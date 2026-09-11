using EnvironmentalMonitor.Data;
using EnvironmentalMonitor.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ----- SQLite database location -----
// On Azure App Service, the deployed app root can be overwritten on every
// redeploy, but the "HOME" environment variable (set automatically by Azure)
// points at a persistent storage location that survives redeploys/restarts.
// Locally, we just use an App_Data folder at the project root.
var azureHome = Environment.GetEnvironmentVariable("HOME");
var isAzure = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"));
var appDataDir = isAzure && !string.IsNullOrEmpty(azureHome)
    ? Path.Combine(azureHome, "App_Data")
    : Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(appDataDir);
var dbPath = Path.Combine(appDataDir, "sensor_log.db");

// Add services to the container.
builder.Services.AddControllersWithViews();

// SensorDataService is a singleton, so we register a DbContextFactory rather
// than a scoped DbContext directly - the factory lets the singleton create a
// short-lived context per operation instead of sharing one long-lived,
// non-thread-safe context across every request.
builder.Services.AddDbContextFactory<SensorDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddSingleton<SensorDataService>();
builder.Services.AddSingleton<AlertsService>();
builder.Services.AddSingleton<SmsService>();
builder.Services.AddSingleton<ReportingService>();
builder.Services.AddHostedService<AlertMonitorService>();

var app = builder.Build();

// ----- Database setup + one-time legacy CSV import -----
// Ensures the SQLite database/table exist, then (only the very first time,
// when the Readings table is empty) imports any historical data found in the
// old CSV log file so existing sensor history isn't lost by the switch to
// SQLite as the data store.
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SensorDbContext>>();
    using var db = dbFactory.CreateDbContext();
    db.Database.EnsureCreated();

    // Lightweight schema evolution: EnsureCreated() only creates the schema the
    // very first time the database file doesn't exist yet, so adding a new
    // property to SensorReading (Co2) won't automatically apply to an
    // already-existing database file. Since this project intentionally doesn't
    // use EF Core Migrations (see ARCHITECTURE.md), add the column manually via
    // raw SQL if it's missing, so pre-existing databases pick up the new column
    // without a full migrations setup or losing existing data.
    var hasCo2Column = db.Database
        .SqlQueryRaw<string>("SELECT name FROM pragma_table_info('Readings') WHERE name = 'Co2'")
        .ToList()
        .Count > 0;
    if (!hasCo2Column)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Readings ADD COLUMN Co2 REAL NULL");
    }

    if (!db.Readings.Any())

    {
        var legacyCsvPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "data", "sensor_log.csv");
        SensorDataService.ImportCsv(legacyCsvPath, db);
    }

    // Same "no EF migrations" schema-evolution approach as the Co2 column above:
    // the Reports feature's tables were added after EnsureCreated() first ran on
    // existing deployments, so create them manually via raw SQL if missing.
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ReportConversations (
            Id INTEGER NOT NULL CONSTRAINT PK_ReportConversations PRIMARY KEY AUTOINCREMENT,
            Title TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );");
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ReportMessages (
            Id INTEGER NOT NULL CONSTRAINT PK_ReportMessages PRIMARY KEY AUTOINCREMENT,
            ConversationId INTEGER NOT NULL,
            Role TEXT NOT NULL,
            Content TEXT NOT NULL,
            Timestamp TEXT NOT NULL,
            CONSTRAINT FK_ReportMessages_ReportConversations_ConversationId
                FOREIGN KEY (ConversationId) REFERENCES ReportConversations (Id) ON DELETE CASCADE
        );");
    db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_ReportMessages_ConversationId ON ReportMessages (ConversationId);");
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS GeneratedReports (
            Id INTEGER NOT NULL CONSTRAINT PK_GeneratedReports PRIMARY KEY AUTOINCREMENT,
            ConversationId INTEGER NOT NULL,
            FileName TEXT NOT NULL,
            Content TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            CONSTRAINT FK_GeneratedReports_ReportConversations_ConversationId
                FOREIGN KEY (ConversationId) REFERENCES ReportConversations (Id) ON DELETE CASCADE
        );");
    db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_GeneratedReports_ConversationId ON GeneratedReports (ConversationId);");

    // Same "no EF migrations" schema-evolution approach as above: the SMS
    // Alerts feature's table was added after EnsureCreated() first ran on
    // existing deployments, so create it manually via raw SQL if missing.
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS SmsAlerts (
            Id INTEGER NOT NULL CONSTRAINT PK_SmsAlerts PRIMARY KEY AUTOINCREMENT,
            ConversationId INTEGER NULL,
            PhoneNumber TEXT NOT NULL,
            Sensor TEXT NOT NULL,
            Metric TEXT NOT NULL,
            Comparator TEXT NOT NULL,
            Threshold REAL NOT NULL,
            Description TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            IsActive INTEGER NOT NULL,
            LastTriggeredAt TEXT NULL
        );");
}


// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// Azure App Service terminates TLS and proxies requests to this app, so the
// "real" client IP arrives via the X-Forwarded-For header rather than as the
// socket's remote address. Forwarding it into HttpContext.Connection.RemoteIpAddress
// lets SensorIngestController's IP allow-list check see the actual caller's IP.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    // Azure's front-end load balancer is the only proxy hop, and its address
    // isn't fixed/known in advance, so we can't restrict KnownProxies/KnownNetworks
    // the way the ASP.NET Core docs normally recommend for on-prem reverse
    // proxies. The X-Forwarded-For header Azure sets is trustworthy because it
    // is only reachable through Azure's own edge, not directly from clients.
    ForwardLimit = null
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");




app.Run();
