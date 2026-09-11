using System;
using System.Collections.Generic;

namespace EnvironmentalMonitor.Models
{
    /// <summary>
    /// A standing "text me if X happens" rule created via the AI Chat
    /// (see ReportingService's create_sms_alert tool). Evaluated periodically
    /// against live sensor data by AlertMonitorService, which sends an SMS
    /// via SmsService whenever the condition is true.
    /// </summary>
    public class SmsAlert
    {
        public int Id { get; set; }

        /// <summary>The AI Chat conversation this alert was created from, if any.</summary>
        public int? ConversationId { get; set; }

        public string PhoneNumber { get; set; } = string.Empty;

        /// <summary>Raw device key ("OUTSIDE", "UPSTAIRS", "BASEMENT"), or "ANY" to match any sensor.</summary>
        public string Sensor { get; set; } = "ANY";

        /// <summary>"Temperature" (°F), "Humidity" (%), or "Co2" (ppm).</summary>
        public string Metric { get; set; } = "Temperature";

        /// <summary>"Above" or "Below".</summary>
        public string Comparator { get; set; } = "Above";

        public double Threshold { get; set; }

        /// <summary>Human-readable summary of the condition, shown in the Alerts list and included in the SMS text.</summary>
        public string Description { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public bool IsActive { get; set; } = true;

        /// <summary>When this alert last actually sent a text, used to apply a cooldown so it doesn't spam the same condition repeatedly.</summary>
        public DateTime? LastTriggeredAt { get; set; }
    }

    /// <summary>View model for the Alerts/Index page.</summary>
    public class AlertsIndexViewModel
    {
        public List<SmsAlert> Alerts { get; set; } = new();
    }
}
