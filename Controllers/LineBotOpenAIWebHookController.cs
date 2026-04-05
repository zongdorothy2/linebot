using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace isRock.Template
{
    // --- 1. 對話歷史管理員 ---
    public static class ChatHistoryManager
    {
        private static readonly ConcurrentDictionary<string, List<object>> _history = new ConcurrentDictionary<string, List<object>>();
        private const int MaxHistory = 5; 
        public static List<object> GetHistory(string userId) => _history.GetOrAdd(userId, _ => new List<object>());
        public static void AddMessage(string userId, string role, string content)
        {
            var userHistory = GetHistory(userId);
            string geminiRole = role.ToLower() == "assistant" ? "model" : "user";
            userHistory.Add(new { role = geminiRole, parts = new[] { new { text = content } } });
            if (userHistory.Count > (MaxHistory * 2)) userHistory.RemoveAt(0);
        }
    }

    // --- 2. 搜尋快取管理員 ---
    public static class SearchCacheManager
    {
        private class CacheEntry { public string Result { get; set; } public DateTime ExpireTime { get; set; } }
        private static readonly ConcurrentDictionary<string, CacheEntry> _searchCache = new ConcurrentDictionary<string, CacheEntry>();
        public static bool TryGetCache(string query, out string result)
        {
            if (_searchCache.TryGetValue(query, out var entry))
            {
                if (DateTime.Now < entry.ExpireTime) { result = entry.Result; return true; }
                _searchCache.TryRemove(query, out _);
            }
            result = null; return false;
        }
        public static void SetCache(string query, string result) => _searchCache[query] = new CacheEntry { Result = result, ExpireTime = DateTime.Now.AddMinutes(30) };
    }

    // --- 3. 動畫管理員 ---
    public static class LoadingAnimationManager
    {
        public static async Task StartLoadingAsync(string accessToken, string chatId, int seconds = 20)
        {
            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(chatId)) return;
            string url = "https://api.line.me/v2/bot/chat/loading/start";
            try {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var content = new StringContent(JsonConvert.SerializeObject(new { chatId = chatId, loadingSeconds = seconds }), Encoding.UTF8, "application/json");
                await client.PostAsync(url, content);
            } catch { }
        }
    }

    // --- 4. Gemini 服務 ---
    public static class GeminiLLM
    {
        private static string ApiKey => Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
        public static async Task<string> GetResponseAsync(string userId, string userQuery)
        {
            if (SearchCacheManager.TryGetCache(userQuery, out string cachedResult)) return cachedResult;
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite-preview:generateContent?key={ApiKey}";
            var requestBody = new {
                contents = ChatHistoryManager.GetHistory(userId),
                systemInstruction = new { parts = new[] { new { text = $"你是一位資深的華德福老師。現在是台灣時間 {DateTime.UtcNow.AddHours(8):yyyy/MM/dd HH:mm}。請用溫柔堅定的語氣和家長說明。" } } },
                generationConfig = new { maxOutputTokens = 1500, temperature = 0.7 }
            };
            try {
                using var client = new HttpClient();
                var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(url, content);
                var jsonResponse = await response.Content.ReadAsStringAsync();
                dynamic? result = JsonConvert.DeserializeObject(jsonResponse);
                string textResult = result?.candidates?[0]?.content?.parts?[0]?.text ?? "我看見了光。";
                SearchCacheManager.SetCache(userQuery, textResult);
                return textResult;
            } catch { return "暫時斷線中。"; }
        }
    }

    // --- 5. GAS 試算表服務 ---
    public static class GASSheetService
    {
        private static string GasUrl => Environment.GetEnvironmentVariable("GAS_WEBAPP_URL") ?? "";
        public static async Task<string?> GetKnowledgeBaseResponseAsync(string userQuery)
        {
            try {
                using var client = new HttpClient();
                var content = new StringContent(JsonConvert.SerializeObject(new { action = "search", query = userQuery }), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(GasUrl, content);
                dynamic? result = JsonConvert.DeserializeObject(await response.Content.ReadAsStringAsync());
                if (result?.status == "success") return (string)result.answer;
            } catch { }
            return null;
        }
        public static async Task LogAiResponseAsync(string userId, string userQuery, string aiAnswer)
        {
            try {
                using var client = new HttpClient();
                var content = new StringContent(JsonConvert.SerializeObject(new { action = "log", userId = userId, query = userQuery, answer = aiAnswer }), Encoding.UTF8, "application/json");
                await client.PostAsync(GasUrl, content);
            } catch { }
        }
    }

    // --- 6. LINE WebHook 控制器 ---
    public class LineBotOpenAIWebHookController : isRock.LineBot.LineWebHookControllerBase
    {
        [HttpHead] [HttpGet] [Route("api/LineBotOpenAIWebHook")]
        public IActionResult Get() => Ok("Bot is Alive! (V2.1 Fixed)");

        [HttpPost] [Route("api/LineBotOpenAIWebHook")]
        public async Task<IActionResult> POST()
        {
            try {
                this.ChannelAccessToken = Environment.GetEnvironmentVariable("LINE_CHANNEL_TOKEN");
                var lineEvent = this.ReceivedMessage?.events?.FirstOrDefault();
                if (lineEvent == null || string.IsNullOrEmpty(lineEvent.replyToken)) return Ok();

                if (lineEvent.type.ToLower() == "message" && lineEvent.message.type == "text") {
                    string userId = lineEvent.source.userId;
                    string userText = lineEvent.message.text;

                    _ = LoadingAnimationManager.StartLoadingAsync(this.ChannelAccessToken, userId);
                    string? finalResponse = await GASSheetService.GetKnowledgeBaseResponseAsync(userText);

                    if (string.IsNullOrEmpty(finalResponse)) {
                        ChatHistoryManager.AddMessage(userId, "user", userText);
                        string aiRawAnswer = await GeminiLLM.GetResponseAsync(userId, userText);
                        ChatHistoryManager.AddMessage(userId, "assistant", aiRawAnswer);
                        finalResponse = $"{aiRawAnswer}\n\n（以上是Gemini AI協助回覆）";
                        _ = GASSheetService.LogAiResponseAsync(userId, userText, aiRawAnswer);
                    }
                    this.ReplyMessage(lineEvent.replyToken, finalResponse);
                }
                return Ok();
            } 
            catch { return Ok(); }
        } // 結束 POST 方法
    } // 結束 Controller 類別
} // 結束 Namespace
