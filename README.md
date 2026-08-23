# KuCoin Futures Reporter

MVP-сервис на .NET 8: забирает историю закрытых фьючерсных позиций KuCoin и отправляет отчёты в Telegram.

## Запуск локально

1. Подними PostgreSQL:

```bash
docker compose up -d
```

2. Установи EF tool, если его нет:

```bash
dotnet tool install --global dotnet-ef
```

3. Создай миграцию и базу:

```bash
dotnet ef migrations add InitialCreate
dotnet ef database update
```

4. Заполни секреты через переменные окружения или `appsettings.json`.

Рекомендуется через environment variables:

```bash
export KuCoin__ApiKey="..."
export KuCoin__ApiSecret="..."
export KuCoin__ApiPassphrase="..."
export Telegram__BotToken="..."
export Telegram__ChatId="..."
export Telegram__AllowedUserIds="123456789"
```

Свой Telegram user id можно узнать у [@userinfobot](https://t.me/userinfobot). Без `AllowedUserIds` команды бота отключены: посторонние не увидят меню и не получат ответы.

5. Запусти:

```bash
dotnet run
```

## Сигналы

Бот сам раз в час (через ~3 минуты после закрытия 1h свечи) сканирует ликвидные USDT-перпы KuCoin и присылает **вам в личку** только 1–2 лучших сетапа двух типов: пробой Donchian и откат к EMA20 в тренде 4h. Если ничего сильного нет — молчит.

Отключить: `Signals__Enabled=false`.

Если открытая сделка доходит до **+400% ROI**, бот в личку напомнит переставить стоп в зону **+200–300% ROI**, чтобы остаться в плюсе. В канал это не уходит.

## Важно

Создай API key на KuCoin только с read-only/general permissions. Не включай trade/withdraw, сервис нужен только для отчётов.
