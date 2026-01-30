using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Lưu conversation_id theo email (in-memory)
builder.Services.AddMemoryCache();

builder.Services.AddHttpClient("coze", client =>
{
    client.Timeout = TimeSpan.FromSeconds(25);
});

builder.Services.AddHttpClient("fchat", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/health", () => Results.Ok(new { ok = true }));

// -------------------- FCHAT INCOMING WEBHOOK --------------------
app.MapPost("/webhook/fchat", async (
    HttpRequest request,
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    IMemoryCache cache) =>
{
    Console.WriteLine("[FCHAT] incoming webhook called");

    // 1) Đọc raw body
    var raw = await new StreamReader(request.Body, Encoding.UTF8).ReadToEndAsync();
    Console.WriteLine("[FCHAT] raw body preview=" + (raw.Length > 800 ? raw[..800] + "..." : raw));

    if (string.IsNullOrWhiteSpace(raw))
        return Results.Ok(new { ok = true });

    // 2) Parse JSON theo payload bạn đang test:
    // { message: { text: "...", user: { email: "..." } } }
    string text = "";
    string email = "";

    try
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
        {
            if (msg.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                text = t.GetString() ?? "";

            if (msg.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
            {
                if (user.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String)
                    email = e.GetString() ?? "";
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("[FCHAT] JSON parse error: " + ex.Message);
        return Results.Ok(new { ok = true });
    }

    // ✅ Vị trí #3 bạn hỏi: log email + text sau parse
    Console.WriteLine($"[FCHAT] email={email} text={text}");

    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(text))
    {
        Console.WriteLine("[FCHAT] Missing email/text -> ignore");
        return Results.Ok(new { ok = true });
    }

    // 3) Lấy conversation_id đã lưu theo email (để Coze nhớ)
    var cacheKey = "coze:cid:" + email.Trim().ToLowerInvariant();
    var existingCid = cache.Get<string>(cacheKey) ?? "";
    Console.WriteLine("[FCHAT] existing conversation_id=" + existingCid);

    // 4) Gọi Coze
    Console.WriteLine("[FCHAT] calling Coze...");
    var (answer, newCid, debug) = await AskCozeAsync(
        httpClientFactory,
        config,
        userId: email,          // dùng email làm user_id cho ổn định
        text: text,
        conversationId: existingCid);

    Console.WriteLine("[FCHAT] Coze answer preview=" + (answer.Length > 120 ? answer[..120] + "..." : answer));
    Console.WriteLine("[FCHAT] new conversation_id=" + newCid);

    // lưu conversation_id mới
    if (!string.IsNullOrWhiteSpace(newCid))
        cache.Set(cacheKey, newCid, TimeSpan.FromDays(7));

    // nếu Coze fail mà answer rỗng -> dùng debug
    if (string.IsNullOrWhiteSpace(answer))
        answer = string.IsNullOrWhiteSpace(debug)
            ? "Mình chưa có câu trả lời phù hợp. Bạn thử lại nhé."
            : $"Coze lỗi/không trả answer. Debug: {debug}";

    // 5) Gửi trả lời ngược về FChat (User Messaging API)
    var baseUrl = config["FChat:BaseUrl"] ?? "https://alerts.soc.fpt.net/webhooks";
    var token = config["FChat:Token"];

    if (string.IsNullOrWhiteSpace(token))
    {
        Console.WriteLine("[FCHAT] Missing FChat:Token");
        return Results.Ok(new { ok = true });
    }

    var sendUrl = $"{baseUrl.TrimEnd('/')}/{token}/fchat";
    Console.WriteLine("[FCHAT] sending back to FChat...");
    Console.WriteLine("[FCHAT] sendUrl=" + sendUrl);

    var sendBody = new { email, text = answer };

    var fchat = httpClientFactory.CreateClient("fchat");
    using var sendResp = await fchat.PostAsync(
        sendUrl,
        new StringContent(JsonSerializer.Serialize(sendBody), Encoding.UTF8, "application/json")
    );

    Console.WriteLine($"[FCHAT] send status={(int)sendResp.StatusCode}");

    if (!sendResp.IsSuccessStatusCode)
    {
        var errBody = await sendResp.Content.ReadAsStringAsync();
        Console.WriteLine("[FCHAT] send error body=" + errBody);
    }

    // 6) Theo tài liệu: Iris/FChat incoming webhook cần {ok:true}
    return Results.Ok(new { ok = true });
});

// -------------------- FPT -> COZE WEBHOOK (GIỮ NGUYÊN, CHỈ TÁI DÙNG AskCozeAsync) --------------------
app.MapPost("/webhook/fpt", async (HttpRequest request, IHttpClientFactory httpClientFactory, IConfiguration config) =>
{
    Console.WriteLine("[WEBHOOK] /webhook/fpt called");

    // Verify shared secret
    var expectedSecret = config["Fpt:WebhookSecret"];
    if (string.IsNullOrWhiteSpace(expectedSecret))
        return Results.Problem("Missing Fpt:WebhookSecret config");

    if (!request.Headers.TryGetValue("X-Webhook-Secret", out var secret) || secret != expectedSecret)
        return Results.Unauthorized();

    // Read body
    var raw = await new StreamReader(request.Body, Encoding.UTF8).ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(raw))
    {
        return Results.Json(new
        {
            messages = new[] { new { type = "text", content = new { text = "Body rỗng." } } }
        });
    }

    using var doc = JsonDocument.Parse(raw);
    var root = doc.RootElement;

    string GetString(params string[] keys)
    {
        foreach (var k in keys)
        {
            if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s!;
            }
        }
        return "";
    }

    var senderId = GetString("sender_id", "senderId", "user_id", "userId");
    if (string.IsNullOrWhiteSpace(senderId)) senderId = "anonymous";

    var text = GetString("sender_input", "text", "message", "query");
    var conversationId = GetString("coze_conversation_id", "conversation_id");

    if (string.IsNullOrWhiteSpace(text))
    {
        return Results.Json(new
        {
            messages = new[] { new { type = "text", content = new { text = "Bạn vui lòng nhập nội dung câu hỏi." } } }
        });
    }

    var (answer, newCid, debug) = await AskCozeAsync(httpClientFactory, config, senderId, text, conversationId);

    if (string.IsNullOrWhiteSpace(answer))
        answer = string.IsNullOrWhiteSpace(debug)
            ? "Mình chưa có câu trả lời phù hợp. Bạn thử hỏi lại theo cách khác nhé."
            : $"Coze không trả answer. Debug: {debug}";

    return Results.Json(new
    {
        set_attributes = new { coze_conversation_id = newCid ?? "" },
        messages = new[] { new { type = "text", content = new { text = answer } } }
    });
})
.WithName("FptWebhook");


// -------------------- SHARED: CALL COZE + PARSE SSE --------------------
static async Task<(string Answer, string NewConversationId, string Debug)> AskCozeAsync(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    string userId,
    string text,
    string conversationId)
{
    var cozeBaseUrl = config["Coze:BaseUrl"] ?? "https://api.coze.com";
    var cozePat = config["Coze:Pat"];
    var cozeBotId = config["Coze:BotId"];

    if (string.IsNullOrWhiteSpace(cozePat) || string.IsNullOrWhiteSpace(cozeBotId))
        return ("", conversationId, "Missing Coze config (Coze:Pat / Coze:BotId)");

    var url = $"{cozeBaseUrl.TrimEnd('/')}/v3/chat";
    if (!string.IsNullOrWhiteSpace(conversationId))
        url += $"?conversation_id={Uri.EscapeDataString(conversationId)}";

    var body = new
    {
        bot_id = cozeBotId,
        user_id = userId,
        stream = true,
        auto_save_history = true,
        additional_messages = new[]
        {
            new { role = "user", content = text, content_type = "text" }
        }
    };

    var http = httpClientFactory.CreateClient("coze");
    using var reqMsg = new HttpRequestMessage(HttpMethod.Post, url);
    reqMsg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
    reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cozePat);
    reqMsg.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    using var resp = await http.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead);

    var ct = resp.Content?.Headers.ContentType?.ToString() ?? "";
    Console.WriteLine($"[COZE-RESP] status={(int)resp.StatusCode} content-type={ct}");

    if (!resp.IsSuccessStatusCode || resp.Content == null)
    {
        var err = resp.Content == null ? "(no content)" : await resp.Content.ReadAsStringAsync();
        return ("", conversationId, $"HTTP {(int)resp.StatusCode}: {err}");
    }

    var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
    if (!mediaType.Contains("text/event-stream"))
    {
        var json = await resp.Content.ReadAsStringAsync();
        Console.WriteLine("[COZE-JSON] " + (json.Length > 800 ? json[..800] + "..." : json));
        return ("", conversationId, "Coze returned JSON (not SSE). See [COZE-JSON].");
    }

    // SSE state
    string currentEvent = "";
    var dataLines = new List<string>();
    bool done = false;

    string newConversationId = conversationId;
    var answerDelta = new StringBuilder();
    string completedAnswer = "";
    string failInfo = "";

    static string ExtractAssistantText(JsonElement r)
    {
        if (r.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            return c.GetString() ?? "";

        if (r.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Object &&
            d.TryGetProperty("content", out var dc) && dc.ValueKind == JsonValueKind.String)
            return dc.GetString() ?? "";

        if (r.TryGetProperty("content", out var co) && co.ValueKind == JsonValueKind.Object &&
            co.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            return t.GetString() ?? "";

        return "";
    }

    void Dispatch()
    {
        if (string.IsNullOrWhiteSpace(currentEvent))
        {
            dataLines.Clear();
            return;
        }

        var dataStr = string.Join("\n", dataLines).Trim();
        Console.WriteLine($"[COZE] event={currentEvent} data={(dataStr.Length > 300 ? dataStr[..300] + "..." : dataStr)}");

        if (currentEvent == "done")
        {
            if (dataStr == "[DONE]" || dataStr == "\"[DONE]\"")
                done = true;

            currentEvent = "";
            dataLines.Clear();
            return;
        }

        if (string.IsNullOrWhiteSpace(dataStr))
        {
            currentEvent = "";
            dataLines.Clear();
            return;
        }

        // Lưu fail/debug nếu có
        if (currentEvent is "conversation.chat.failed" or "conversation.chat.requires_action" or "error")
            failInfo = $"{currentEvent}: {dataStr}";

        try
        {
            using var d = JsonDocument.Parse(dataStr);
            var r = d.RootElement;

            if (currentEvent == "conversation.chat.created")
            {
                if (r.TryGetProperty("conversation_id", out var cid) && cid.ValueKind == JsonValueKind.String)
                    newConversationId = cid.GetString() ?? newConversationId;
            }

            if ((currentEvent == "conversation.message.delta" || currentEvent == "conversation.message.completed") &&
                r.TryGetProperty("role", out var role) && role.GetString() == "assistant" &&
                r.TryGetProperty("type", out var type) && type.GetString() == "answer")
            {
                var s = ExtractAssistantText(r);
                if (!string.IsNullOrWhiteSpace(s))
                {
                    if (currentEvent == "conversation.message.delta")
                        answerDelta.Append(s);
                    else
                        completedAnswer = s;
                }
            }
        }
        catch
        {
            // ignore parse errors
        }

        currentEvent = "";
        dataLines.Clear();
    }

    await using var cozeStream = await resp.Content.ReadAsStreamAsync();
    using var sr = new StreamReader(cozeStream);

    while (!sr.EndOfStream)
    {
        var line = await sr.ReadLineAsync();
        if (line == null) break;

        // raw sse (bạn có thể tắt nếu spam)
        // Console.WriteLine("[SSE] " + line);

        if (line.Length == 0)
        {
            Dispatch();
            if (done) break;
            continue;
        }

        if (line.StartsWith("event:"))
        {
            currentEvent = line["event:".Length..].Trim();
            continue;
        }

        if (line.StartsWith("data:"))
        {
            dataLines.Add(line["data:".Length..].Trim());
            continue;
        }
    }

    Dispatch();

    var answer = !string.IsNullOrWhiteSpace(completedAnswer)
        ? completedAnswer.Trim()
        : answerDelta.ToString().Trim();

    return (answer, newConversationId, failInfo);
}
app.MapGet("/", () => Results.Ok(new { ok = true, service = "CozeFptWebhook" }));
app.MapGet("/webhook/fchat", () => Results.Ok(new { ok = true, hint = "Use POST /webhook/fchat" }));

app.Run();

