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
            var vm = _dataService.BuildDashboard(hoursBack: 24, recentRows: 10);
            return View(vm);
        }
    }
}
