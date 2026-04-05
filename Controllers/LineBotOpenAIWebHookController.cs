記錄下LINE Bot GAS 整合 Gemini 2.2版（客服機器人含監控儀表板）
https://linebot-b09v.onrender.com/admin/monitor
語法如下：
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace isRock.Template
{
    // --- 1. 監控統計服務 ---
    public static class MonitorService
    {
        private static int _totalRequests = 0;
        private static readonly ConcurrentDictionary<string, int> _keywordStats = new ConcurrentDictionary<string, int>();
        private static readonly ConcurrentDictionary<string, DateTime> _activeUsers = new ConcurrentDictionary<string, DateTime>();
        private static DateTime _startTime = DateTime.UtcNow.AddHours(8);

        public static void RecordRequest(string userId, string text)
        {
            Interlocked.Increment(ref _totalRequests);
            _activeUsers[userId] = DateTime.UtcNow.AddHours(8);
            if (!string.IsNullOrEmpty(text) && text.Length > 1)
                _keywordStats.AddOrUpdate(text, 1, (k, v) => v + 1);
        }

        public static void Reset()
        {
            Interlocked.Exchange(ref _totalRequests, 0);
            _keywordStats.Clear();
            _activeUsers.Clear();
            _startTime = DateTime.UtcNow.AddHours(8);
        }

        public static dynamic GetSnapshot()
        {
            return new {
                TotalRequests = _totalRequests,
                Uptime = (DateTime.UtcNow.AddHours(8) - _startTime).ToString(@"dd\.hh\:mm\:ss"),
                TopKeywords = _keywordStats.OrderByDescending(x => x.Value).Take(10).ToList(),
                ActiveUserCount = _activeUsers.Count,
                RecentUsers = _activeUsers.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key).ToList()
            };
        }
    }

    // --- 2. 管理員控制器 ---
    public class AdminController : Controller
    {
        [HttpGet] [Route("admin/monitor")]
        public IActionResult Index()
        {
            var data = MonitorService.GetSnapshot();
            string html = $@"
            <html>
            <head>
                <title>均一國小部客服 Bot 監控後台</title>
                <link rel='stylesheet' href='https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/css/bootstrap.min.css'>
                <meta http-equiv='refresh' content='30'>
            </head>
            <body class='container mt-5' style='background-color:#f8f9fa;'>
                <div class='p-5 mb-4 bg-white rounded-3 shadow-sm'>
                    <div class='d-flex justify-content-between align-items-center mb-3'>
                        <h2 class='display-6 text-primary mb-0'>🌟 均一國小部客服 Bot 監控儀表板</h2>
                        <form action='/admin/reset' method='post' onsubmit='return confirm(""確定要清空所有統計數據嗎？"");'>
                            <button type='submit' class='btn btn-outline-danger'>重置統計數據</button>
                        </form>
                    </div>
                    <p class='text-muted'>數據自最後一次重置後開始統計。頁面每 30 秒自動刷新。</p>
                    <hr>
                    <div class='row text-center mt-4'>
                        <div class='col-md-4'><div class='card shadow-sm'><div class='card-body'><h6 class='text-muted'>統計週期內總請求</h6><h3>{data.TotalRequests}</h3></div></div></div>
                        <div class='col-md-4'><div class='card shadow-sm'><div class='card-body'><h6 class='text-muted'>統計週期內家長數</h6><h3>{data.ActiveUserCount}</h3></div></div></div>
                        <div class='col-md-4'><div class='card shadow-sm'><div class='card-body'><h6 class='text-muted'>數據累積時長</h6><h3>{data.Uptime}</h3></div></div></div>
                    </div>
                    <div class='row mt-5'>
                        <div class='col-md-7'>
                            <h5 class='mb-3'>🔥 熱門提問關鍵字 (Top 10)</h5>
                            <table class='table table-hover bg-white'>
                                <thead class='table-light'><tr><th>關鍵字</th><th class='text-end'>次數</th></tr></thead>
                                <tbody>";
            foreach (var item in data.TopKeywords) html += $"<tr><td>{item.Key}</td><td class='text-end'>{item.Value}</td></tr>";
            html += $@"</tbody></table></div>
                        <div class='col-md-5'>
                            <h5 class='mb-3'>👤 最近活躍家長 (ID)</h5>
                            <ul class='list-group shadow-sm'>";
            foreach (var user in data.RecentUsers) html += $"<li class='list-group-item small text-truncate'>{user}</li>";
            html += @"</ul></div></div></div></body></html>";
            return Content(html, "text/html", Encoding.UTF8);
        }

        [HttpPost] [Route("admin/reset")]
        public IActionResult ResetData() { MonitorService.Reset(); return RedirectToAction("Index"); }
    }

    // --- 3. 歷史紀錄與快取 ---
    public static class ChatHistoryManager
    {
        private static readonly ConcurrentDictionary<string, List<object>> _history = new ConcurrentDictionary<string, List<object>>();
        public static List<object> GetHistory(string userId) => _history.GetOrAdd(userId, _ => new List<object>());
        public static void AddMessage(string userId, string role, string content)
        {
            var userHistory = GetHistory(userId);
            userHistory.Add(new { role = (role.ToLower() == "assistant" ? "model" : "user"), parts = new[] { new { text = content } } });
            if (userHistory.Count > 10) userHistory.RemoveAt(0);
        }
    }

    public static class SearchCacheManager
    {
        private static readonly ConcurrentDictionary<string, (string res, DateTime exp)> _cache = new ConcurrentDictionary<string, (string, DateTime)>();
        public static bool TryGetCache(string q, out string r) { if (_cache.TryGetValue(q, out var e) && DateTime.Now < e.exp) { r = e.res; return true; } r = null; return false; }
        public static void SetCache(string q, string r) => _cache[q] = (r, DateTime.Now.AddMinutes(30));
    }

    // --- 4. 服務類別 (修正參數命名問題) ---
    public static class LoadingAnimationManager
    {
        public static async Task StartLoadingAsync(string? token, string? userId)
        {
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(userId)) return;
            try { 
                using var client = new HttpClient(); 
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                await client.PostAsync("https://api.line.me/v2/bot/chat/loading/start", 
                    new StringContent(JsonConvert.SerializeObject(new { chatId = userId, loadingSeconds = 20 }), Encoding.UTF8, "application/json"));
            } catch { }
        }
    }

    public static class GeminiLLM
    {
        public static async Task<string> GetResponseAsync(string userId, string userQuery)
        {
            if (SearchCacheManager.TryGetCache(userQuery, out string cached)) return cached;
            string key = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
            
            // 統一使用 userId 與 userQuery
            var requestObj = new { 
                contents = ChatHistoryManager.GetHistory(userId), 
                systemInstruction = new { parts = new[] { new { text = $"你是一位資深的華德福老師。現在是台灣時間 {DateTime.UtcNow.AddHours(8):yyyy/MM/dd HH:mm}。請用溫柔堅定的語氣和家長說明。" } } },

generationConfig = new { maxOutputTokens = 1500, temperature = 0.7 }

};

            try { 
                using var client = new HttpClient(); 
                var res = await client.PostAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite-preview:generateContent?key={key}", 
                    new StringContent(JsonConvert.SerializeObject(requestObj), Encoding.UTF8, "application/json"));
                
                var jsonResponse = await res.Content.ReadAsStringAsync();
                dynamic? result = JsonConvert.DeserializeObject(jsonResponse);
                string textResult = result?.candidates?[0]?.content?.parts?[0]?.text ?? "我看見了光。";
                
                SearchCacheManager.SetCache(userQuery, textResult); 
                return textResult;
            } catch { return "暫時連線不穩定。"; }
        }
    }

    public static class GASSheetService
    {
        private static string Url => Environment.GetEnvironmentVariable("GAS_WEBAPP_URL") ?? "";
        public static async Task<string?> GetResponseAsync(string userQuery)
        {
            if (string.IsNullOrEmpty(Url)) return null;
            try { 
                using var client = new HttpClient(); 
                var res = await client.PostAsync(Url, new StringContent(JsonConvert.SerializeObject(new { action = "search", query = userQuery }), Encoding.UTF8, "application/json"));
                dynamic? result = JsonConvert.DeserializeObject(await res.Content.ReadAsStringAsync());
                return result?.status == "success" ? (string)result.answer : null;
            } catch { return null; }
        }
        public static async Task LogAsync(string userId, string userQuery, string aiAnswer)
        {
            if (string.IsNullOrEmpty(Url)) return;
            try { 
                using var client = new HttpClient(); 
                await client.PostAsync(Url, new StringContent(JsonConvert.SerializeObject(new { action = "log", userId = userId, query = userQuery, answer = aiAnswer }), Encoding.UTF8, "application/json")); 
            } catch { }
        }
    }

    // --- 5. LINE WebHook 控制器 ---
    public class LineBotOpenAIWebHookController : isRock.LineBot.LineWebHookControllerBase
    {
        [HttpGet] [Route("api/LineBotOpenAIWebHook")]
        public IActionResult Get() => Ok("Bot is Alive! 04/06版");
        [Route("api/LineBotOpenAIWebHook")]
        [HttpPost] [Route("api/LineBotOpenAIWebHook")]
        public async Task<IActionResult> POST()
        {
            try {
                this.ChannelAccessToken = Environment.GetEnvironmentVariable("LINE_CHANNEL_TOKEN");
                var lineEvent = this.ReceivedMessage?.events?.FirstOrDefault();
                
                if (lineEvent?.type.ToLower() == "message" && lineEvent.message.type == "text") {
                    string userId = lineEvent.source.userId; 
                    string userText = lineEvent.message.text;
                    
                    MonitorService.RecordRequest(userId, userText);
                    _ = LoadingAnimationManager.StartLoadingAsync(this.ChannelAccessToken, userId);

                    string? reply = await GASSheetService.GetResponseAsync(userText);
                    if (string.IsNullOrEmpty(reply)) {
                        ChatHistoryManager.AddMessage(userId, "user", userText);
                        reply = await GeminiLLM.GetResponseAsync(userId, userText);
                        ChatHistoryManager.AddMessage(userId, "assistant", reply);
                        _ = GASSheetService.LogAsync(userId, userText, reply);
                        reply += "\n\n（以上由Gemini AI 協助回覆，如有任何疑問歡迎電話聯繫學校。）";
                    }
                    this.ReplyMessage(lineEvent.replyToken, reply);
                }
            } catch { }
            return Ok();
        }
    }
} 

GAS語法
/**
 * LINE Bot Waldorf V2.1 - GAS 模組
 * 支援功能：關鍵字比對(search) 與 AI 紀錄(log, 含 UserID)
 */

function doPost(e) {
  try {
    var requestData = JSON.parse(e.postData.contents);
    var userQuery = requestData.query;
    var action = requestData.action; 
    
    var ss = SpreadsheetApp.getActiveSpreadsheet();
    
    // --- 紀錄功能：紀錄 AI 答覆與家長 ID ---
    if (action === "log") {
      var logSheet = ss.getSheetByName("Log");
      if (!logSheet) {
        logSheet = ss.insertSheet("Log");
        logSheet.appendRow(["紀錄時間", "家長 ID", "家長提問", "AI 老師回覆"]);
        logSheet.getRange("A1:D1").setBackground("#eeeeee").setFontWeight("bold");
      }
      logSheet.appendRow([
        new Date(), 
        requestData.userId, 
        userQuery, 
        requestData.answer
      ]);
      return ContentService.createTextOutput(JSON.stringify({ status: "logged" })).setMimeType(ContentService.MimeType.JSON);
    }
    
    // --- 搜尋功能：搜尋標準答案 ---
    var sheet = ss.getSheets()[0]; 
    var data = sheet.getDataRange().getValues();
    var foundAnswer = null;
    
    for (var i = 1; i < data.length; i++) {
      var keyword = data[i][0].toString().toLowerCase();
      var answer = data[i][1];
      if (userQuery.toLowerCase().indexOf(keyword) !== -1) {
        foundAnswer = answer;
        break; 
      }
    }
    
    if (foundAnswer) {
      return ContentService.createTextOutput(JSON.stringify({ status: "success", answer: foundAnswer })).setMimeType(ContentService.MimeType.JSON);
    } else {
      return ContentService.createTextOutput(JSON.stringify({ status: "not_found" })).setMimeType(ContentService.MimeType.JSON);
    }
    
  } catch (err) {
    return ContentService.createTextOutput(JSON.stringify({ status: "error", message: err.toString() })).setMimeType(ContentService.MimeType.JSON);
  }
}
