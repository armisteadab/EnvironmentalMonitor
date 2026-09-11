using System;
using System.Collections.Generic;
using System.Linq;
using EnvironmentalMonitor.Data;
using EnvironmentalMonitor.Models;
using Microsoft.EntityFrameworkCore;

namespace EnvironmentalMonitor.Services
{
    /// <summary>
    /// CRUD for SMS alert rules (see SmsAlert / Views/Alerts). Alerts are
    /// created by the AI Chat's create_sms_alert tool (see ReportingService)
    /// and evaluated periodically by AlertMonitorService.
    ///
    /// Registered as a singleton, matching SensorDataService/ReportingService,
    /// since it just wraps the DbContextFactory.
    /// </summary>
    public class AlertsService
    {
        private readonly IDbContextFactory<SensorDbContext> _dbFactory;

        public AlertsService(IDbContextFactory<SensorDbContext> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        /// <summary>All saved SMS alerts (most recently created first).</summary>
        public List<SmsAlert> GetAlerts()
        {
            using var db = _dbFactory.CreateDbContext();
            return db.SmsAlerts.OrderByDescending(a => a.CreatedAt).ToList();
        }

        /// <summary>All active alerts, used by AlertMonitorService's periodic evaluation.</summary>
        public List<SmsAlert> GetActiveAlerts()
        {
            using var db = _dbFactory.CreateDbContext();
            return db.SmsAlerts.Where(a => a.IsActive).ToList();
        }

        public SmsAlert CreateAlert(int? conversationId, string phoneNumber, string sensor, string metric,
            string comparator, double threshold, string description)
        {
            using var db = _dbFactory.CreateDbContext();
            var alert = new SmsAlert
            {
                ConversationId = conversationId,
                PhoneNumber = phoneNumber,
                Sensor = sensor,
                Metric = metric,
                Comparator = comparator,
                Threshold = threshold,
                Description = description,
                CreatedAt = DateTime.Now,
                IsActive = true
            };
            db.SmsAlerts.Add(alert);
            db.SaveChanges();
            return alert;
        }

        /// <summary>Deletes an alert. Returns false if no alert with that id exists.</summary>
        public bool DeleteAlert(int id)
        {
            using var db = _dbFactory.CreateDbContext();
            var alert = db.SmsAlerts.FirstOrDefault(a => a.Id == id);
            if (alert == null) return false;

            db.SmsAlerts.Remove(alert);
            db.SaveChanges();
            return true;
        }

        /// <summary>Records that an alert just fired, for the cooldown check in AlertMonitorService.</summary>
        public void MarkTriggered(int id)
        {
            using var db = _dbFactory.CreateDbContext();
            var alert = db.SmsAlerts.FirstOrDefault(a => a.Id == id);
            if (alert == null) return;

            alert.LastTriggeredAt = DateTime.Now;
            db.SaveChanges();
        }
    }
}
