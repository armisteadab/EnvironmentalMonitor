using EnvironmentalMonitor.Models;
using Microsoft.EntityFrameworkCore;

namespace EnvironmentalMonitor.Data
{
    /// <summary>
    /// EF Core database context backing the SQLite data store. This replaces
    /// the CSV file (wwwroot/data/sensor_log.csv) as the application's
    /// persistent record of sensor readings.
    ///
    /// Registered via AddDbContextFactory (see Program.cs) rather than the
    /// usual AddDbContext, because SensorDataService is a long-lived
    /// singleton and DbContext instances are not thread-safe / not meant to
    /// be shared across requests. The factory lets the singleton service
    /// create a short-lived context per operation.
    /// </summary>
    public class SensorDbContext : DbContext
    {
        public SensorDbContext(DbContextOptions<SensorDbContext> options) : base(options)
        {
        }

        public DbSet<SensorReading> Readings => Set<SensorReading>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SensorReading>(entity =>
            {
                entity.ToTable("Readings");
                entity.HasKey(r => r.Id);
                entity.Property(r => r.Device).IsRequired().HasMaxLength(64);
                entity.Property(r => r.Ip).HasMaxLength(64);

                // Most queries filter/sort by device + time window, so index on both.
                entity.HasIndex(r => new { r.Device, r.Timestamp });
            });
        }
    }
}
