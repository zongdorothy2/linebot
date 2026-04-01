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
        public IActionResult POST()
        {
            // 注意：如果您沒有正確的 Admin User ID，請保持這裡為空或維持現狀，
            // 但我已經在下方 catch 區塊做了防呆，不會再讓它導致程式崩潰。
            const string AdminUserId = "34f6f75d30772c7d4a1605f1cf4a86e8"; 

            try
            {
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

                        // 智慧判斷天氣
                        if (userMsg.Contains("天氣") || userMsg.Contains("氣溫")) {
                            searchResults = KeylessSearchService.GetWeatherInfo(userMsg);
                        } else {
                            searchResults = KeylessSearchService.GoogleSearch(userMsg);
                        }
                        
                        // 終端機顯示抓到的資料內容
                        Console.WriteLine($">>> [系統診斷] 抓取結果: {searchResults}");

                        // 呼叫 LLM
                        string responseMsg = LLM.getResponse(userMsg, searchResults);
                        
                        // 執行回覆
                        this.ReplyMessage(LineEvent.replyToken, responseMsg);
                    }
                }
                return Ok();
            }
            catch (Exception ex)
            {
                // 改進：只在終端機印出錯誤，不強制 PushMessage 避免二次崩潰
                Console.WriteLine($">>> [重要錯誤]: {ex.Message}");
                return Ok();
            }
        }
    }

    public class KeylessSearchService
    {
        public static string GetWeatherInfo(string query)
        {
            try {
                // 座標判斷 (台東 22.7, 121.1 / 台北 25.0, 121.5)
                string lat = query.Contains("台東") ? "22.75" : "25.03";
                string lon = query.Contains("台東") ? "121.15" : "121.56";
                string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current_weather=true&timezone=Asia%2FTaipei";
                
                using (var client = new HttpClient()) {
                    var json = client.GetStringAsync(url).Result;
                    var data = JsonConvert.DeserializeObject<dynamic>(json);
                    return $"[即時氣象數據] 氣溫: {data.current_weather.temperature}°C, 風速: {data.current_weather.windspeed} km/h, 觀測時間: {data.current_weather.time}";
                }
            } catch { return "天氣資訊獲取失敗"; }
        }

        public static string GoogleSearch(string query)
        {
            try {
                using (var client = new HttpClient()) {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36");
                    string url = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}&gbv=1";
                    var html = client.GetStringAsync(url).Result;
                    var matches = Regex.Matches(html, @"<(div|span) class=""[^""]*(BNeawe s3v9rd AP7Wnd)[^""]*"">([^<]+)</(div|span)>");
                    
                    if (matches.Count == 0) return "目前查無即時新聞";
                    return string.Join("\n", matches.Cast<Match>().Take(3).Select(m => "- " + m.Groups[3].Value));
                }
            } catch { return "搜尋連線受阻"; }
        }
    }

    
{
    // 1. 刪除原本的 const string，改用變數讀取
    // 注意：Environment 讀取不是常數，所以不能用 const
    private static string GitHubModelKey => Environment.GetEnvironmentVariable("GITHUB_MODEL_KEY") ?? "";

    public static string getResponse(string userMsg, string searchContext)
    {
        // 2. 檢查金鑰是否存在，避免程式崩潰
        if (string.IsNullOrEmpty(GitHubModelKey))
        {
            return "錯誤：找不到 API 金鑰，請在 Render 設定環境變數 GITHUB_MODEL_KEY。";
        }

        var MessageBody = new
        {
            model = "gpt-4o", 
            messages = new[]
            {
                new
                {
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
            // 必須加入 User-Agent 否則有些 AI 服務會拒絕連線
            client.DefaultRequestHeaders.Add("User-Agent", "DotNetApp");

            var content = new StringContent(JsonConvert.SerializeObject(MessageBody), Encoding.UTF8, "application/json");
            
            var response = client.PostAsync("https://models.github.ai/inference/chat/completions", content).Result;
            
            // 3. 增加簡單的錯誤檢查
            if (!response.IsSuccessStatusCode)
            {
                return $"AI 暫時無法回應 (HTTP {response.StatusCode})";
            }

            var resultString = response.Content.ReadAsStringAsync().Result;
            var obj = JsonConvert.DeserializeObject<dynamic>(resultString);
            
            return obj.choices[0].message.content.Value;
        }
    }
}
