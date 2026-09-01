using System.Text;
using System.Threading.Tasks;
using EnvironmentalMonitor.Models;
using EnvironmentalMonitor.Services;
using Microsoft.AspNetCore.Mvc;

namespace EnvironmentalMonitor.Controllers
{
    /// <summary>
    /// Backs the "Reports" page (renamed/replacing the old dead "Charts" sidebar
    /// link): an AI chat dialog for generating environmental reports on demand,
    /// with past conversations saved to the database and generated reports
    /// downloadable as text files.
    /// </summary>
    public class ReportsController : Controller
    {
        private readonly ReportingService _reportingService;

        public ReportsController(ReportingService reportingService)
        {
            _reportingService = reportingService;
        }

        public IActionResult Index()
        {
            ViewData["Title"] = "Reports";
            var vm = new ReportsIndexViewModel
            {
                Conversations = _reportingService.GetConversations()
            };
            return View(vm);
        }

        /// <summary>Returns a single conversation's full message history as JSON, used to load a past chat back into the dialog.</summary>
        [HttpGet]
        public IActionResult Conversation(int id)
        {
            var conversation = _reportingService.GetConversation(id);
            if (conversation == null) return NotFound();
            return Json(conversation);
        }

        /// <summary>
        /// Sends a chat message to the AI (creating a new conversation if none
        /// was specified), saves both messages to the database, and returns the
        /// AI's reply as JSON for the chat UI to render.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Send([FromBody] SendReportMessageRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return BadRequest(new { error = "Message cannot be empty." });
            }

            var (conversationId, reply) = await _reportingService.SendMessageAsync(request.ConversationId, request.Message);
            return Json(new { conversationId, reply });
        }

        /// <summary>
        /// Generates (or re-generates) a downloadable .txt report from the AI's
        /// latest response in the given conversation and returns its id/filename
        /// so the UI can offer a download link.
        /// </summary>
        [HttpPost]
        public IActionResult GenerateDownload([FromBody] GenerateDownloadRequest request)
        {
            var report = _reportingService.CreateDownloadableReport(request.ConversationId);
            return Json(new { reportId = report.Id, fileName = report.FileName });
        }

        /// <summary>Streams a previously generated report file for download.</summary>
        [HttpGet]
        public IActionResult Download(int id)
        {
            var report = _reportingService.GetGeneratedReport(id);
            if (report == null) return NotFound();

            var bytes = Encoding.UTF8.GetBytes(report.Content);
            return File(bytes, "text/plain", report.FileName);
        }

        /// <summary>
        /// Permanently deletes a past report conversation (and its messages /
        /// generated report files), used by the "Delete" button next to each
        /// item in the Past Reports list.
        /// </summary>
        [HttpPost]
        public IActionResult Delete(int id)
        {
            var deleted = _reportingService.DeleteConversation(id);
            if (!deleted) return NotFound();
            return Json(new { success = true });
        }
    }
}
