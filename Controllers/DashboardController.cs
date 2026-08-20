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
        /// Generates a CSV export of all sensor readings from the SQLite database
        /// on the fly, used by the "Data" link in the sidebar. (Previously this
        /// streamed the raw sensor_log.csv file directly; now the CSV is
        /// produced dynamically from the database.)
        /// </summary>
        [HttpGet]
        public IActionResult DownloadCsv()
        {
            var csv = _dataService.ExportCsv();
            var bytes = System.Text.Encoding.UTF8.GetBytes(csv);
            return base.File(bytes, "text/csv", "sensor_log.csv");
        }

    }
}



