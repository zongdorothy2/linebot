using System;
using System.Collections.Generic;
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
    // --- 1. 使用量管理員 (負責計次與重置邏輯) ---
    public static class UsageManager
    {
        private static int _todayCount = 0;
        private static DateTime _nextResetTime = GetNextResetTime();
        private static readonly object _lock = new object();

        private static DateTime GetNextResetTime()
        {
            DateTime now = DateTime.Now;
            // 設定為今天的 08:00:00
            DateTime today8AM = new DateTime(now.Year, now.Month, now.Day, 8, 0, 0);
            // 如果現在已經過了 8 點，下一個重置點就是明天 8 點
            return now < today8AM ? today8AM : today8AM.AddDays(1);
        }

        public static int GetAndIncrementCount(out bool isOverLimit)
        {
            lock (_lock)
            {
                // 檢查是否到達重置時間
                if (DateTime.Now >= _nextResetTime)
                {
                    _todayCount = 0;
                    _nextResetTime = GetNextResetTime();
                    Console.WriteLine($">>> [系統通知] 已到達 08:00，重置計數器。");
                }

                isOverLimit = _todayCount >= 50;

                if (!isOverLimit)
                {
                    _todayCount++;
                }

                return _todayCount;
            }
        }
    }

    // --- 2. 主程式控制器 ---
    public class LineBotOpenAIWebHookController : isRock.LineBot.LineWebHookControllerBase
    {
        // ---------------------------------------------------------
        // ---------------------------------------------------------
// 修改後：同時支援 HEAD 與 GET，徹底解決監控工具的 405 錯誤
// ---------------------------------------------------------
[HttpHead] // 新增這行，專門應對監控工具的偵測
[HttpGet]
[Route("api/LineBotOpenAIWebHook")]
public IActionResult Get()
{
    // 當監控工具用 HEAD 請求時，它只會接收到 200 OK，不會抓取 Body 內容，節省流量
    return Ok("Bot is Alive!");
}

        [Route("api/LineBotOpenAIWebHook")]
        [HttpPost]
        public async Task<IActionResult> POST() // 改為非同步 Task
        {
            // 管理員 ID (保留供後續管理功能使用)
            const string AdminUserId = "34f6f75d30772c7d4a1605f1cf4a86e8";

            try
            {
                // LINE 頻道 Token
                this.ChannelAccessToken = "+TMqgSuSc5xQ3exc9raMYDXo+TMC6wDV7JrtcmZ0fxWWnWotHZt9zdFpciHI8nrV4lUqjXmbJgNpxvlcQx6axHyJYJevUP2tRSWfIjItxlgqrSXz1+YJjAJuT2IxedI+EiifbH4MPQLxxTDlmWE1pQdB04t89/1O/w1cDnyilFU=";

                var LineEvent = this.ReceivedMessage?.events?.FirstOrDefault();
                if (LineEvent == null || LineEvent.replyToken == "00000000000000000000000000000000") return Ok();

                if (LineEvent.type.ToLower() == "message" && LineEvent.message.type == "text")
                {
                    string userMsg = LineEvent.message.text;
                    bool isPrivate = LineEvent.source.type.ToLower() == "user";
                    bool isMentioned = LineEvent.message.mention?.mentionees?.Any(m => m.isSelf == true) ?? false;

                    if (isPrivate || isMentioned)
                    {
                        // A. 檢查次數限制
                        bool isOverLimit;
                        int currentCount = UsageManager.GetAndIncrementCount(out isOverLimit);

                        if (isOverLimit)
                        {
                            this.ReplyMessage(LineEvent.replyToken, "🌟 今日的 50 次溫暖陪伴額度已滿，讓我們等明早 8 點再相見吧。");
                            return Ok();
                        }

                        // B. 搜尋邏輯 (加入延遲與非同步)
                        string searchResults = "";
                        if (userMsg.Contains("天氣") || userMsg.Contains("氣溫")) {
                            searchResults = await KeylessSearchService.GetWeatherInfoAsync(userMsg);
                        } else {
                            searchResults = await KeylessSearchService.GoogleSearchAsync(userMsg);
                        }

                        Console.WriteLine($">>> [系統診斷] 今日第 {currentCount} 次抓取結果: {searchResults}");

                        // C. 呼叫 LLM
                        string responseMsg = await LLM.getResponseAsync(userMsg, searchResults);

                        // D. 組合最終訊息腳註
                        string finalMsg = $@"{responseMsg}

---
### 今日互動紀錄：這是今日第 {currentCount} 次互動";

                        this.ReplyMessage(LineEvent.replyToken, finalMsg);
                    }
                }
                return Ok();
            }
            catch (Exception ex)
            {
                Console.WriteLine($">>> [重要錯誤]: {ex.Message}");
                return Ok();
            }
        }
    }

    // --- 3. 搜尋服務 ---
    public class KeylessSearchService
    {
        private static readonly Random _random = new Random();

        public static async Task<string> GetWeatherInfoAsync(string query)
        {
            try {
                string lat = query.Contains("台東") ? "22.75" : "25.03";
                string lon = query.Contains("台東") ? "121.15" : "121.56";
                string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current_weather=true&timezone=Asia%2FTaipei";

                using (var client = new HttpClient()) {
                    var json = await client.GetStringAsync(url);
                    var data = JsonConvert.DeserializeObject<dynamic>(json);
                    return $"[即時氣象數據] 氣溫: {data.current_weather.temperature}°C, 風速: {data.current_weather.windspeed} km/h";
                }
            } catch { return "天氣資訊暫時無法取得"; }
        }

        public static async Task<string> GoogleSearchAsync(string query)
        {
            try {
                await Task.Delay(_random.Next(1000, 2500));

                using (var client = new HttpClient()) {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
                    string url = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}&gbv=1";
                    var html = await client.GetStringAsync(url);
                    var matches = Regex.Matches(html, @"<(div|span) class=""[^""]*(BNeawe s3v9rd AP7Wnd)[^""]*"">([^<]+)</(div|span)>");

                    if (matches.Count == 0) return "目前查無即時相關資訊";
                    return string.Join("\n", matches.Cast<Match>().Take(3).Select(m => "- " + m.Groups[3].Value));
                }
            } catch { return "搜尋連線受阻，請稍後再試"; }
        }
    }

    // --- 4. AI 模型呼叫 ---
    public class LLM
    {
        private static string GitHubModelKey => Environment.GetEnvironmentVariable("GITHUB_MODEL_KEY") ?? "";

        public static async Task<string> getResponseAsync(string userMsg, string searchContext)
        {
            if (string.IsNullOrEmpty(GitHubModelKey))
                return "錯誤：找不到 API 金鑰。請在環境變數設定 GITHUB_MODEL_KEY。";

            var MessageBody = new
            {
                model = "gpt-4o",
                messages = new[]
                {
                    new {
                        role = "system",
                        content = $@"你是一位溫暖的華德福導師。
現在時間是 {DateTime.Now:yyyy/MM/dd HH:mm}。
請優先參考以下搜尋數據來回答，並用優美的語言轉述。

【參考數據】：
{searchContext}"
                    },
                    new { role = "user", content = userMsg },
                },
            };

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {GitHubModelKey}");
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Add("User-Agent", "DotNetApp");

                var content = new StringContent(JsonConvert.SerializeObject(MessageBody), Encoding.UTF8, "application/json");
                var response = await client.PostAsync("https://models.github.ai/inference/chat/completions", content);

                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    return $"AI 暫時休息中 (HTTP {response.StatusCode})。";
                }

                var resultString = await response.Content.ReadAsStringAsync();
                var obj = JsonConvert.DeserializeObject<dynamic>(resultString);
                return obj.choices[0].message.content.Value;
            }
        }
    }
}
