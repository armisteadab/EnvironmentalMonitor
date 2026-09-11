using EnvironmentalMonitor.Models;
using EnvironmentalMonitor.Services;
using Microsoft.AspNetCore.Mvc;

namespace EnvironmentalMonitor.Controllers
{
    /// <summary>
    /// Backs the "Alerts" page: lists the SMS alert rules the user has asked
    /// the AI Chat to create, and lets the user delete them.
    /// </summary>
    public class AlertsController : Controller
    {
        private readonly AlertsService _alertsService;

        public AlertsController(AlertsService alertsService)
        {
            _alertsService = alertsService;
        }

        public IActionResult Index()
        {
            ViewData["Title"] = "Alerts";
            var vm = new AlertsIndexViewModel
            {
                Alerts = _alertsService.GetAlerts()
            };
            return View(vm);
        }

        /// <summary>Permanently deletes an SMS alert rule, used by the "Delete" button next to each item in the list.</summary>
        [HttpPost]
        public IActionResult Delete(int id)
        {
            var deleted = _alertsService.DeleteAlert(id);
            if (!deleted) return NotFound();
            return Json(new { success = true });
        }
    }
}
