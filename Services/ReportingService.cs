using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        private readonly AlertsService _alertsService;
        private readonly ILogger<ReportingService> _logger;
        private readonly ChatClient? _chatClient;
        private readonly bool _isConfigured;

        public ReportingService(
            IDbContextFactory<SensorDbContext> dbFactory,
            SensorDataService sensorDataService,
            AlertsService alertsService,
            IConfiguration configuration,
            ILogger<ReportingService> logger)
        {
            _dbFactory = dbFactory;
            _sensorDataService = sensorDataService;
            _alertsService = alertsService;
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

            var reply = await GetAiReplyAsync(history, conversation.Id);

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

        /// <summary>
        /// The single tool exposed to the AI: an arbitrary read-only SQL query against
        /// the app's entire SQLite database (Readings, ReportConversations, ReportMessages,
        /// GeneratedReports tables), so the assistant is not limited to the last-24-hours
        /// summary baked into the system prompt and can answer questions about the full
        /// sensor history (e.g. "what was the coldest day last winter?").
        /// </summary>
        private static readonly ChatTool QueryDatabaseTool = ChatTool.CreateFunctionTool(
            functionName: "query_database",
            functionDescription:
                "Runs a read-only SQL query (SELECT only) against the application's SQLite database and returns " +
                "the resulting rows as JSON. Use this to look up sensor history beyond the recent summary already " +
                "provided, e.g. specific date ranges, min/max/avg aggregates, or counts. " +
                "Available tables: " +
                "Readings(Id, Timestamp, Device, TempC, TempF, Humidity, Ip, Co2) - Device is one of 'OUTSIDE', 'UPSTAIRS', 'BASEMENT'; " +
                "ReportConversations(Id, Title, CreatedAt, UpdatedAt); " +
                "ReportMessages(Id, ConversationId, Role, Content, Timestamp); " +
                "GeneratedReports(Id, ConversationId, FileName, Content, CreatedAt). " +
                "Timestamp columns are stored as ISO-8601 text, comparable with standard SQL string/date comparisons or SQLite date functions. " +
                "Always include a LIMIT clause (e.g. LIMIT 200) unless you are computing an aggregate.",
            functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "sql": {
                            "type": "string",
                            "description": "A single read-only SQL SELECT statement to run against the SQLite database."
                        }
                    },
                    "required": ["sql"]
                }
                """));

        /// <summary>
        /// The AI tool that lets the user create a standing "text me if X happens" SMS
        /// alert rule through conversation (e.g. "text me at 555-123-4567 if the basement
        /// humidity goes above 70%"). Alerts are evaluated periodically in the background
        /// by AlertMonitorService and shown/deletable on the Alerts page.
        /// </summary>
        private static readonly ChatTool CreateSmsAlertTool = ChatTool.CreateFunctionTool(
            functionName: "create_sms_alert",
            functionDescription:
                "Creates a standing SMS alert rule that will text the given phone number whenever the specified " +
                "sensor condition becomes true. Use this whenever the user asks to be notified/texted/alerted if " +
                "some condition is met (e.g. temperature, humidity, or CO2 crossing a threshold). Always confirm " +
                "the phone number and condition back to the user in your reply after calling this tool.",
            functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "phoneNumber": {
                            "type": "string",
                            "description": "The phone number to text, e.g. '+15551234567' or '555-123-4567'."
                        },
                        "sensor": {
                            "type": "string",
                            "enum": ["OUTSIDE", "UPSTAIRS", "BASEMENT", "ANY"],
                            "description": "Which sensor the condition applies to, or 'ANY' if the user didn't specify one / means any sensor."
                        },
                        "metric": {
                            "type": "string",
                            "enum": ["Temperature", "Humidity", "Co2"],
                            "description": "Which measurement to watch. Temperature is in Fahrenheit. Co2 is only reported by the Outdoor sensor."
                        },
                        "comparator": {
                            "type": "string",
                            "enum": ["Above", "Below"],
                            "description": "Whether the alert should fire when the metric goes above or below the threshold."
                        },
                        "threshold": {
                            "type": "number",
                            "description": "The numeric threshold value (Fahrenheit for Temperature, percent for Humidity, ppm for Co2)."
                        },
                        "description": {
                            "type": "string",
                            "description": "A short human-readable summary of the condition, e.g. 'Basement humidity above 70%'."
                        }
                    },
                    "required": ["phoneNumber", "sensor", "metric", "comparator", "threshold", "description"]
                }
                """));

        /// <summary>Parses the create_sms_alert tool call's arguments and saves a new SmsAlert via AlertsService.</summary>
        private string CreateSmsAlertFromToolCall(BinaryData functionArguments, int conversationId)
        {
            try
            {
                using var argsDoc = JsonDocument.Parse(functionArguments);
                var root = argsDoc.RootElement;

                var phoneNumber = root.TryGetProperty("phoneNumber", out var p) ? p.GetString() ?? string.Empty : string.Empty;
                var sensor = root.TryGetProperty("sensor", out var s) ? s.GetString() ?? "ANY" : "ANY";
                var metric = root.TryGetProperty("metric", out var m) ? m.GetString() ?? "Temperature" : "Temperature";
                var comparator = root.TryGetProperty("comparator", out var c) ? c.GetString() ?? "Above" : "Above";
                var threshold = root.TryGetProperty("threshold", out var t) ? t.GetDouble() : 0;
                var description = root.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;

                if (string.IsNullOrWhiteSpace(phoneNumber))
                {
                    return JsonSerializer.Serialize(new { error = "A phone number is required to create an SMS alert." });
                }

                var alert = _alertsService.CreateAlert(conversationId, phoneNumber.Trim(), sensor, metric, comparator, threshold,
                    string.IsNullOrWhiteSpace(description) ? $"{sensor} {metric} {comparator} {threshold}" : description);

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    alertId = alert.Id,
                    message = $"Alert created: will text {alert.PhoneNumber} when {alert.Description}."
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create SMS alert from AI tool call.");
                return JsonSerializer.Serialize(new { error = $"Could not create the alert: {ex.Message}" });
            }
        }

        /// <summary>
        /// Executes a SQL query submitted by the AI tool call. Only SELECT statements
        /// (optionally wrapped in a WITH/CTE) are permitted - any statement that could
        /// mutate data (INSERT/UPDATE/DELETE/DROP/ALTER/etc.) is rejected, since this
        /// runs against the live production database with no separate read replica.
        /// </summary>
        private string RunDatabaseQuery(string sql)
        {
            var trimmed = (sql ?? string.Empty).Trim().TrimEnd(';');

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return JsonSerializer.Serialize(new { error = "Empty query." });
            }

            if (!Regex.IsMatch(trimmed, @"^(select|with)\b", RegexOptions.IgnoreCase))
            {
                return JsonSerializer.Serialize(new { error = "Only read-only SELECT (or WITH ... SELECT) queries are allowed." });
            }

            // Guard against write keywords sneaking in via a CTE or subquery.
            var forbidden = new[] { "insert", "update", "delete", "drop", "alter", "create", "attach", "pragma", "replace", "vacuum" };
            var lowered = trimmed.ToLowerInvariant();
            foreach (var word in forbidden)
            {
                if (Regex.IsMatch(lowered, $@"\b{word}\b"))
                {
                    return JsonSerializer.Serialize(new { error = $"Query contains a disallowed keyword: {word}." });
                }
            }

            try
            {
                using var db = _dbFactory.CreateDbContext();
                var connection = db.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    connection.Open();
                }

                using var command = connection.CreateCommand();
                command.CommandText = trimmed;
                command.CommandTimeout = 10;

                using var reader = command.ExecuteReader();
                var rows = new List<Dictionary<string, object?>>();
                var rowCount = 0;
                while (reader.Read() && rowCount < 500) // hard cap so a missing LIMIT can't blow up the response
                {
                    var row = new Dictionary<string, object?>();
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        var value = reader.GetValue(i);
                        row[reader.GetName(i)] = value is DBNull ? null : value;
                    }
                    rows.Add(row);
                    rowCount++;
                }

                return JsonSerializer.Serialize(new { rowCount = rows.Count, rows });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI-submitted database query failed: {Sql}", trimmed);
                return JsonSerializer.Serialize(new { error = $"Query failed: {ex.Message}" });
            }
        }

        private async Task<string> GetAiReplyAsync(List<ReportMessage> history, int conversationId)
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

            var options = new ChatCompletionOptions();
            options.Tools.Add(QueryDatabaseTool);
            options.Tools.Add(CreateSmsAlertTool);

            try
            {
                // Allow a small number of tool-call round-trips so the AI can query the
                // database, look at the results, and (if needed) issue a follow-up query
                // before producing its final answer.
                for (var round = 0; round < 5; round++)
                {
                    ChatCompletion completion = await _chatClient.CompleteChatAsync(messages, options);

                    if (completion.FinishReason == ChatFinishReason.ToolCalls)
                    {
                        messages.Add(new AssistantChatMessage(completion));

                        foreach (var toolCall in completion.ToolCalls)
                        {
                            string resultJson;
                            if (toolCall.FunctionName == "query_database")
                            {
                                using var argsDoc = JsonDocument.Parse(toolCall.FunctionArguments);
                                var sqlArg = argsDoc.RootElement.TryGetProperty("sql", out var sqlProp)
                                    ? sqlProp.GetString() ?? string.Empty
                                    : string.Empty;
                                resultJson = RunDatabaseQuery(sqlArg);
                            }
                            else if (toolCall.FunctionName == "create_sms_alert")
                            {
                                resultJson = CreateSmsAlertFromToolCall(toolCall.FunctionArguments, conversationId);
                            }
                            else
                            {
                                resultJson = JsonSerializer.Serialize(new { error = $"Unknown tool '{toolCall.FunctionName}'." });
                            }

                            messages.Add(new ToolChatMessage(toolCall.Id, resultJson));
                        }

                        continue; // ask the model again with the tool results in context
                    }

                    return completion.Content.Count > 0 ? completion.Content[0].Text : "(No response text was returned.)";
                }

                return "Sorry, the assistant took too many steps trying to answer that. Please try rephrasing your question.";
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
            sb.AppendLine();
            sb.AppendLine("The summary above only covers the last 24 hours. You also have a 'query_database' tool that runs " +
                          "read-only SQL SELECT queries against the app's entire SQLite database (the full history of every " +
                          "Readings row, ever recorded, plus past report conversations). Use it whenever the user asks about " +
                          "data outside the last 24 hours, specific dates, longer-term trends, historical extremes, or anything " +
                          "the summary above doesn't already answer.");
            sb.AppendLine();
            sb.AppendLine("You also have a 'create_sms_alert' tool. Use it whenever the user asks to be texted/notified/alerted " +
                          "if some sensor condition happens (e.g. \"text me at 555-123-4567 if the basement gets above 80 degrees\"). " +
                          "Ask for a phone number if the user hasn't given you one yet. Alerts created this way run continuously " +
                          "in the background and appear on the Alerts page, where the user can delete them.");

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

