# Развёртывание Castar Server

Скачайте серверный архив для вашей платформы из [GitHub Releases](https://github.com/krislechy/Castar/releases). Для Windows x64 используйте `Castar-Server-win-x64.zip`, для Linux x64 с glibc — `Castar-Server-linux-x64.tar.gz`. Среда выполнения входит в оба архива. На Linux установите системные библиотеки для вашей версии дистрибутива. [Требования .NET для Linux](https://learn.microsoft.com/dotnet/core/install/linux)

## Первый запуск

Распакуйте архив в отдельный каталог. Используйте случайный ключ из 32 или более символов без пробелов. Если клиент установлен на том же компьютере, укажите в его настройках адрес `http://127.0.0.1:5080` и тот же ключ.

Linux

```bash
mkdir -p ./castar-server
tar -xzf Castar-Server-linux-x64.tar.gz -C ./castar-server
cd ./castar-server
read -rsp 'Ключ API Castar: ' Castar__ApiKey && printf '\n'
export Castar__ApiKey
export Database__SqlitePath="$HOME/.local/share/castar/castar.sqlite"
./Castar.Server
```

Windows, PowerShell 7

```powershell
Expand-Archive -LiteralPath '.\Castar-Server-win-x64.zip' -DestinationPath '.\castar-server'
Set-Location -LiteralPath '.\castar-server'
$env:Castar__ApiKey = Read-Host 'Ключ API Castar' -MaskInput
$env:Database__SqlitePath = Join-Path $env:LOCALAPPDATA 'CastarServer\castar.sqlite'
.\Castar.Server.exe
```

Настройки окружения

| Переменная | Назначение |
| --- | --- |
| `Castar__ApiKey` | Общий ключ клиента и сервера. Клиент присылает его в заголовке `Authorization: Bearer`. |
| `ASPNETCORE_URLS` | Адрес Kestrel. По умолчанию `http://127.0.0.1:5080`. |
| `Database__SqlitePath` | Путь к постоянному файлу SQLite. По умолчанию `data/castar.sqlite` внутри серверного каталога. |
| `Castar__Telegram__Enabled` | Задайте `true`, чтобы запустить Telegram-бота на сервере. |
| `Castar__Telegram__Token` | Токен бота из BotFather. |
| `Castar__Receipts__Enabled` | Импорт чеков включён по умолчанию. |
| `Castar__Receipts__Provider` | `serbia` для публичной проверки QR TaxCore или `nalog` для ФНС России. |
| `Castar__Receipts__Nalog__Inn`, `Castar__Receipts__Nalog__Password` | Учётные данные для провайдера `nalog`. |

Задайте для службы часовой пояс, в котором ведётся бюджет. Сервер использует локальные даты для сообщений и поиска по периодам. Для Linux укажите, например, `TZ=Europe/Belgrade` в окружении службы, если бюджет ведётся по сербскому времени.

После запуска откройте `/health` на указанном адресе. HTTP 200 подтверждает, что отвечает серверный процесс. Защищённый `GET /api/status` проверяет также соединение с базой, а `GET /api/users` возвращает список пользователей. Для настройки Telegram заведите пользователей через клиент. Оставьте один активный процесс бота на токен.

В Windows-клиенте откройте **Настройки → Сервер**, чтобы включить или выключить Telegram и обработку чеков и выбрать провайдера. Изменения применяются сразу. После первого изменения эти три значения берутся из файла `<имя файла SQLite>.settings.json` рядом с базой. До этого действуют значения из конфигурации запуска. Включите этот файл в резервную копию. Запускайте один процесс Castar Server на файл базы. Токены, учётные данные провайдеров, путь SQLite и адрес прослушивания задаются в конфигурации запуска.

Чтобы снова использовать значения из конфигурации запуска, остановите сервер и переместите файл `.settings.json` в резервную копию. Затем запустите сервер.

Сервер хранит состояние ключей в `<имя файла SQLite>.keys.json` рядом с базой. Текущий `Castar__ApiKey` добавляется при первом запуске. При замене значения прежний ключ из конфигурации перестаёт действовать. На iPhone действующие ключи можно просматривать, выпускать и отзывать в приложении Castar. При резервном копировании сохраняйте файл реестра вместе с базой.

## Удалённое подключение

На сервере оставьте Kestrel на `127.0.0.1:5080`. Подключите обратный прокси с TLS и укажите его HTTPS-адрес в клиенте. Пример Caddyfile для домена, который ведёт на этот сервер. [Настройка HTTPS и прокси в Caddy](https://caddyserver.com/docs/quick-starts/reverse-proxy#https-from-client-to-proxy)

```caddyfile
castar.example.com {
    reverse_proxy 127.0.0.1:5080
}
```

Для постоянной работы запускайте сервер от отдельной учётной записи. Например, systemd может читать настройки из защищённого файла `/etc/castar/server.env` с правами `0600` и хранить SQLite в каталоге `/var/lib/castar`. В файле окружения задайте `Database__SqlitePath=/var/lib/castar/castar.sqlite` и нужные параметры из таблицы. Дайте учётной записи `castar` права на запись в каталог данных и на чтение серверных файлов.

```ini
[Unit]
Description=Castar Server
After=network-online.target
Wants=network-online.target

[Service]
User=castar
WorkingDirectory=/opt/castar/current
EnvironmentFile=/etc/castar/server.env
ExecStart=/opt/castar/current/Castar.Server
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

## Перенос данных и обновление

- **SQLite из версии 0.1.0.** Остановите прежний Castar перед копированием. Перенесите базу SQLite в каталог данных сервера и задайте путь через `Database__SqlitePath`. Для установки по умолчанию старый файл находится в `%LOCALAPPDATA%\Castar\castar.sqlite`.
- **База из прежней конфигурации SQL Server.** До обновления получите файл SQLite. Историческая команда переноса доступна в исходниках тега `v0.3.0`. Затем укажите путь к SQLite в `Database__SqlitePath` и удалите прежние `Database__Engine` и `ConnectionStrings__Castar`. Подробнее в [документации БД](../DataBase/README.md).
- **Обновление.** Остановите сервер, сделайте резервную копию SQLite и настроек и распакуйте новый архив в отдельный каталог. Сохраните путь к SQLite и ключи в окружении службы. При запуске сервер применяет миграции EF Core. Для базы Castar, созданной до появления миграций, он проверяет схему перед добавлением записи в историю. После запуска проверьте `/health` и `/api/status`, затем обновите Windows-клиент.
