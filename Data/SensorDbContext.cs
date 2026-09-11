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

        /// <summary>AI-report chat conversations (see Views/Reports and ReportingService).</summary>
        public DbSet<ReportConversation> ReportConversations => Set<ReportConversation>();
        public DbSet<ReportMessage> ReportMessages => Set<ReportMessage>();
        public DbSet<GeneratedReport> GeneratedReports => Set<GeneratedReport>();

        /// <summary>SMS alert rules created via the AI Chat (see Views/Alerts and AlertsService/AlertMonitorService).</summary>
        public DbSet<SmsAlert> SmsAlerts => Set<SmsAlert>();

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

            modelBuilder.Entity<ReportConversation>(entity =>
            {
                entity.ToTable("ReportConversations");
                entity.HasKey(c => c.Id);
                entity.Property(c => c.Title).IsRequired().HasMaxLength(200);
            });

            modelBuilder.Entity<ReportMessage>(entity =>
            {
                entity.ToTable("ReportMessages");
                entity.HasKey(m => m.Id);
                entity.Property(m => m.Role).IsRequired().HasMaxLength(16);
                entity.HasIndex(m => m.ConversationId);
                entity.HasOne(m => m.Conversation)
                    .WithMany(c => c.Messages)
                    .HasForeignKey(m => m.ConversationId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<GeneratedReport>(entity =>
            {
                entity.ToTable("GeneratedReports");
                entity.HasKey(g => g.Id);
                entity.Property(g => g.FileName).IsRequired().HasMaxLength(256);
                entity.HasIndex(g => g.ConversationId);
                entity.HasOne(g => g.Conversation)
                    .WithMany(c => c.GeneratedReports)
                    .HasForeignKey(g => g.ConversationId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<SmsAlert>(entity =>
            {
                entity.ToTable("SmsAlerts");
                entity.HasKey(a => a.Id);
                entity.Property(a => a.PhoneNumber).IsRequired().HasMaxLength(32);
                entity.Property(a => a.Sensor).IsRequired().HasMaxLength(32);
                entity.Property(a => a.Metric).IsRequired().HasMaxLength(32);
                entity.Property(a => a.Comparator).IsRequired().HasMaxLength(16);
                entity.Property(a => a.Description).IsRequired().HasMaxLength(500);
            });
        }
    }
}
