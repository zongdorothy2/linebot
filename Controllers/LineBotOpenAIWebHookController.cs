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
    public class LineBotOpenAIWebHookController : isRock.LineBot.LineWebHookControllerBase
    {
        [Route("api/LineBotOpenAIWebHook")]
        [HttpPost]
        public async Task<IActionResult> POST() // 改為 async Task
        {
            // 管理員 ID
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
                        string searchResults = "";

                        // 智慧判斷天氣 (改為 await)
                        if (userMsg.Contains("天氣") || userMsg.Contains("氣溫")) {
                            searchResults = await KeylessSearchService.GetWeatherInfoAsync(userMsg);
                        } else {
                            searchResults = await KeylessSearchService.GoogleSearchAsync(userMsg);
                        }

                        // 終端機顯示抓到的資料內容
                        Console.WriteLine($">>> [系統診斷] 抓取結果: {searchResults}");

                        // 呼叫 LLM (改為 await)
                        string responseMsg = await LLM.getResponseAsync(userMsg, searchResults);

                        // 執行回覆
                        this.ReplyMessage(LineEvent.replyToken, responseMsg);
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
                    return $"[即時氣象數據] 氣溫: {data.current_weather.temperature}°C, 風速: {data.current_weather.windspeed} km/h, 觀測時間: {data.current_weather.time}";
                }
            } catch { return "天氣資訊獲取失敗"; }
        }

        public static async Task<string> GoogleSearchAsync(string query)
        {
            try {
                // --- 加入隨機延遲 1.5 ~ 4 秒，降低被偵測率 ---
                await Task.Delay(_random.Next(1500, 4000));

                using (var client = new HttpClient()) {
                    // 模擬真實瀏覽器 Header
                    client.DefaultRequestHeaders.UserAgent.Clear();
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
                    
                    string url = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}&gbv=1";
                    var html = await client.GetStringAsync(url);
                    var matches = Regex.Matches(html, @"<(div|span) class=""[^""]*(BNeawe s3v9rd AP7Wnd)[^""]*"">([^<]+)</(div|span)>");

                    if (matches.Count == 0) return "目前查無即時新聞";
                    return string.Join("\n", matches.Cast<Match>().Take(3).Select(m => "- " + m.Groups[3].Value));
                }
            } catch (Exception ex) { 
                Console.WriteLine($">>> [搜尋失敗]: {ex.Message}");
                return "搜尋服務暫時忙碌中"; 
            }
        }
    }

    public class LLM
    {
        private static string GitHubModelKey => Environment.GetEnvironmentVariable("GITHUB_MODEL_KEY") ?? "";

        public static async Task<string> getResponseAsync(string userMsg, string searchContext)
        {
            if (string.IsNullOrEmpty(GitHubModelKey))
            {
                return "錯誤：找不到 API 金鑰，請在 Render 設定環境變數 GITHUB_MODEL_KEY。";
            }

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
                client.DefaultRequestHeaders.Add("User-Agent", "DotNetApp-V2");

                var content = new StringContent(JsonConvert.SerializeObject(MessageBody), Encoding.UTF8, "application/json");
                
                // 改為非同步 Post
                var response = await client.PostAsync("https://models.github.ai/inference/chat/completions", content);

                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    return $"AI 暫時無法回應 (HTTP {response.StatusCode})。原因可能是請求太頻繁，請稍後再試。";
                }

                var resultString = await response.Content.ReadAsStringAsync();
                var obj = JsonConvert.DeserializeObject<dynamic>(resultString);
                return obj.choices[0].message.content.Value;
            }
        }
    }
}
