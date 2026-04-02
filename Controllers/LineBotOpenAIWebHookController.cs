using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace isRock.Template
{
    // --- 1. 使用量管理員 (每日 50 次限制) ---
    public static class UsageManager
    {
        private static int _todayCount = 0;
        private static DateTime _nextResetTime = GetNextResetTime();
        private static readonly object _lock = new object();

        private static DateTime GetNextResetTime()
        {
            DateTime now = DateTime.Now;
            DateTime today8AM = new DateTime(now.Year, now.Month, now.Day, 8, 0, 0);
            return now < today8AM ? today8AM : today8AM.AddDays(1);
        }

        public static int GetAndIncrementCount(out bool isOverLimit)
        {
            lock (_lock)
            {
                if (DateTime.Now >= _nextResetTime)
                {
                    _todayCount = 0;
                    _nextResetTime = GetNextResetTime();
                    Console.WriteLine($">>> [系統通知] 已到達 08:00，重置計數器。");
                }
                isOverLimit = _todayCount >= 50;
                if (!isOverLimit) _todayCount++;
                return _todayCount;
            }
        }
    }

    // --- 2. 對話歷史管理員 (記憶最近 10 則對話) ---
    public static class ChatHistoryManager
    {
        private static readonly ConcurrentDictionary<string, List<dynamic>> _history = new ConcurrentDictionary<string, List<dynamic>>();
        private const int MaxHistory = 10; 

        public static List<dynamic> GetHistory(string userId)
        {
            return _history.GetOrAdd(userId, new List<dynamic>());
        }

        public static void AddMessage(string userId, string role, string content)
        {
            var userHistory = GetHistory(userId);
            userHistory.Add(new { role = role, content = content });
            if (userHistory.Count > MaxHistory) userHistory.RemoveAt(0);
        }
    }

    // --- 3. 新增：搜尋快取管理員 (節省 Serper 點數) ---
    public static class SearchCacheManager
    {
        private class CacheEntry {
            public string Result { get; set; }
            public DateTime ExpireTime { get; set; }
        }

        private static readonly ConcurrentDictionary<string, CacheEntry> _searchCache = new ConcurrentDictionary<string, CacheEntry>();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30); // 設定快取有效時間為 30 分鐘

        public static bool TryGetCache(string query, out string result)
        {
            if (_searchCache.TryGetValue(query, out var entry))
            {
                if (DateTime.Now < entry.ExpireTime)
                {
                    result = entry.Result;
                    return true;
                }
                _searchCache.TryRemove(query, out _); // 過期則移除
            }
            result = null;
            return false;
        }

        public static void SetCache(string query, string result)
        {
            var entry = new CacheEntry {
                Result = result,
                ExpireTime = DateTime.Now.Add(CacheDuration)
            };
            _searchCache[query] = entry;
        }
    }

    // --- 4. 主程式控制器 ---
    public class LineBotOpenAIWebHookController : isRock.LineBot.LineWebHookControllerBase
    {
        [HttpHead]
        [HttpGet]
        [Route("api/LineBotOpenAIWebHook")]
        public IActionResult Get() => Ok("Bot is Alive!");

        [Route("api/LineBotOpenAIWebHook")]
        [HttpPost]
        public async Task<IActionResult> POST()
        {
            try
            {
                this.ChannelAccessToken = "+TMqgSuSc5xQ3exc9raMYDXo+TMC6wDV7JrtcmZ0fxWWnWotHZt9zdFpciHI8nrV4lUqjXmbJgNpxvlcQx6axHyJYJevUP2tRSWfIjItxlgqrSXz1+YJjAJuT2IxedI+EiifbH4MPQLxxTDlmWE1pQdB04t89/1O/w1cDnyilFU=";

                var LineEvent = this.ReceivedMessage?.events?.FirstOrDefault();
                if (LineEvent == null || LineEvent.replyToken == "00000000000000000000000000000000") return Ok();

                if (LineEvent.type.ToLower() == "message" && LineEvent.message.type == "text")
                {
                    string userId = LineEvent.source.userId;
                    string userMsg = LineEvent.message.text;
                    bool isPrivate = LineEvent.source.type.ToLower() == "user";
                    bool isMentioned = LineEvent.message.mention?.mentionees?.Any(m => m.isSelf == true) ?? false;

                    if (isPrivate || isMentioned)
                    {
                        bool isOverLimit;
                        int currentCount = UsageManager.GetAndIncrementCount(out isOverLimit);

                        if (isOverLimit)
                        {
                            this.ReplyMessage(LineEvent.replyToken, "🌟 今日互動額度已滿，明早 8 點再相見。");
                            return Ok();
                        }

                        // B. 執行搜尋 (內含快取檢查邏輯)
                        string searchResults = "";
                        if (userMsg.Contains("天氣") || userMsg.Contains("氣溫")) {
                            searchResults = await KeylessSearchService.GetWeatherInfoAsync(userMsg);
                        } else {
                            searchResults = await SerperSearchService.GoogleSearchWithCacheAsync(userMsg);
                        }

                        ChatHistoryManager.AddMessage(userId, "user", userMsg);

                        var userHistory = ChatHistoryManager.GetHistory(userId);
                        string responseMsg = await LLM.getResponseWithHistoryAsync(userHistory, searchResults);

                        ChatHistoryManager.AddMessage(userId, "assistant", responseMsg);

                        string finalMsg = $@"{responseMsg}

---
### 今日互動紀錄：第 {currentCount} 次";
                        this.ReplyMessage(LineEvent.replyToken, finalMsg);
                    }
                }
                return Ok();
            }
            catch (Exception ex)
            {
                Console.WriteLine($">>> [系統錯誤]: {ex.Message}");
                return Ok();
            }
        }
    }

    // --- 5. 搜尋服務 (整合快取) ---
    public class SerperSearchService
    {
        private const string SerperApiKey = "82153d7f3577fc91edf635839630e669623360e3";

        public static async Task<string> GoogleSearchWithCacheAsync(string query)
        {
            // 1. 先看快取裡有沒有
            if (SearchCacheManager.TryGetCache(query, out string cachedResult))
            {
                Console.WriteLine($">>> [快取命中] 使用舊有搜尋結果，省下 1 點 Serper 額度。關鍵字: {query}");
                return cachedResult;
            }

            // 2. 沒找到才呼叫 API
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("X-API-KEY", SerperApiKey);
                    var payload = new { q = query, gl = "tw", hl = "zh-tw" };
                    var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                    
                    var response = await client.PostAsync("https://google.serper.dev/search", content);
                    var json = await response.Content.ReadAsStringAsync();
                    var data = JsonConvert.DeserializeObject<dynamic>(json);

                    StringBuilder sb = new StringBuilder();
                    if (data.answerBox != null) sb.AppendLine($"[精選答案]: {data.answerBox.answer ?? data.answerBox.snippet}");
                    if (data.organic != null) {
                        foreach (var item in ((IEnumerable<dynamic>)data.organic).Take(3)) 
                            sb.AppendLine($"- {item.title}: {item.snippet}");
                    }

                    string finalResult = sb.Length > 0 ? sb.ToString() : "目前查無即時資訊";

                    // 3. 存入快取
                    SearchCacheManager.SetCache(query, finalResult);

                    return finalResult;
                }
            }
            catch { return "搜尋連線失敗"; }
        }
    }

    // --- 6. 天氣服務 ---
    public class KeylessSearchService
    {
        public static async Task<string> GetWeatherInfoAsync(string query)
        {
            try {
                string lat = query.Contains("台東") ? "22.75" : "25.03";
                string lon = query.Contains("台東") ? "121.15" : "121.56";
                string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current_weather=true&timezone=Asia%2FTaipei";
                using (var client = new HttpClient()) {
                    var json = await client.GetStringAsync(url);
                    var data = JsonConvert.DeserializeObject<dynamic>(json);
                    return $"[即時氣象] 氣溫: {data.current_weather.temperature}°C";
                }
            } catch { return "天氣數據暫時無法取得"; }
        }
    }

    // --- 7. AI 核心 ---
    public class LLM
    {
        private static string GitHubModelKey => Environment.GetEnvironmentVariable("GITHUB_MODEL_KEY") ?? "";

        public static async Task<string> getResponseWithHistoryAsync(List<dynamic> history, string searchContext)
        {
            if (string.IsNullOrEmpty(GitHubModelKey)) return "錯誤：API 金鑰未設定。";

            var messages = new List<dynamic>();
            messages.Add(new {
                role = "system",
                content = $@"你是一位溫暖的華德福導師。現在時間 {DateTime.Now:yyyy/MM/dd HH:mm}。
請結合搜尋結果與先前的對話脈絡回答。

【搜尋參考數據】：
{searchContext}"
            });

            messages.AddRange(history);

            var MessageBody = new { model = "gpt-4o", messages = messages };

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {GitHubModelKey}");
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Add("User-Agent", "DotNetApp");

                var content = new StringContent(JsonConvert.SerializeObject(MessageBody), Encoding.UTF8, "application/json");
                var response = await client.PostAsync("https://models.github.ai/inference/chat/completions", content);

                if (!response.IsSuccessStatusCode) return "AI 正在森林裡散步，請稍後再試。";

                var resultString = await response.Content.ReadAsStringAsync();
                var obj = JsonConvert.DeserializeObject<dynamic>(resultString);
                return obj.choices[0].message.content.Value;
            }
        }
    }
}
