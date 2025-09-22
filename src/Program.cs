using System.Collections.Concurrent;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.Payments;
using Telegram.Bot.Types.ReplyMarkups;

var builder = WebApplication.CreateBuilder(args);

// Env vars
var botToken = Environment.GetEnvironmentVariable("Telegram__BotToken");
var currency = Environment.GetEnvironmentVariable("Telegram__Currency") ?? "EUR";
if (string.IsNullOrWhiteSpace(botToken)) throw new InvalidOperationException("Telegram__BotToken is not set.");

var bot = new TelegramBotClient(botToken);
var app = builder.Build();

// ====== Demo menu (единый источник) ======
var MENU = new List<MenuItem>
{
    new("espresso", "Эспрессо", 180),
    new("cappuccino", "Капучино", 250),
    new("cheesecake", "Чизкейк", 420),
};
// корзина: chatId -> (code -> qty)
var Carts = new ConcurrentDictionary<long, ConcurrentDictionary<string, int>>();

// ========= Healthcheck =========
app.MapGet("/health", () => Results.Ok("ok"));

// ========= Надёжный вебхук: всегда 200 =========
app.MapPost($"/bot/{botToken}", async (HttpRequest req) =>
{
    try
    {
        using var sr = new StreamReader(req.Body);
        var json = await sr.ReadToEndAsync();
        var update = JsonConvert.DeserializeObject<Update>(json);

        if (update != null)
            await HandleUpdateAsync(bot, update, MENU, Carts, currency);
        else
            Console.WriteLine("Webhook: null update");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Webhook error: {ex}");
        // не роняем ответ наружу
    }
    return Results.Ok();
});

app.Run();

// ============================================
//                Handlers
// ============================================
static async Task HandleUpdateAsync(
    ITelegramBotClient bot,
    Update update,
    List<MenuItem> MENU,
    ConcurrentDictionary<long, ConcurrentDictionary<string,int>> Carts,
    string currency)
{
    switch (update.Type)
    {
        case UpdateType.Message when update.Message!.Type == MessageType.Text:
            await OnText(bot, update.Message!, MENU, Carts, currency);
            break;

        case UpdateType.CallbackQuery:
            await OnCallback(bot, update.CallbackQuery!, MENU, Carts, currency);
            break;

        case UpdateType.PreCheckoutQuery:
            // Перед оплатой — подтвердить, чтобы Telegram продолжил платёж
            await bot.AnswerPreCheckoutQueryAsync(update.PreCheckoutQuery!.Id);
            break;

        case UpdateType.Message when update.Message!.SuccessfulPayment is not null:
            // Успешный платёж — очищаем корзину
            var chatId = update.Message!.Chat.Id;
            Carts.TryRemove(chatId, out _);
            await bot.SendTextMessageAsync(chatId, "✅ Оплата прошла успешно! Спасибо. Заказ передан в работу.");
            break;
    }
}

static async Task OnText(
    ITelegramBotClient bot,
    Message msg,
    List<MenuItem> MENU,
    ConcurrentDictionary<long, ConcurrentDictionary<string,int>> Carts,
    string currency)
{
    var chatId = msg.Chat.Id;
    var text = (msg.Text ?? string.Empty).Trim();

    // поддержим и кнопки, и slash-команды
    switch (text.ToLowerInvariant())
    {
        case "/start":
            await bot.SendTextMessageAsync(chatId,
                "Привет! Я кафе-бот ☕️\nВыберите действие:",
                replyMarkup: MainMenu());
            return;

        case "📋 меню":
        case "/menu":
            await SendMenu(bot, chatId, MENU, currency);
            return;

        case "🧺 корзина":
        case "/cart":
            await SendCart(bot, chatId, MENU, Carts, currency);
            return;

        case "🧹 очистить":
        case "/clear":
            Carts.TryRemove(chatId, out _);
            await bot.SendTextMessageAsync(chatId, "🧹 Корзина очищена.", replyMarkup: MainMenu());
            return;

        case "💳 оплатить":
        case "/pay":
            await SendInvoiceFromCart(bot, chatId, MENU, Carts, currency);
            return;

        default:
            await bot.SendTextMessageAsync(chatId,
                "Не понял 🤔 Используйте кнопки ниже.",
                replyMarkup: MainMenu());
            return;
    }
}

static async Task OnCallback(
    ITelegramBotClient bot,
    CallbackQuery cq,
    List<MenuItem> MENU,
    ConcurrentDictionary<long, ConcurrentDictionary<string,int>> Carts,
    string currency)
{
    var chatId = cq.Message?.Chat.Id ?? cq.From.Id;
    var data = cq.Data ?? "";

    if (data.StartsWith("add:", StringComparison.Ordinal))
    {
        var code = data[4..];
        var item = MENU.FirstOrDefault(m => m.Code == code);
        if (item is not null)
        {
            var cart = Carts.GetOrAdd(chatId, _ => new ConcurrentDictionary<string, int>());
            cart.AddOrUpdate(code, 1, (_, q) => q + 1);

            await bot.AnswerCallbackQueryAsync(cq.Id, $"Добавлено: {item.Name}");
            // Обновим корзину «в один клик»
            await SendCart(bot, chatId, MENU, Carts, currency, editMessageId: cq.Message?.MessageId);
            return;
        }
    }
    else if (data.StartsWith("del:", StringComparison.Ordinal))
    {
        var code = data[4..];
        if (Carts.TryGetValue(chatId, out var cart) && cart.TryGetValue(code, out var qty))
        {
            if (qty <= 1) cart.TryRemove(code, out _);
            else cart[code] = qty - 1;

            var item = MENU.FirstOrDefault(m => m.Code == code);
            await bot.AnswerCallbackQueryAsync(cq.Id, item is null ? "Удалено" : $"Удалено: {item.Name}");
            await SendCart(bot, chatId, MENU, Carts, currency, editMessageId: cq.Message?.MessageId);
            return;
        }
    }
    else if (data == "pay")
    {
        await SendInvoiceFromCart(bot, chatId, MENU, Carts, currency);
        await bot.AnswerCallbackQueryAsync(cq.Id);
        return;
    }
    else if (data == "menu")
    {
        await SendMenu(bot, chatId, MENU, currency, editMessageId: cq.Message?.MessageId);
        await bot.AnswerCallbackQueryAsync(cq.Id);
        return;
    }

    // по умолчанию
    await bot.AnswerCallbackQueryAsync(cq.Id);
}

// ============================================
//               UI helpers
// ============================================
static ReplyKeyboardMarkup MainMenu() =>
    new(new[]
    {
        new KeyboardButton[] { "📋 Меню" },
        new KeyboardButton[] { "🧺 Корзина", "💳 Оплатить" },
        new KeyboardButton[] { "🧹 Очистить" }
    })
    { ResizeKeyboard = true };

static InlineKeyboardMarkup MenuInline(List<MenuItem> MENU, string currency)
{
    var rows = MENU.Select(m => new[]
    {
        InlineKeyboardButton.WithCallbackData($"➕ {m.Name} — {FormatPrice(m.PriceCents, currency)}", $"add:{m.Code}")
    });
    return new InlineKeyboardMarkup(rows.Append(new[]
    {
        InlineKeyboardButton.WithCallbackData("🧺 Корзина", "cart"),
        InlineKeyboardButton.WithCallbackData("💳 Оплатить", "pay")
    }));
}

static InlineKeyboardMarkup CartInline(Dictionary<MenuItem,int> items, string currency)
{
    var rows = items.Select(kv => new[]
    {
        InlineKeyboardButton.WithCallbackData($"➖ {kv.Key.Name} × {kv.Value}", $"del:{kv.Key.Code}"),
        InlineKeyboardButton.WithCallbackData($"➕", $"add:{kv.Key.Code}")
    });

    var footer = new[]
    {
        InlineKeyboardButton.WithCallbackData("📋 Меню", "menu"),
        InlineKeyboardButton.WithCallbackData("💳 Оплатить", "pay")
    };

    return new InlineKeyboardMarkup(rows.Append(footer));
}

// ============================================
//             Business helpers
// ============================================
static async Task SendMenu(
    ITelegramBotClient bot, long chatId, List<MenuItem> MENU, string currency, int? editMessageId = null)
{
    var lines = MENU.Select(m => $"• {m.Name} — {FormatPrice(m.PriceCents, currency)}");
    var text = "📋 Меню:\n" + string.Join("\n", lines);

    if (editMessageId is int mid)
        await bot.EditMessageTextAsync(chatId, mid, text, replyMarkup: MenuInline(MENU, currency));
    else
        await bot.SendTextMessageAsync(chatId, text, replyMarkup: MenuInline(MENU, currency));
}

static async Task SendCart(
    ITelegramBotClient bot,
    long chatId,
    List<MenuItem> MENU,
    ConcurrentDictionary<long, ConcurrentDictionary<string,int>> Carts,
    string currency,
    int? editMessageId = null)
{
    var dict = BuildCartDict(chatId, MENU, Carts);
    if (dict.Count == 0)
    {
        var textEmpty = "🧺 Корзина пуста. Откройте 📋 Меню и добавьте позиции.";
        if (editMessageId is int mid)
            await bot.EditMessageTextAsync(chatId, mid, textEmpty, replyMarkup: MenuInline(MENU, currency));
        else
            await bot.SendTextMessageAsync(chatId, textEmpty, replyMarkup: MenuInline(MENU, currency));
        return;
    }

    var total = dict.Sum(kv => kv.Key.PriceCents * kv.Value);
    var lines = dict.Select(kv => $"• {kv.Key.Name} × {kv.Value} = {FormatPrice(kv.Key.PriceCents * kv.Value, currency)}");
    var text = "🧺 Корзина:\n" + string.Join("\n", lines) + $"\n\nИтого: *{FormatPrice(total, currency)}*";

    if (editMessageId is int mid2)
        await bot.EditMessageTextAsync(chatId, mid2, text, parseMode: ParseMode.Markdown, replyMarkup: CartInline(dict, currency));
    else
        await bot.SendTextMessageAsync(chatId, text, parseMode: ParseMode.Markdown, replyMarkup: CartInline(dict, currency));
}

static async Task SendInvoiceFromCart(
    ITelegramBotClient bot,
    long chatId,
    List<MenuItem> MENU,
    ConcurrentDictionary<long, ConcurrentDictionary<string,int>> Carts,
    string currency)
{
    var payToken = Environment.GetEnvironmentVariable("Telegram__PaymentProviderToken");
    if (string.IsNullOrWhiteSpace(payToken))
    {
        await bot.SendTextMessageAsync(chatId, "⚠️ Токен платёжного провайдера не задан. Установите Telegram__PaymentProviderToken.");
        return;
    }

    var dict = BuildCartDict(chatId, MENU, Carts);
    if (dict.Count == 0)
    {
        await bot.SendTextMessageAsync(chatId, "🧺 Корзина пуста. Добавьте позиции в 📋 Меню.");
        return;
    }

    var prices = dict.Select(kv =>
        new LabeledPrice($"{kv.Key.Name} × {kv.Value}", kv.Key.PriceCents * kv.Value)
    ).ToList();

    var totalCents = prices.Sum(p => p.Amount);
    var title = "Оплата заказа";
    var descr = $"Ваш заказ на сумму {FormatPrice(totalCents, currency)}";

    await bot.SendInvoiceAsync(
        chatId: chatId,
        title: title,
        description: descr,
        payload: $"order-{chatId}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
        providerToken: payToken!,
        currency: currency,
        prices: prices,
        needName: true,
        isFlexible: false
    );
}

static Dictionary<MenuItem,int> BuildCartDict(
    long chatId,
    List<MenuItem> MENU,
    ConcurrentDictionary<long, ConcurrentDictionary<string,int>> Carts)
{
    var result = new Dictionary<MenuItem, int>();
    if (!Carts.TryGetValue(chatId, out var cart)) return result;

    foreach (var kv in cart)
    {
        var item = MENU.FirstOrDefault(m => m.Code == kv.Key);
        if (item is not null && kv.Value > 0)
            result[item] = kv.Value;
    }
    return result;
}

static string FormatPrice(int cents, string currency) =>
    $"{cents / 100m:F2} {currency}";

// ======== Models ========
internal record MenuItem(string Code, string Name, int PriceCents);
