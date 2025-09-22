using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.Payments;
using Telegram.Bot.Types.ReplyMarkups;

var builder = WebApplication.CreateBuilder(args);

// Env vars
var botToken = Environment.GetEnvironmentVariable("Telegram__BotToken");
var payToken = Environment.GetEnvironmentVariable("Telegram__PaymentProviderToken");
var currency = Environment.GetEnvironmentVariable("Telegram__Currency") ?? "EUR";

if (string.IsNullOrWhiteSpace(botToken)) throw new InvalidOperationException("Telegram__BotToken is not set.");

var bot = new TelegramBotClient(botToken);
var app = builder.Build();

// Healthcheck
app.MapGet("/health", () => Results.Ok("ok"));

// Webhook endpoint: Telegram будет слать сюда Update
// Healthcheck
app.MapGet("/health", () => Results.Ok("ok"));

// Надёжный вебхук: всегда 200, логируем ошибки
app.MapPost($"/bot/{botToken}", async (HttpRequest req) =>
{
    try
    {
        using var sr = new StreamReader(req.Body);
        var json = await sr.ReadToEndAsync();

        var update = JsonConvert.DeserializeObject<Update>(json);
        if (update != null)
            await HandleUpdateAsync(bot, update);
        else
            Console.WriteLine("Webhook: null update");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Webhook error: {ex}");
    }

    return Results.Ok(); // всегда 200, чтобы Telegram не ретраил

    // ВАЖНО: всегда 200, иначе Telegram будет ретраить и копить pending_update_count
    return Results.Ok();
});


app.Run();

// ================= Handlers =================
static async Task HandleUpdateAsync(ITelegramBotClient bot, Update update)
{
    switch (update.Type)
    {
        case UpdateType.Message:
            if (update.Message!.Type == MessageType.Text)
                await OnText(bot, update.Message!);
            break;

        case UpdateType.PreCheckoutQuery:
            // Перед оплатой — подтверждаем, что всё ок
            await bot.AnswerPreCheckoutQueryAsync(update.PreCheckoutQuery!.Id);
            break;
    }
}

static async Task OnText(ITelegramBotClient bot, Message msg)
{
    var chatId = msg.Chat.Id;
    var text = msg.Text ?? string.Empty;

    if (text == "/start")
    {
        await bot.SendTextMessageAsync(chatId,
            "Привет! Я кафе-бот ☕️\nВыберите действие:",
            replyMarkup: MainMenu());
        return;
    }

    if (text == "📋 Меню")
    {
        var lines = GetMenu().Select(m => $"• {m.Name} — {FormatPrice(m.PriceCents)}");
        await bot.SendTextMessageAsync(chatId, "📋 Меню:\n" + string.Join("\n", lines), replyMarkup: MenuKeyboard());
        return;
    }

    if (text == "💳 Оплатить")
    {
        await SendSampleInvoice(bot, chatId);
        return;
    }

    await bot.SendTextMessageAsync(chatId, "Команда не распознана. Нажмите кнопки ниже.", replyMarkup: MainMenu());
}


static string FormatPrice(int cents)
{
    return $"{cents / 100m:F2} {Environment.GetEnvironmentVariable("Telegram__Currency") ?? "EUR"}";
}

static ReplyKeyboardMarkup MainMenu()
{
    return new ReplyKeyboardMarkup(new[]
    {
        new KeyboardButton[] { "📋 Меню" },
        new KeyboardButton[] { "💳 Оплатить" }
    }) { ResizeKeyboard = true };
}

static MenuItem[] GetMenu()
{
    return new[]
    {
        new MenuItem("Эспрессо", 180),
        new MenuItem("Капучино", 250),
        new MenuItem("Чизкейк", 420)
    };
}

static InlineKeyboardMarkup MenuKeyboard()
{
    var rows = GetMenu().Select(m => new[]
    {
        InlineKeyboardButton.WithCallbackData($"{m.Name} — {FormatPrice(m.PriceCents)}", $"noop:{m.Name}")
    });
    return new InlineKeyboardMarkup(rows);
}

static async Task SendSampleInvoice(ITelegramBotClient bot, long chatId)
{
    var payToken = Environment.GetEnvironmentVariable("Telegram__PaymentProviderToken");
    var currency = Environment.GetEnvironmentVariable("Telegram__Currency") ?? "EUR";

    if (string.IsNullOrWhiteSpace(payToken))
    {
        await bot.SendTextMessageAsync(chatId, "Токен платёжного провайдера не задан. Установите переменную окружения Telegram__PaymentProviderToken.");
        return;
    }

    var prices = new List<LabeledPrice>
    {
        new("Эспрессо ×1", 180),
        new("Капучино ×1", 250)
    };

    await bot.SendInvoiceAsync(
        chatId,
        "Оплата заказа",
        "Демо-инвойс (Stripe test)",
        $"order-{chatId}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
        payToken,
        currency,
        prices,
        needName: true,
        needPhoneNumber: false,
        needEmail: false,
        needShippingAddress: false,
        isFlexible: false
    );
}

// ============ Demo menu + invoice ============
internal record MenuItem(string Name, int PriceCents);