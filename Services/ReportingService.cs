using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using EnvironmentalMonitor.Data;
using EnvironmentalMonitor.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace EnvironmentalMonitor.Services
{
    /// <summary>
    /// Drives the "Reports" chat feature: persists conversations/messages to the
    /// SQLite database (via the same IDbContextFactory pattern used by
    /// SensorDataService), calls Azure OpenAI to produce AI responses grounded
    /// in the app's own sensor history, and builds downloadable report files
    /// from the AI's output.
    ///
    /// Registered as a singleton (see Program.cs), matching SensorDataService,
    /// since it holds no per-request state - just the DbContextFactory and a
    /// (thread-safe) Azure OpenAI client.
    /// </summary>
    public class ReportingService
    {
        private readonly IDbContextFactory<SensorDbContext> _dbFactory;
        private readonly SensorDataService _sensorDataService;
        private readonly ILogger<ReportingService> _logger;
        private readonly ChatClient? _chatClient;
        private readonly bool _isConfigured;

        public ReportingService(
            IDbContextFactory<SensorDbContext> dbFactory,
            SensorDataService sensorDataService,
            IConfiguration configuration,
            ILogger<ReportingService> logger)
        {
            _dbFactory = dbFactory;
            _sensorDataService = sensorDataService;
            _logger = logger;

            // Azure OpenAI settings come from configuration (appsettings.json /
            // environment variables / Azure App Service application settings),
            // never hard-coded, since the API key is a secret.
            var endpoint = configuration["AzureOpenAI:Endpoint"];
            var apiKey = configuration["AzureOpenAI:ApiKey"];
            var deployment = configuration["AzureOpenAI:Deployment"];

            if (!string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(deployment))
            {
                var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
                _chatClient = azureClient.GetChatClient(deployment);
                _isConfigured = true;
            }
            else
            {
                _isConfigured = false;
                _logger.LogWarning("Azure OpenAI is not configured (AzureOpenAI:Endpoint/ApiKey/Deployment missing). " +
                                    "The Reports chat feature will return a placeholder response until it's configured.");
            }
        }

        /// <summary>All saved report conversations (most recently updated first), including their messages.</summary>
        public List<ReportConversation> GetConversations()
        {
            using var db = _dbFactory.CreateDbContext();
            return db.ReportConversations
                .Include(c => c.Messages)
                .Include(c => c.GeneratedReports)
                .OrderByDescending(c => c.UpdatedAt)
                .ToList();
        }

        /// <summary>A single conversation with its messages, or null if not found.</summary>
        public ReportConversation? GetConversation(int id)
        {
            using var db = _dbFactory.CreateDbContext();
            return db.ReportConversations
                .Include(c => c.Messages)
                .Include(c => c.GeneratedReports)
                .FirstOrDefault(c => c.Id == id);
        }

        /// <summary>
        /// Saves the user's message, asks the AI for a response (with recent
        /// sensor data as context), saves the AI's reply, and returns both the
        /// updated conversation id and the assistant's reply text.
        /// </summary>
        public async Task<(int ConversationId, string Reply)> SendMessageAsync(int? conversationId, string userMessage)
        {
            using var db = _dbFactory.CreateDbContext();

            ReportConversation conversation;
            if (conversationId.HasValue)
            {
                conversation = db.ReportConversations
                    .Include(c => c.Messages)
                    .FirstOrDefault(c => c.Id == conversationId.Value)
                    ?? throw new InvalidOperationException($"Report conversation {conversationId} not found.");
            }
            else
            {
                conversation = new ReportConversation
                {
                    Title = BuildTitle(userMessage),
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };
                db.ReportConversations.Add(conversation);
                db.SaveChanges(); // assigns conversation.Id
            }

            var userMsg = new ReportMessage
            {
                ConversationId = conversation.Id,
                Role = "user",
                Content = userMessage,
                Timestamp = DateTime.Now
            };
            db.ReportMessages.Add(userMsg);
            db.SaveChanges();

            var history = db.ReportMessages
                .Where(m => m.ConversationId == conversation.Id)
                .OrderBy(m => m.Timestamp)
                .ToList();

            var reply = await GetAiReplyAsync(history);

            var assistantMsg = new ReportMessage
            {
                ConversationId = conversation.Id,
                Role = "assistant",
                Content = reply,
                Timestamp = DateTime.Now
            };
            db.ReportMessages.Add(assistantMsg);

            conversation.UpdatedAt = DateTime.Now;
            db.SaveChanges();

            return (conversation.Id, reply);
        }

        /// <summary>
        /// Builds a plain-text report file from the AI's latest reply in a
        /// conversation and saves it to the database so it can be downloaded
        /// again later.
        /// </summary>
        public GeneratedReport CreateDownloadableReport(int conversationId)
        {
            using var db = _dbFactory.CreateDbContext();
            var conversation = db.ReportConversations
                .Include(c => c.Messages)
                .FirstOrDefault(c => c.Id == conversationId)
                ?? throw new InvalidOperationException($"Report conversation {conversationId} not found.");

            var lastAssistantMessage = conversation.Messages
                .Where(m => m.Role == "assistant")
                .OrderByDescending(m => m.Timestamp)
                .FirstOrDefault();

            if (lastAssistantMessage == null)
            {
                throw new InvalidOperationException("This conversation has no AI response yet to turn into a report.");
            }

            var report = new GeneratedReport
            {
                ConversationId = conversation.Id,
                FileName = $"report_{conversation.Id}_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                Content = lastAssistantMessage.Content,
                CreatedAt = DateTime.Now
            };

            db.GeneratedReports.Add(report);
            db.SaveChanges();
            return report;
        }

        /// <summary>Fetches a previously generated report file for download.</summary>
        public GeneratedReport? GetGeneratedReport(int id)
        {
            using var db = _dbFactory.CreateDbContext();
            return db.GeneratedReports.FirstOrDefault(r => r.Id == id);
        }

        /// <summary>
        /// Permanently deletes a report conversation, including its messages
        /// and any generated report files (both cascade-deleted via the FK
        /// relationships configured in SensorDbContext). Returns false if no
        /// conversation with that id exists.
        /// </summary>
        public bool DeleteConversation(int id)
        {
            using var db = _dbFactory.CreateDbContext();
            var conversation = db.ReportConversations.FirstOrDefault(c => c.Id == id);
            if (conversation == null) return false;

            db.ReportConversations.Remove(conversation);
            db.SaveChanges();
            return true;
        }

        private async Task<string> GetAiReplyAsync(List<ReportMessage> history)
        {
            if (!_isConfigured || _chatClient == null)
            {
                return "Azure OpenAI is not configured yet. Set AzureOpenAI:Endpoint, AzureOpenAI:ApiKey, " +
                       "and AzureOpenAI:Deployment (e.g. via appsettings.json or environment variables) to enable AI-generated reports.";
            }

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(BuildSystemPrompt())
            };

            foreach (var m in history)
            {
                messages.Add(m.Role == "assistant"
                    ? new AssistantChatMessage(m.Content)
                    : new UserChatMessage(m.Content));
            }

            try
            {
                ChatCompletion completion = await _chatClient.CompleteChatAsync(messages);
                return completion.Content.Count > 0 ? completion.Content[0].Text : "(No response text was returned.)";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Azure OpenAI chat completion failed.");
                return "Sorry, something went wrong contacting the AI service. Please try again.";
            }
        }

        /// <summary>
        /// Grounds the AI in the app's own sensor data by summarizing recent
        /// readings into the system prompt, so it can write reports about
        /// actual temperature/humidity/CO2 history instead of hallucinating.
        /// </summary>
        private string BuildSystemPrompt()
        {
            var dashboard = _sensorDataService.BuildDashboard(hoursBack: 24);

            var sb = new StringBuilder();
            sb.AppendLine("You are an assistant that helps the user create written reports summarizing data from a home " +
                          "environmental monitoring system (ESP32 + DHT-22 sensors placed Outdoor, Upstairs, and in the Basement, " +
                          "tracking temperature, humidity, and outdoor CO2).");
            sb.AppendLine("Write clear, well-organized plain-text reports (suitable for downloading as a .txt file) when the user asks for one.");
            sb.AppendLine();
            sb.AppendLine($"Here is a summary of the most recent sensor data, as of {dashboard.AsOf:MMM d, yyyy h:mm tt}:");
            foreach (var card in dashboard.Cards)
            {
                var co2Text = card.Co2.HasValue ? $", {card.Co2.Value:0} ppm CO2" : string.Empty;
                sb.AppendLine($"- {card.Name}: {card.TempF:0}\u00b0F / {card.TempC:0}\u00b0C, {card.Humidity:0}% humidity{co2Text}, " +
                              $"{(card.Online ? "online" : "offline")} as of {card.Timestamp:MMM d, h:mm tt}");
            }
            sb.AppendLine();
            sb.AppendLine("Recent readings (most recent last):");
            foreach (var row in dashboard.RecentReadings.TakeLast(20))
            {
                var co2Text = row.Co2.HasValue ? $" | {row.Co2.Value:0} ppm CO2" : string.Empty;
                sb.AppendLine($"- {row.Timestamp:MMM d, h:mm tt} | {row.SensorName} | {row.TempF:0}\u00b0F | {row.Humidity:0}% humidity{co2Text}");
            }

            return sb.ToString();
        }

        private static string BuildTitle(string firstMessage)
        {
            var trimmed = firstMessage.Trim();
            if (trimmed.Length > 60) trimmed = trimmed[..60] + "...";
            return string.IsNullOrWhiteSpace(trimmed) ? "New Report" : trimmed;
        }
    }
}

