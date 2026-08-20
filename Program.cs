using EnvironmentalMonitor.Data;
using EnvironmentalMonitor.Services;
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

    if (!db.Readings.Any())
    {
        var legacyCsvPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "data", "sensor_log.csv");
        SensorDataService.ImportCsv(legacyCsvPath, db);
    }
}


// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");




app.Run();
