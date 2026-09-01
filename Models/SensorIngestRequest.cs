using System;
using System.ComponentModel.DataAnnotations;

namespace EnvironmentalMonitor.Models
{
    /// <summary>
    /// Shape of the JSON payload accepted by the sensor ingest API.
    /// Field names intentionally match the JSON telemetry payload produced
    /// by wwwroot/data/sensor_logger_azure.py, which runs on a local machine
    /// and POSTs each reading here directly.
    /// </summary>
    public class SensorIngestRequest
    {
        /// <summary>Optional. ISO-8601 timestamp. Defaults to server time (local) if omitted.</summary>

        public DateTime? RecordedUtc { get; set; }

        [Required]
        public string DeviceId { get; set; } = string.Empty;

        [Required]
        public double TemperatureC { get; set; }

        // Optional - computed from TemperatureC if not supplied.
        public double? TemperatureF { get; set; }

        [Required]
        public double Humidity { get; set; }

        /// <summary>Optional. IP address (or other identifier) of the originating sensor.</summary>
        public string? SensorIp { get; set; }

        /// <summary>Optional. CO2 concentration in ppm, for sensors that report it.</summary>
        public double? Co2 { get; set; }
    }

}
