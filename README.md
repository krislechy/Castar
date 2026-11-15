# Castar

Castar учитывает доходы и покупки. Windows-клиент показывает данные за выбранный период и отправляет изменения по HTTP API. Castar Server хранит базу, обрабатывает сообщения Telegram и загружает позиции фискальных чеков по QR-коду.

## Установка и обновление

1. Скачайте архив Castar Server для Windows x64 или Linux x64 из [последнего выпуска](https://github.com/krislechy/Castar/releases/latest) и запустите сервер по [инструкции](Castar.Server/DEPLOYMENT.md).
2. Установите [Microsoft .NET Desktop Runtime 11 для Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/11.0#runtime-desktop-11.0.0). Для первой установки клиента запустите `Castar-win-x64.exe`. Затем открывайте приложение через ярлык или `Castar.exe`.
3. В клиенте откройте **Настройки → Подключение к Castar Server**. Укажите адрес и ключ API сервера, затем нажмите **Проверить и сохранить**. Для сервера на этой же машине используется `http://127.0.0.1:5080`, для удалённого — HTTPS.

Начиная с `v0.3.0`, обновляйте клиент через **Настройки → Обновления Castar → Проверить обновления**. Castar покажет объём и процент загрузки, сверит SHA-256 ZIP по данным GitHub, заменит файлы клиента и перезапустится. Сервер обновляется отдельно заменой его архива с сохранением каталога данных и конфигурации службы.

Для однократного перехода с `v0.2.0` сначала установите Desktop Runtime и полностью закройте Castar. Скачайте и распакуйте `Castar-Update-win-x64.zip` в отдельную папку. Откройте в этой папке PowerShell и выполните команду. Если Castar установлен в другом каталоге, подставьте путь к нему первым аргументом.

```powershell
.\Castar.Updater.exe "$env:LOCALAPPDATA\Programs\Castar" (Get-Location).Path
```

Клиент хранит общие настройки в `%LOCALAPPDATA%\Castar\Castar.Client.xml`. Если этого файла ещё нет, клиент переносит параметры автозапуска и сворачивания в трей из настроек `v0.1.0` и сразу сохраняет их в новом формате. Параметры базы и Telegram задаются в конфигурации сервера.

## Установка на iPhone

Нужен iPhone ARM64 с iOS 15 или новее, rootless-jailbreak, доступ по SSH и `sudo`. Установка проверена на iPhone SE первого поколения с iOS 15.7. На iPhone нужны `bash`, `ldid`, `uicache`, `launchctl` и `tar`.

1. Скачайте [полный архив Castar для iPhone](https://github.com/krislechy/Castar/releases/download/v0.4.0/Castar-Server-ios-arm64.tar) и [контрольную сумму SHA-256](https://github.com/krislechy/Castar/releases/download/v0.4.0/Castar-Server-ios-arm64.tar.sha256). Архив содержит сервер, наблюдатель, приложение Castar и инструкцию `DEPLOYMENT.md`.
2. На Windows подставьте Wi-Fi IP iPhone вместо `IPHONE_IP` и выполните команды в PowerShell. Если ваш SSH использует другой порт, укажите его вместо 22.

   ```powershell
   scp -P 22 .\Castar-Server-ios-arm64.tar mobile@IPHONE_IP:.
   ssh -p 22 mobile@IPHONE_IP
   ```

3. В сеансе SSH на iPhone распакуйте и запустите установщик.

   ```sh
   work=$(mktemp -d "$HOME/castar-install.XXXXXXXX")
   tar -xf "$HOME/Castar-Server-ios-arm64.tar" -C "$work"
   sudo /var/jb/usr/bin/bash "$work/install.sh"
   ```

   Если `ldid` находится вне PATH, передайте его путь установщику, например `sudo /var/jb/usr/bin/env LDID=/absolute/path/to/ldid /var/jb/usr/bin/bash "$work/install.sh"`.

4. Откройте Castar на iPhone. В приложении доступны статус, журнал, остановка и перезапуск службы. Для подключения Windows-клиента получите ключ API в том же сеансе SSH.

   ```sh
   sudo sed -n 's/^Castar__ApiKey=//p' /var/jb/etc/castar/server.env
   ```

5. На Windows запустите SSH-туннель в отдельном окне PowerShell. В настройках Castar введите `http://127.0.0.1:15082` и ключ API.

   ```powershell
   ssh -p 22 -N -o ExitOnForwardFailure=yes -L 127.0.0.1:15082:127.0.0.1:5080 mobile@IPHONE_IP
   ```

Отдельный [ZIP приложения Castar для iPhone](https://github.com/krislechy/Castar/releases/download/v0.4.0/Castar-Control-ios-arm64.zip) содержит `CastarControl.app` и файл для подписи `ldid`. Для первой установки используйте полный архив, который регистрирует приложение и запускает службы.

## Хранилище

Новый сервер создаёт SQLite в `data/castar.sqlite`. Путь меняется через `Database__SqlitePath`. Для существующего SQLite из выпуска 0.1.0 остановите прежний Castar и укажите серверу путь к копии старого файла. При прежней настройке по умолчанию он находится в `%LOCALAPPDATA%\Castar\castar.sqlite`.

Castar Server использует SQLite. Если данные уже перенесены в SQLite, достаточно указать путь к этому файлу в `Database__SqlitePath`. История старых резервных копий описана в [документации БД](DataBase/README.md).

## Telegram и чеки

Укажите `Castar__Telegram__Token` и `Castar__Telegram__Enabled=true` в окружении сервера и перезапустите его. Отправьте `/me` в личном чате, чтобы узнать Telegram ID, затем добавьте пользователя с этим ID в клиенте. Получатели уведомлений определяются списком пользователей приложения.

Выберите провайдера в `Castar__Receipts__Provider`. Значение по умолчанию — `serbia`. Бот принимает фото QR-кода или ссылку с чека, а сервер запрашивает JSON у [публичного сервиса TaxCore](https://tap.suf.purs.gov.rs/help/view/145107305/Verify-invoice-via-verification-URL-with-JSON-response/en-US). Для `nalog` задайте ИНН и пароль в окружении сервера. Новые провайдеры реализуют `IReceiptProvider`.

Если QR мелкий или бледный, отправьте исходное изображение как файл до 20 МБ. Так Telegram передаст его в исходном качестве. Можно также вставить ссылку из QR-кода.

## Сборка

Установите [.NET SDK 11](https://dotnet.microsoft.com/en-us/download/dotnet/11.0) и выполните на Windows `dotnet build Castar.sln -c Release`. Публикация тега вида `vX.Y.Z`, совпадающего с `Version` в `Castar/Castar.csproj`, запускает сборку установщика и выпуск на GitHub.
