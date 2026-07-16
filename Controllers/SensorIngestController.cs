using System;
using System.Globalization;
using EnvironmentalMonitor.Models;
using EnvironmentalMonitor.Services;
using Microsoft.AspNetCore.Mvc;

namespace EnvironmentalMonitor.Controllers
{
    /// <summary>
    /// Accepts JSON sensor telemetry over HTTP and appends each reading to the
    /// CSV log used by the dashboard. The accepted JSON shape mirrors the
    /// payload already produced by wwwroot/data/sensor_logger_azure.py, so
    /// when this project moves to Azure IoT Hub, a Function/webhook can post
    /// the same message body here (or this endpoint can be retired entirely).
    ///
    /// Example request:
    ///   POST /api/sensor-ingest
    ///   Content-Type: application/json
    ///   {
    ///     "recordedUtc": "2026-05-08T16:48:24.378897Z",
    ///     "deviceId": "UPSTAIRS",
    ///     "temperatureC": 22.5,
    ///     "temperatureF": 72.5,
    ///     "humidity": 60.2,
    ///     "sensorIp": "10.0.0.108"
    ///   }
    /// </summary>
    [ApiController]
    [Route("api/sensor-ingest")]
    public class SensorIngestController : ControllerBase
    {
        private readonly SensorDataService _dataService;

        public SensorIngestController(SensorDataService dataService)
        {
            _dataService = dataService;
        }

        [HttpPost]
        public IActionResult Post([FromBody] SensorIngestRequest request)
        {
            if (request == null)
            {
                return BadRequest(new { error = "Request body is required." });
            }

            if (string.IsNullOrWhiteSpace(request.DeviceId))
            {
                return BadRequest(new { error = "deviceId is required." });
            }

            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var timestamp = request.RecordedUtc ?? DateTime.Now;
            var tempF = request.TemperatureF ?? (request.TemperatureC * 9.0 / 5.0 + 32.0);

            var reading = new SensorReading
            {
                Timestamp = timestamp,
                Device = request.DeviceId.Trim(),
                TempC = request.TemperatureC,
                TempF = Math.Round(tempF, 2),
                Humidity = request.Humidity,
                Ip = request.SensorIp ?? string.Empty
            };

            _dataService.AppendReading(reading);

            return Ok(new
            {
                status = "ok",
                saved = new
                {
                    timestamp = reading.Timestamp.ToString("o", CultureInfo.InvariantCulture),
                    device = reading.Device,
                    tempC = reading.TempC,
                    tempF = reading.TempF,
                    humidity = reading.Humidity,
                    ip = reading.Ip
                }
            });
        }
    }
}
