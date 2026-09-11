using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvironmentalMonitor.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EnvironmentalMonitor.Services
{
    /// <summary>
    /// Background service that periodically evaluates every active SmsAlert
    /// rule against the latest sensor readings and sends a text via
    /// SmsService whenever a condition is met, applying a cooldown so the
    /// same alert doesn't spam the user's phone every polling cycle.
    /// </summary>
    public class AlertMonitorService : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan Cooldown = TimeSpan.FromHours(1);

        private readonly AlertsService _alertsService;
        private readonly SensorDataService _sensorDataService;
        private readonly SmsService _smsService;
        private readonly ILogger<AlertMonitorService> _logger;

        public AlertMonitorService(
            AlertsService alertsService,
            SensorDataService sensorDataService,
            SmsService smsService,
            ILogger<AlertMonitorService> logger)
        {
            _alertsService = alertsService;
            _sensorDataService = sensorDataService;
            _smsService = smsService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await EvaluateAlertsAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Alert monitor evaluation failed.");
                }

                try
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    // Shutting down.
                }
            }
        }

        private async Task EvaluateAlertsAsync()
        {
            var alerts = _alertsService.GetActiveAlerts();
            if (alerts.Count == 0) return;

            var dashboard = _sensorDataService.BuildDashboard(hoursBack: 1);
            if (dashboard.Cards.Count == 0) return;

            foreach (var alert in alerts)
            {
                if (alert.LastTriggeredAt.HasValue && DateTime.Now - alert.LastTriggeredAt.Value < Cooldown)
                {
                    continue;
                }

                var matchingCards = string.Equals(alert.Sensor, "ANY", StringComparison.OrdinalIgnoreCase)
                    ? dashboard.Cards
                    : dashboard.Cards.Where(c => string.Equals(c.RawKey, alert.Sensor, StringComparison.OrdinalIgnoreCase)).ToList();

                foreach (var card in matchingCards)
                {
                    double? value = alert.Metric switch
                    {
                        "Temperature" => card.TempF,
                        "Humidity" => card.Humidity,
                        "Co2" => card.Co2,
                        _ => null
                    };

                    if (!value.HasValue) continue;

                    var triggered = string.Equals(alert.Comparator, "Below", StringComparison.OrdinalIgnoreCase)
                        ? value.Value < alert.Threshold
                        : value.Value > alert.Threshold;

                    if (!triggered) continue;

                    var message = $"Environmental Monitor alert: {alert.Description} " +
                                  $"(currently {value.Value:0.#} at {card.Name}, as of {card.Timestamp:MMM d, h:mm tt}).";

                    var sent = await _smsService.SendAsync(alert.PhoneNumber, message);
                    if (sent || !_smsService.IsConfigured)
                    {
                        // Mark as triggered even when Twilio isn't configured (dev mode), so the
                        // cooldown logic still behaves the same way it would in production.
                        _alertsService.MarkTriggered(alert.Id);
                    }

                    break; // one text per alert per cycle, even if multiple sensors match "ANY"
                }
            }
        }
    }
}
