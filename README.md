# Telegram bot on Google Cloud Run (.NET 8)

## Requirements
- Google Cloud account + billing enabled (Always Free tier)
- gcloud CLI (`gcloud init`)
- Bot token от @BotFather
- (Опционально) Telegram Payments provider token (Stripe test/live)

## Build & Run (local)
```bash
dotnet run --project src/telegram-cloudrun-bot.csproj
# локально можно протестировать только через /health
```

## Deploy to Cloud Run
```bash
PROJECT_ID=your-project-id
REGION=europe-central2
IMAGE=$REGION-docker.pkg.dev/$PROJECT_ID/telebot-repo/telebot:latest

# 1) Enable APIs
gcloud services enable run.googleapis.com artifactregistry.googleapis.com

# 2) Create Docker repo (one-time)
gcloud artifacts repositories create telebot-repo \
  --repository-format=docker \
  --location=$REGION \
  --description="Telegram bot repo"

# 3) Build & push image with Cloud Build
gcloud builds submit --tag $IMAGE

# 4) Deploy
gcloud run deploy telebot \
  --image $IMAGE \
  --platform managed \
  --region $REGION \
  --allow-unauthenticated \
  --set-env-vars "Telegram__BotToken=YOUR_BOT_TOKEN,Telegram__PaymentProviderToken=YOUR_PROVIDER_TOKEN,Telegram__Currency=EUR"

# Output: Service URL, e.g. https://telebot-xxxxxx-run.app/
```

## Set Telegram webhook
```bash
SERVICE_URL="https://telebot-xxxxxx-run.app" # подставь из вывода деплоя
TOKEN="YOUR_BOT_TOKEN"

curl -X POST "https://api.telegram.org/bot$TOKEN/setWebhook" \
  -d "url=$SERVICE_URL/bot/$TOKEN"

# Проверка
curl "https://api.telegram.org/bot$TOKEN/getWebhookInfo"
```

## Notes
- endpoint уже захардкожен с токеном внутри пути: `/bot/{botToken}`.
- для прод — добавь логирование/БД/ретраи.
- Long polling — на VM/VPS (Cloud Run масштабируется до 0, webhook-only).
