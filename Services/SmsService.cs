using System;
using System.Threading.Tasks;
using Azure.Communication.Sms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EnvironmentalMonitor.Services
{
    /// <summary>
    /// Sends SMS text messages via Azure Communication Services (ACS).
    /// Configuration (connection string / from phone number) comes from
    /// IConfiguration ("AzureCommunicationServices:*" in appsettings.json /
    /// environment variables / Azure App Service application settings), the
    /// same pattern used by ReportingService for the Azure OpenAI secrets -
    /// never hard-coded.
    ///
    /// Registered as a singleton since Azure's SmsClient is thread-safe and
    /// holds no per-request state.
    /// </summary>
    public class SmsService
    {
        private readonly ILogger<SmsService> _logger;
        private readonly SmsClient? _smsClient;
        private readonly string? _fromPhoneNumber;
        private readonly bool _isConfigured;

        public SmsService(IConfiguration configuration, ILogger<SmsService> logger)
        {
            _logger = logger;

            var connectionString = configuration["AzureCommunicationServices:ConnectionString"];
            _fromPhoneNumber = configuration["AzureCommunicationServices:FromPhoneNumber"];

            _isConfigured = !string.IsNullOrWhiteSpace(connectionString) && !string.IsNullOrWhiteSpace(_fromPhoneNumber);

            if (_isConfigured)
            {
                _smsClient = new SmsClient(connectionString);
            }
            else
            {
                _logger.LogWarning("Azure Communication Services is not configured " +
                                    "(AzureCommunicationServices:ConnectionString/FromPhoneNumber missing). " +
                                    "SMS alerts will be logged only until it's configured.");
            }
        }

        public bool IsConfigured => _isConfigured;

        /// <summary>
        /// Sends a text message to the given phone number. If ACS isn't
        /// configured, logs the message instead of failing outright, so the
        /// rest of the alerting feature (creating/listing/deleting alerts)
        /// keeps working in dev environments without ACS credentials.
        /// </summary>
        public async Task<bool> SendAsync(string toPhoneNumber, string message)
        {
            if (!_isConfigured || _smsClient == null)
            {
                _logger.LogInformation("(Azure Communication Services not configured) Would have sent SMS to {To}: {Message}",
                    toPhoneNumber, message);
                return false;
            }

            try
            {
                var response = await _smsClient.SendAsync(_fromPhoneNumber, toPhoneNumber, message);
                if (!response.Value.Successful)
                {
                    _logger.LogWarning("ACS SMS send to {To} failed: HttpStatusCode={StatusCode}, ErrorMessage={ErrorMessage}",
                        toPhoneNumber, response.Value.HttpStatusCode, response.Value.ErrorMessage);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send SMS via Azure Communication Services.");
                return false;
            }
        }
    }
}
