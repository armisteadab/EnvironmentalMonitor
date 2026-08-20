using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using EnvironmentalMonitor.Data;
using EnvironmentalMonitor.Models;
using Microsoft.EntityFrameworkCore;

namespace EnvironmentalMonitor.Services
{
    public class SensorDataService
    {
        private readonly IDbContextFactory<SensorDbContext> _dbFactory;

        public static readonly Dictionary<string, (string Display, string Color)> DeviceMeta =
            new(StringComparer.OrdinalIgnoreCase)
        {
            { "OUTSIDE",  ("Outdoor",  "#3b82f6") },
            { "UPSTAIRS", ("Upstairs", "#f59e0b") },
            { "BASEMENT", ("Basement", "#10b981") }
        };

        public SensorDataService(IDbContextFactory<SensorDbContext> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        /// <summary>
        /// Appends a single sensor reading to the SQLite database. This replaces the
        /// previous CSV append logic; EF Core / SQLite handle durability and
        /// concurrent-writer safety for us, so no manual locking is required here.
        /// </summary>
        public void AppendReading(SensorReading reading)
        {
            using var db = _dbFactory.CreateDbContext();
            db.Readings.Add(reading);
            db.SaveChanges();
        }

        /// <summary>
        /// Loads every reading from the database. This replaces the previous
        /// CSV-parsing + file-timestamp-based in-memory cache: SQLite reads are
        /// fast enough at this data volume that no additional caching layer is
        /// needed. Everything downstream (BuildDashboard, GetSensorInfos, etc.)
        /// is unchanged and simply consumes this list.
        /// </summary>
        public List<SensorReading> GetAll()
        {
            using var db = _dbFactory.CreateDbContext();
            return db.Readings.AsNoTracking().OrderBy(r => r.Timestamp).ToList();
        }

        /// <summary>The most recent timestamp in the database, used as "now" for the demo dashboard.</summary>
        public DateTime GetAsOf()
        {
            var all = GetAll();
            return all.Count == 0 ? DateTime.Now : all.Max(r => r.Timestamp);
        }

        /// <summary>
        /// Builds a summary list of all known sensors, including their sensor type
        /// (always "DHT-22"), online status, and last-seen timestamp.
        /// </summary>
        public List<SensorInfo> GetSensorInfos()
        {
            var all = GetAll();
            var asOf = GetAsOf();
            var list = new List<SensorInfo>();

            foreach (var kv in DeviceMeta)
            {
                var latest = all.Where(r => r.Device.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))
                                 .OrderByDescending(r => r.Timestamp)
                                 .FirstOrDefault();

                var info = new SensorInfo
                {
                    Name = kv.Value.Display,
                    RawKey = kv.Key,
                    ColorHex = kv.Value.Color,
                    SensorType = "DHT-22",
                    LastSeen = latest?.Timestamp,
                    Online = latest != null && (asOf - latest.Timestamp).TotalMinutes < 60
                };
                list.Add(info);
            }

            return list
                .OrderBy(s => s.RawKey == "OUTSIDE" ? 0 : s.RawKey == "UPSTAIRS" ? 1 : 2)
                .ToList();
        }

        public DashboardViewModel BuildDashboard(int hoursBack = 24)

        {

            var all = GetAll();
            var asOf = GetAsOf();
            var vm = new DashboardViewModel { AsOf = asOf };

            // ----- Cards: latest reading per device -----
            foreach (var kv in DeviceMeta)
            {
                var latest = all.Where(r => r.Device.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))
                                .OrderByDescending(r => r.Timestamp)
                                .FirstOrDefault();
                if (latest == null) continue;
                var online = (asOf - latest.Timestamp).TotalMinutes < 60;
                vm.Cards.Add(new SensorCard
                {
                    Name = kv.Value.Display,
                    RawKey = kv.Key,
                    TempF = latest.TempF,
                    TempC = latest.TempC,
                    Humidity = latest.Humidity,
                    Online = online,
                    ColorHex = kv.Value.Color,
                    Timestamp = latest.Timestamp
                });
            }

            // Order Outdoor, Upstairs, Basement
            vm.Cards = vm.Cards
                .OrderBy(c => c.RawKey == "OUTSIDE" ? 0 : c.RawKey == "UPSTAIRS" ? 1 : 2)
                .ToList();

            // ----- Time series for last N hours, bucketed every 30 minutes -----
            var windowStart = asOf.AddHours(-hoursBack);
            var bucketMinutes = 30;
            var buckets = new List<DateTime>();
            for (var t = new DateTime(windowStart.Year, windowStart.Month, windowStart.Day,
                                     windowStart.Hour, (windowStart.Minute / bucketMinutes) * bucketMinutes, 0);
                 t <= asOf; t = t.AddMinutes(bucketMinutes))
            {
                buckets.Add(t);
            }
            vm.ChartLabels = buckets.Select(b => b.ToString("MMM d h:mm tt")).ToList();

            foreach (var kv in DeviceMeta)
            {
                var deviceReadings = all
                    .Where(r => r.Device.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)
                                && r.Timestamp >= windowStart && r.Timestamp <= asOf)
                    .OrderBy(r => r.Timestamp)
                    .ToList();

                var temps = new List<double?>();
                var hums = new List<double?>();
                foreach (var b in buckets)
                {
                    var bEnd = b.AddMinutes(bucketMinutes);
                    var group = deviceReadings.Where(r => r.Timestamp >= b && r.Timestamp < bEnd).ToList();
                    if (group.Count == 0)
                    {
                        temps.Add(null);
                        hums.Add(null);
                    }
                    else
                    {
                        temps.Add(Math.Round(group.Average(r => r.TempF), 1));
                        hums.Add(Math.Round(group.Average(r => r.Humidity), 1));
                    }
                }
                vm.TempSeries[kv.Value.Display] = temps;
                vm.HumiditySeries[kv.Value.Display] = hums;
            }

            // ----- Recent readings table (all readings within the same
            //       hoursBack window used for the charts above, so the
            //       table and graphs always agree on what "recent" means) -----
            vm.RecentReadings = all
                .Where(r => r.Timestamp >= windowStart && r.Timestamp <= asOf)
                .OrderByDescending(r => r.Timestamp)
                .Select(r =>

                {
                    var meta = DeviceMeta.TryGetValue(r.Device, out var m) ? m : (r.Device, "#6b7280");
                    var online = (asOf - r.Timestamp).TotalMinutes < 60;
                    return new RecentReadingRow
                    {
                        Timestamp = r.Timestamp,
                        SensorName = meta.Item1,
                        ColorHex = meta.Item2,
                        TempF = r.TempF,
                        TempC = r.TempC,
                        Humidity = r.Humidity,
                        Online = online
                    };
                })
                .ToList();

            return vm;
        }

        /// <summary>
        /// Renders all readings as CSV text, in the same column format as the
        /// legacy sensor_log.csv file, for the "Data" download link in the sidebar.
        /// </summary>
        public string ExportCsv()
        {
            var all = GetAll();
            var sb = new StringBuilder();
            sb.AppendLine("timestamp,device,temp_c,temp_f,humid,ip_of_sensor");
            foreach (var r in all)
            {
                sb.AppendLine(string.Join(",",
                    r.Timestamp.ToString("o", CultureInfo.InvariantCulture),
                    r.Device,
                    r.TempC.ToString(CultureInfo.InvariantCulture),
                    r.TempF.ToString(CultureInfo.InvariantCulture),
                    r.Humidity.ToString(CultureInfo.InvariantCulture),
                    r.Ip));
            }
            return sb.ToString();
        }

        /// <summary>
        /// One-time migration helper: parses a legacy CSV log file (in the format
        /// produced by the old CSV-based data store) and bulk-inserts all rows into
        /// the given database context. Called once at startup (see Program.cs) only
        /// when the Readings table is empty and a legacy CSV file is present, so
        /// existing sensor history is preserved when switching to SQLite.
        /// </summary>
        public static void ImportCsv(string csvPath, SensorDbContext db)
        {
            if (!File.Exists(csvPath)) return;

            var readings = new List<SensorReading>();
            using (var reader = new StreamReader(csvPath))
            {
                string? line = reader.ReadLine(); // header
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',');
                    if (parts.Length < 6) continue;

                    if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeLocal, out var ts)) continue;
                    if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var tc)) continue;
                    if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var tf)) continue;
                    if (!double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var hu)) continue;

                    readings.Add(new SensorReading
                    {
                        Timestamp = ts,
                        Device = parts[1].Trim(),
                        TempC = tc,
                        TempF = tf,
                        Humidity = hu,
                        Ip = parts[5].Trim()
                    });
                }
            }

            if (readings.Count == 0) return;

            db.Readings.AddRange(readings);
            db.SaveChanges();
        }
    }
}
