var builder = WebApplication.CreateBuilder(args);

// 註冊控制器服務
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

// 設定 HTTP 請求管道
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// --- 重要：啟動背景自動喚醒任務 ---
isRock.Template.SelfPingService.Start();

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// 程式最後只能有一個 app.Run()
app.Run();
