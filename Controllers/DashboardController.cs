using Microsoft.AspNetCore.Mvc;
using EnvironmentalMonitor.Services;


namespace EnvironmentalMonitor.Controllers
{
    public class DashboardController : Controller
    {
        private readonly SensorDataService _dataService;

        public DashboardController(SensorDataService dataService)
        {
            _dataService = dataService;
        }

        public IActionResult Index()
        {
            var vm = _dataService.BuildDashboard(hoursBack: 24);

            return View(vm);
        }

        /// <summary>
        /// Returns the list of known sensors (name, type, online status) as JSON,
        /// used to populate the "Sensors" pop-up in the sidebar.
        /// </summary>
        [HttpGet]
        public IActionResult Sensors()
        {
            var sensors = _dataService.GetSensorInfos();
            return Json(sensors);
        }

        /// <summary>
        /// Streams the full sensor log CSV file as a download, used by the
        /// "Data" link in the sidebar.
        /// </summary>
        [HttpGet]
        public IActionResult DownloadCsv()
        {
            var path = _dataService.CsvFilePath;
            if (!System.IO.File.Exists(path))
            {
                return NotFound("No sensor data has been recorded yet.");
            }

            var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            return base.File(stream, "text/csv", "sensor_log.csv");
        }

    }
}



