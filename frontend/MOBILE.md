# Мобильное приложение (Android)

APK — обёртка Capacitor вокруг веб-клиента. Логика, авторизация и SignalR остаются в вебе;
нативное здесь только окно, сплэш и стандартный Android-бэкстек.

## Первая версия грузит прод-сайт

В `capacitor.config.ts` стоит `server.url = 'https://conexyai.ru'`. Это сделано осознанно:
cookie сессии, `Authorization` и `/hubs/conexy` остаются same-origin, и сервер менять не нужно.
Именно поэтому в CORS сервера сейчас нет origin приложения — и не требуется.

Переход на локальные ассеты (старт без сети, никакой загрузки HTML при каждом запуске)
возможен позже и требует двух шагов:

1. в `Program.cs` добавить origin приложения (`https://localhost` для Capacitor) в политику
   `AllowFrontend` **с credentials**;
2. убрать `server.url` из `capacitor.config.ts` и пересобрать APK — тогда UI поедет из
   `dist`, а запросы пойдут на прод по абсолютному адресу. Для этого в `api/client.ts`
   нужно будет подставить base URL вместо относительного `/api`.

## Вписывание в экран

- `android.adjustMarginsForEdgeToEdge: 'force'` — Android 15 рисует приложение под статус-баром и
  полосой навигации; без этого низ с полем ввода и верх с шапкой уезжали за экран.

## Вход в аккаунт

Google запрещает OAuth внутри WebView («This browser or app may not be secure»), поэтому вход
через GitHub (и Google внутри него) в приложении шёл в системном браузере, а cookie оставалась
там. Теперь сессия возвращается приложению одноразовым кодом:

1. кнопка в приложении открывает `/api/auth/mobile?redirect=ru.conexy.app://auth` в системном
   браузере (`@capacitor/browser`);
2. если в этом браузере уже есть сессия сайта, OAuth не запускается вообще — код выдаётся сразу;
   иначе идёт обычный GitHub OAuth;
3. после входа сервер отдаёт страницу, которая открывает `ru.conexy.app://auth?code=…`;
4. приложение ловит ссылку (`appUrlOpen`), меняет код на JWT (`POST /api/auth/handoff`) и
   сохраняет сессию.

Код живёт 2 минуты и удаляется при первом использовании. `redirect` принимается только точным
значением `ru.conexy.app://auth` — иначе это был бы открытый редирект с токеном.

## Голосовой ввод

В Android WebView нет Web Speech API: `window.SpeechRecognition` там отсутствует, поэтому кнопка
микрофона отвечала «не поддерживается» при любых разрешениях. Распознаёт нативный
`SpeechInputPlugin` (`android.speech.SpeechRecognizer`):

- `start({ lang })` запускает сессию, текст приходит событием `result` (`{ text, isFinal }`),
  конец сессии — `end`, ошибки — `error` с кодом в терминах Web Speech API;
- `stop()` даёт договорить последнюю фразу, `cancel()` рвёт сразу;
- прокрутка сессий (склейка по перекрытию, перенос текста в общий транскрипт, перезапуск после
  паузы) живёт в `InputBar` и общая для браузерного и нативного путей;
- `RECORD_AUDIO` запрашивается через Capacitor (`alias = "microphone"`).

На сайте всё работает через браузерный `SpeechRecognition`: `nativeSpeech()` в `utils/mobile.ts`
возвращает null, когда приложения нет.

## Обновления

При старте приложение читает список релизов на GitHub (`releases`) и берёт первый по времени,
в котором есть `conexy-android.apk`, — десктоп и телефон выпускаются независимо, поэтому релиз
только с `conexy-win-x64.exe` обновлением для приложения не считается. Версия сравнивается
с `versionName` сборки, и если она новее, показывается ненавязчивая полоса «Доступно обновление».

Кнопка обновления не открывает браузер: `@capacitor/browser` (Custom Tab) умел только показывать
страницы и висел в бесконечной загрузке, потому что Chrome не устанавливает APK. Вместо этого
локальный плагин `ApkUpdaterPlugin`:

1. при необходимости открывает системный экран «Установка неизвестных приложений»
   (`REQUEST_INSTALL_PACKAGES`), а после возврата продолжает сам;
2. скачивает APK через `DownloadManager` — прогресс виден в шторке, загрузка переживает
   сворачивание приложения;
3. по завершении отдаёт файл системному установщику через `FileProvider`.

Номер скачанного обновления хранится в `SharedPreferences`: если систему убьёт приложение посреди
загрузки, установка догонится при следующем запуске.

Сервер (`GET /api/device/client`) по той же логике отдаёт для ПК последний релиз с exe, а для
телефона — последний с APK.

Версия сборки задаётся в `android/app/build.gradle` (`versionName`/`versionCode`) и должна
совпадать с тегом релиза, куда вы кладёте APK. Десктопная версия живёт в
`ConexyAI.Desktop/ConexyAI.Desktop.csproj` и поднимается отдельно.

## Безопасные зоны

Android 15 рисует WebView во весь экран (edge-to-edge), и `env(safe-area-inset-*)` внутри WebView
остаётся нулевым: шапка уезжала под статус-бар, композер — под полосу жестов.

Поэтому `MainActivity` отдаёт отступы системных панелей (`systemBars() | displayCutout()`) и высоту
клавиатуры (`ime()`) в CSS переменными `--safe-top`, `--safe-bottom`, `--safe-left`,
`--safe-right`, `--kb-native` и `--window-height` — инлайном на `<html>`, после каждой загрузки
страницы и при каждом изменении insets. В браузере те же переменные определены в `:root` через
`env()`, так что одна и та же вёрстка работает и на сайте.

`viewport-fit=cover` в `index.html` намеренно НЕ включён: с ним WebView перестаёт сжиматься под
клавиатурой, и композер прячется за ней. Высота `.app` — минимум из видимой области
(`--app-height` из `visualViewport`) и `--window-height - --kb-native`, поэтому работает и когда
клавиатуру сжимает сам WebView, и когда она приходит только инсетами.

## Иконки

`make-icons.ps1` растеризует `frontend/public/conexyai.svg` через headless Chrome и
раскладывает `ic_launcher` / `ic_launcher_round` / `ic_launcher_foreground` по всем плотностям,
а также ставит фирменный фон адаптивной иконки (`#0A0A0A`). Запуск:

```
powershell -ExecutionPolicy Bypass -File android/make-icons.ps1
```

Скрипт держим без кириллицы: PowerShell читает `.ps1` без BOM как ANSI и ломается на ней.

## Требования к машине сборки

- **JDK 21** (Capacitor 7 не собирается на 17: `error: invalid source release: 21`).
- **Android SDK** с платформой `android-36` и `build-tools;36.1.0`.
- `android/local.properties` (в git не попадает):

  ```
  sdk.dir=C:/Users/<user>/AppData/Local/Android/Sdk
  ```

  Путь только с прямыми слэшами — обратные в `.properties` ломают разбор.

Если JDK 21 не прописан в `JAVA_HOME`, сборка запускается так:

```
gradlew.bat assembleDebug -Dorg.gradle.java.home=C:/Users/<user>/jdk21/jdk-21.0.12.1+1
```

## Сборка

```
npm run build          # web-ассеты в dist (нужны для cap sync)
npm run mobile:sync    # cap sync android
npm run mobile:apk     # gradlew assembleDebug
```

APK: `android/app/build/outputs/apk/debug/app-debug.apk`.

## Установка на телефон

Debug-APK подписан debug-ключом — ставится на устройство вручную (разрешить установку из
неизвестных источников). Для Google Play нужен release-keystore и `bundleRelease`/`assembleRelease`,
плюс прохождение модерации (политика про AI-контент и «не медицинская рекомендация»).

Альтернатива для Play без Capacitor — TWA: тот же сайт в системном Chrome, но тогда обязателен
`https://conexyai.ru/.well-known/assetlinks.json` с отпечатком подписи APK.
