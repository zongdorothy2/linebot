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

        public static List<object> GetHistory(string userId)
        {
            return _history.GetOrAdd(userId, _ => new List<object>());
        }

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

        public static void SetCache(string query, string result)
        {
            _searchCache[query] = new CacheEntry { Result = result, ExpireTime = DateTime.Now.AddMinutes(30) };
        }
    }

    // --- 3. 動畫管理員 ---
    public static class LoadingAnimationManager
    {
        public static async Task StartLoadingAsync(string accessToken, string chatId, int seconds = 20)
        {
            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(chatId)) return;
            string url = "https://api.line.me/v2/bot/chat/loading/start";
            var requestBody = new { chatId = chatId, loadingSeconds = seconds };
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
                await client.PostAsync(url, content);
            }
            catch (Exception ex) { Console.WriteLine($"Loading Error: {ex.Message}"); }
        }
    }

    // --- 4. Gemini 服務 (簡化 Token 處理) ---
    public static class GeminiLLM
    {
        private static string ApiKey => Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
        private const string ModelName = "gemini-3.1-flash-lite-preview";

        public static async Task<string> GetResponseAsync(string userId, string userQuery)
        {
            if (SearchCacheManager.TryGetCache(userQuery, out string cachedResult)) return cachedResult;

            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{ModelName}:generateContent?key={ApiKey}";
            string currentTimeInfo = DateTime.UtcNow.AddHours(8).ToString("yyyy/MM/dd dddd HH:mm");

            var requestBody = new {
                contents = ChatHistoryManager.GetHistory(userId),
                systemInstruction = new { 
                    parts = new[] { new { text = $"你是一位資深的教育人員。現在是台灣時間 {currentTimeInfo}。請用溫柔而堅定的語氣對話。" } } 
                },
                generationConfig = new { maxOutputTokens = 1500, temperature = 0.7 }
            };

            try 
            {
                using var client = new HttpClient();
                var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(url, content);
                var jsonResponse = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode) return "導師正在沉思，請稍後再試。";

                dynamic? result = JsonConvert.DeserializeObject(jsonResponse);
                string textResult = result?.candidates?[0]?.content?.parts?[0]?.text ?? "我看見了光。";

                SearchCacheManager.SetCache(userQuery, textResult);
                return textResult;
            }
            catch { return "導師暫時切斷了與外界的聯繫。"; }
        }
    }

    // --- 5. GAS 試算表檢索服務 ---
    public static class GASSheetService
    {
        private static string GasUrl => Environment.GetEnvironmentVariable("GAS_WEBAPP_URL") ?? "";

        public static async Task<string?> GetKnowledgeBaseResponseAsync(string userQuery)
        {
            if (string.IsNullOrEmpty(GasUrl)) return null;
            try
            {
                using var client = new HttpClient();
                var requestBody = new { action = "search", query = userQuery };
                var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(GasUrl, content);
                if (!response.IsSuccessStatusCode) return null;

                var jsonResponse = await response.Content.ReadAsStringAsync();
                dynamic? result = JsonConvert.DeserializeObject(jsonResponse);
                if (result?.status == "success") return (string)result.answer;
            }
            catch (Exception ex) { Console.WriteLine($"GAS Error: {ex.Message}"); }
            return null;
        }
    }

    // --- 6. LINE WebHook 控制器 ---
    public class LineBotOpenAIWebHookController : isRock.LineBot.LineWebHookControllerBase
    {
        [Route("api/LineBotOpenAIWebHook")]
        [HttpPost]
        public async Task<IActionResult> POST()
        {
            try
            {
                this.ChannelAccessToken = Environment.GetEnvironmentVariable("LINE_CHANNEL_TOKEN");
                var lineEvent = this.ReceivedMessage?.events?.FirstOrDefault();
                if (lineEvent == null || string.IsNullOrEmpty(lineEvent.replyToken)) return Ok();

                if (lineEvent.type.ToLower() == "message" && lineEvent.message.type == "text")
                {
                    string userId = lineEvent.source.userId;
                    string userText = lineEvent.message.text;

                    // 步驟 A: 啟動動畫 (非阻塞)
                    _ = LoadingAnimationManager.StartLoadingAsync(this.ChannelAccessToken, userId);

                    // 步驟 B: 優先從 GAS 獲取答案
                    string? finalResponse = await GASSheetService.GetKnowledgeBaseResponseAsync(userText);

                    if (string.IsNullOrEmpty(finalResponse))
                    {
                        // 步驟 C: GAS 沒找到，才叫 Gemini AI
                        ChatHistoryManager.AddMessage(userId, "user", userText);
                        finalResponse = await GeminiLLM.GetResponseAsync(userId, userText);
                        ChatHistoryManager.AddMessage(userId, "assistant", finalResponse);
                    }

                    // 步驟 D: 直接回覆內容 (移除計次與 Token 顯示)
                    this.ReplyMessage(lineEvent.replyToken, finalResponse);
                }
                return Ok();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                return Ok();
            }
        }
    }
}
