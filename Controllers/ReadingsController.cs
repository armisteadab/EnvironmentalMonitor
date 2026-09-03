using Microsoft.AspNetCore.Mvc;
using EnvironmentalMonitor.Models;
using EnvironmentalMonitor.Services;

namespace EnvironmentalMonitor.Controllers
{
    /// <summary>
    /// Backs the dedicated "Recent Readings" page (moved out of the Dashboard
    /// into its own sidebar item/page, the same way "AI Report Chat" has its
    /// own page separate from the Dashboard).
    /// </summary>
    public class ReadingsController : Controller
    {
        private readonly SensorDataService _dataService;

        public ReadingsController(SensorDataService dataService)
        {
            _dataService = dataService;
        }

        private const int PageSize = 50;

        public IActionResult Index(int page = 1)
        {
            if (page < 1) page = 1;

            var asOf = _dataService.GetAsOf();
            var (rows, totalCount) = _dataService.GetReadingsPage(page, PageSize);
            var totalPages = totalCount == 0 ? 1 : (int)Math.Ceiling(totalCount / (double)PageSize);
            if (page > totalPages) page = totalPages;

            var vm = new ReadingsIndexViewModel
            {
                AsOf = asOf,
                Readings = rows,
                PageNumber = page,
                PageSize = PageSize,
                TotalCount = totalCount,
                TotalPages = totalPages
            };

            ViewData["Title"] = "Recent Readings";
            ViewData["HeaderTime"] = asOf.ToString("MMM d, yyyy  h:mm tt");
            ViewData["LastDataTime"] = asOf.ToString("MMM d, yyyy h:mm:ss tt");

            return View(vm);
        }
    }
}
