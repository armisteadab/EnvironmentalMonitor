using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EnvironmentalMonitor.Models
{
    /// <summary>
    /// A single AI-report chat session. Groups together the back-and-forth
    /// messages the user exchanged with the AI while producing a report, plus
    /// any downloadable report file that resulted from it.
    /// </summary>
    public class ReportConversation
    {
        public int Id { get; set; }
        public string Title { get; set; } = "New Report";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        public List<ReportMessage> Messages { get; set; } = new();
        public List<GeneratedReport> GeneratedReports { get; set; } = new();
    }

    /// <summary>One chat message (either from the user or the AI assistant) within a ReportConversation.</summary>
    public class ReportMessage
    {
        public int Id { get; set; }
        public int ConversationId { get; set; }
        [JsonIgnore]
        public ReportConversation? Conversation { get; set; }

        /// <summary>"user" or "assistant" (matches the OpenAI chat role naming).</summary>
        public string Role { get; set; } = "user";
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// A downloadable report file (plain text) produced by the AI during a
    /// conversation, saved so the user can re-download it later without
    /// regenerating it.
    /// </summary>
    public class GeneratedReport
    {
        public int Id { get; set; }
        public int ConversationId { get; set; }
        [JsonIgnore]
        public ReportConversation? Conversation { get; set; }

        public string FileName { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>View model for the Reports/Index page.</summary>
    public class ReportsIndexViewModel
    {
        public List<ReportConversation> Conversations { get; set; } = new();
    }

    /// <summary>Request body posted by the chat UI when the user sends a message.</summary>
    public class SendReportMessageRequest
    {
        public int? ConversationId { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>Request body posted by the chat UI when the user asks to generate a downloadable report file.</summary>
    public class GenerateDownloadRequest
    {
        public int ConversationId { get; set; }
    }
}
