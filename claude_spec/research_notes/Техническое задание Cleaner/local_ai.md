# Локальный AI-слой Cleaner (on-device): модели, runtime, guardrails, оценка, управление решениями

Дата среза: 2026-10-23. Факты о версиях, датах и лицензиях быстро устаревают — проверить перед фиксацией в ТЗ.
Связь с прошлыми заметками: `research_notes\Архитектура и сборка Cleaner\model_product.md` уже содержит выбор llama.cpp как базового backend, кандидатов Qwen3.5-2B / Qwen3-4B-Instruct-2507 / Phi-4-mini, SHA256 GGUF, ограничения GBNF и OWASP LLM01. Здесь — только дополнения и новые решения, без повтора.

## 1. Runtime на Windows из .NET (октябрь 2026)

### Takeaway
Базовый путь остаётся llama.cpp через LLamaSharp (CPU всегда, CUDA/Vulkan опционально) — работает на Windows 10 и 11 одинаково. Foundry Local (GA с 2026-04-09) и Windows ML — второй adapter для Windows 11 24H2+ с GPU/NPU; Phi Silica — только бонус на Copilot+ PC, не опора. Все runtime прячутся за `Microsoft.Extensions.AI.IChatClient`.

### Cited Findings
- **Foundry Local**: GA объявлена 2026-04-09; Windows/Linux/macOS; SDK на C#, Python, JS, Rust; C# пакет `Microsoft.AI.Foundry.Local`; работает in-process, без отдельного сервиса или CLI, пользователю не нужно ставить драйверы; опциональный OpenAI-compatible HTTP endpoint; на Windows получает EP-плагины через Windows ML; GPU/NPU с fallback на CPU; каталог включает Qwen, Phi, Mistral, DeepSeek, GPT OSS, Whisper; модели скачиваются при первом запуске и кэшируются; размер установки не указан («без заметного влияния на размер»), путь кэша не указан, лицензия в посте не указана. — [Foundry blog GA](https://devblogs.microsoft.com/foundry/foundry-local-ga/)
- Пакеты NuGet: `Microsoft.AI.Foundry.Local.Runtime` 2.0.1 (обновлён 2026-08-31, native для win-x64, win-arm64, linux, macOS arm64); `Microsoft.AI.Foundry.Local.Core.WinML` 1.2.x («native AOT compiled library… with WinML support»). — [NuGet Runtime](https://www.nuget.org/packages/Microsoft.AI.Foundry.Local.Runtime/), [NuGet Core.WinML 1.2.3](https://www.nuget.org/packages/Microsoft.AI.Foundry.Local.Core.WinML/1.2.3)
- **Windows ML**: GA 2025-09-23, Windows App SDK 1.8.1, требует Windows 11 24H2+; Windows сам детектирует железо и скачивает подходящие execution providers, приложение их не бандлит. — [Windows Developer Blog](https://blogs.windows.com/windowsdeveloper/2025/09/23/windows-ml-is-generally-available-empowering-developers-to-scale-local-ai-across-windows-devices), [Thurrott](https://www.thurrott.com/?p=327165)
- ONNX Runtime: «WinML is the recommended Windows development path»; DirectML «in sustained engineering»; на Windows 10 1809+ и Windows 11 до 24H2 модели и EP выбирает и обслуживает само приложение. — [onnxruntime.ai: with Windows](https://onnxruntime.ai/docs/get-started/with-windows.html)
- **ORT GenAI constrained decoding**: использует LLGuidance; режимы Lark grammar (рекомендован), JSON Schema, regex; для tool calling нужно пометить tool-call токены как special в `tokenizer.json` (пример Phi-4-mini `<|tool_call|>`). Источник — зеркало документации, не официальный release notes. — [Mintlify mirror: constrained decoding](https://www.mintlify.com/microsoft/onnxruntime-genai/guides/constrained-decoding)
- **Phi Silica / Windows AI APIs (LanguageModel)**: требуют Copilot+ PC и Windows 11 25H2 (build 10.0.26200.7309+); возможны проблемы с Limited Access Feature; Microsoft советует experimental-сборки, которым LAF-токен не нужен. — [Learn: troubleshooting](https://learn.microsoft.com/en-us/windows/ai/apis/troubleshooting)
- С Windows App SDK 2.2 Experimental 9 (июнь 2026) Language Model API экспериментально работает на GPU NVIDIA RTX 30+ с 6+ GB VRAM; раньше — только NPU 40+ TOPS. Данные из СМИ, статус experimental. — [Windows Latest](https://windowslatest.com/2026/06/11/microsoft-is-killing-the-copilot-pc-advantage-brings-windows-11s-local-ai-to-rtx-30-pcs-with-6gb-vram), [PC Watch](https://pc.watch.impress.co.jp/docs/news/2118861.html)
- **LLamaSharp**: v0.30.0 (17 окт., год на странице не указан — по порядку релизов 2026) добавил `LLamaSharp.Backend.Cuda13`, CUDA 13 пробуется раньше 12, исправлен `AsChatClient` (интеграция с M.E.AI); v0.29.0 (24 авг.) — llama.cpp `815a2a59…` (b10221); v0.27.0 (26 апр.) — поддержка Qwen3.5/Gemma4 и Windows arm64 CPU. — [LLamaSharp releases](https://github.com/SciSharp/LLamaSharp/releases)
- **Microsoft.Extensions.AI**: `GetResponseAsync<T>` даёт структурированный вывод; флаг JSON schema «might cause an error if the model does not support it» — для локальных моделей может понадобиться `useJsonSchemaResponseFormat: false`. — [Learn: structured output](https://learn.microsoft.com/dotnet/ai/quickstarts/structured-output), [API reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.chatclientstructuredoutputextensions.getresponseasync?view=net-6.0-pp)

### Inferences
- Матрица runtime по тирам:
  - Windows 10 / Windows 11 < 24H2 / любой ПК: LLamaSharp + CPU backend (AVX2), Vulkan или CUDA — опциональные пакеты, грузятся только при наличии GPU.
  - Windows 11 24H2+ с NPU или dGPU: Foundry Local (WinML) как opt-in adapter после бенчмарка на целевых классах железа.
  - Copilot+ PC: Phi Silica для коротких объяснений, без скачивания весов. Нельзя делать его единственным путём из-за LAF и узкого охвата.
- Требование «нет фоновой нагрузки» проще всего выполнить в отдельном дочернем процессе inference. Процесс стартует при открытии «Аналитики/Ассистента» и завершается при закрытии или по idle-таймеру (например 120 с). Завершение процесса гарантированно освобождает RAM/VRAM. Это надёжнее, чем Dispose внутри GUI-процесса, и изолирует native-краши llama.cpp. Foundry Local in-process это требование выполняет хуже.
- Constrained decoding есть в обоих путях: GBNF/JSON-schema в llama.cpp, LLGuidance в ORT GenAI. Поэтому JSON-контракт можно держать единым, а через `IChatClient` абстрагировать только транспорт.

### Gaps
- Не найдено официальных release notes ORT GenAI за 2026 год и точного размера установки Foundry Local / WinML EP.
- Не проверено, где Foundry Local хранит кэш моделей и можно ли его перенаправить в `%LOCALAPPDATA%\Cleaner\models`.
- Нет данных о версии M.E.AI, которую тянет LLamaSharp 0.30.

## 2. Кандидатные LLM и тиры железа

### Takeaway
Лицензии: Qwen3/3.5 (Apache-2.0), Phi-4-mini (MIT), Gemma 4 (Apache-2.0 с апреля 2026) и Ministral 3 (Apache-2.0) пригодны для коммерческой редистрибуции. Llama 3.x требует notice-файл, AUP и порог 700M MAU, поэтому хуже подходит. По заявленному русскому языку лидируют Qwen, Phi-4-mini и Gemma. У Ministral 3 русский язык в карточке не указан.

### Cited Findings
- Qwen3-4B-Instruct-2507: 4.02B параметров, Apache-2.0. — [HF](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507)
- Phi-4-mini-instruct: 3.84B, MIT, в списке языков есть `ru` и `uk`. — [HF](https://huggingface.co/microsoft/Phi-4-mini-instruct)
- Gemma 4: релиз 2026-04-02 под Apache-2.0, впервые без собственной лицензии Google; размеры E2B (~2.3B effective), E4B (~4.5B effective), 26B MoE, 31B; 128K контекст. — [The Decoder](https://the-decoder.com/googles-gemma-4-is-now-available-with-apache-2-0-licensing-for-the-first-time/), [implicator.ai](https://www.implicator.ai/google-releases-gemma-4-under-apache-2-0-dropping-its-custom-ai-license/)
- `google/gemma-4-E4B-it` на HF: всего 7.996B параметров (any-to-any, с мультимодальными частями), Apache-2.0. Значит, «E4B» не равно 4B по RAM. — [HF](https://huggingface.co/google/gemma-4-E4B-it)
- Ministral-3-3B-Instruct-2512: 3.85B (3.4B LM + 0.4B vision), Apache-2.0, языки en/fr/es/de/it/pt/nl/zh/ja/ko/ar — **без ru**. — [HF](https://huggingface.co/mistralai/Ministral-3-3B-Instruct-2512), [LM Studio](https://www.lmstudio.ai/models/mistralai/ministral-3-3b)
- Llama 3.2 Community License: при >700M MAU нужна отдельная лицензия Meta; при распространении обязателен файл «Notice»; действует Acceptable Use Policy. — [Meta license](https://developer.meta.com/ai/llama3_2/license/)
- Скорость CPU (ориентир, не замер): Qwen3.5-4B Q4_K_M ≈24 tok/s на Ryzen 7 7700X + DDR5-6000 и ≈15 tok/s на Core i5-12400 + DDR4, разброс ±20 %, Ollama/llama.cpp. — [bestllmfor.com](https://bestllmfor.com/guide/llm-local-cpu-sans-gpu/)
- AMD: до 50.7 tok/s для Llama 3.2 1B 4-bit на Ryzen AI 9 HX 375 (LM Studio, частичная GPU offload). — [AMD blog](https://www.amd.com/en/blogs/2024/accelerating-llama-cpp-performance-in-consumer-llm.html)
- Gemma 4 E2B на Raspberry Pi 5: prefill 133 tok/s, decode 7.6 tok/s (вторичный источник). — [techwyse](https://www.techwyse.com/news/ai-search/google-deepmind-gemma-4-open-models-apache-2-0-release)
- Размеры GGUF Qwen3.5-2B (Q4_K_M 1.27 GB) уже зафиксированы в прошлых заметках. — [HF tree](https://huggingface.co/lmstudio-community/Qwen3.5-2B-GGUF/tree/main)

### Inferences
Предлагаемая линейка. Цифры RAM — оценки по правилу «размер GGUF + KV-кэш на 4–8K контекста + ~0.5 GB runtime»; это не замеры.
| Тир | Железо | Модель (основная / альтернатива) | Квант | Скачивание | RAM/VRAM ≈ | Ожидаемо |
|---|---|---|---|---|---|---|
| Без LLM | <8 GB RAM, старый CPU без AVX2 | Только шаблоны + классификаторы | — | 0 | — | Объяснения по шаблонам из fact ID |
| Слабый | 8 GB RAM, CPU/iGPU | Qwen3.5-2B / Gemma 4 E2B | Q4_K_M | ~1.3 GB | ~2–2.5 GB | ~20–40 tok/s CPU (экстраполяция) |
| Средний | 16 GB RAM, iGPU или dGPU 4–6 GB | Qwen3-4B-Instruct-2507 / Phi-4-mini | Q4_K_M | ~2.5 GB | ~3.5–4.5 GB | ~15–25 tok/s CPU, больше на GPU |
| Сильный | 32 GB RAM или dGPU 8+ GB | Qwen3-8B-класс или Gemma 4 E4B | Q4_K_M / Q5_K_M | ~5 GB | ~6–8 GB | GPU offload, длинные планы |
| Copilot+ | NPU 40+ TOPS | Phi Silica (системная) | — | 0 | системная | короткие объяснения |
- По умолчанию предлагать «Средний», если RAM ≥16 GB, иначе «Слабый». Тир можно переопределить вручную. Автоподбор делать по RAM, VRAM, AVX2 и короткому `llama-bench` при первом запуске (≈10 с, только по согласию пользователя).
- Ministral 3 исключить из основного кандидатного списка: русский в карточке не заявлен. Llama 3.x держать в резерве из-за юридических требований.
- Для tool calling у Phi-4-mini есть отдельные токены. Но в прошлых заметках отмечены галлюцинации имён функций, а контракт Cleaner — «JSON по grammar, не исполняемые вызовы». Поэтому специальные tool-токены не обязательны.

### Gaps
- Нет независимых замеров tok/s конкретно для Qwen3-4B / Phi-4-mini на типичных ноутбучных CPU и iGPU (Intel Xe/Arc, Radeon 780M) в 2026 году; нужен свой benchmark-spike.
- Нет русскоязычного бенчмарка, сравнивающего эти модели на задаче объяснения дисковых данных. Нужен собственный eval (раздел 7).
- Размеры GGUF для Gemma 4 E2B/E4B и Qwen3-4B не проверены.

## 3. Малые классификаторы путей и папок

### Takeaway
Слой 1: детерминированные правила и сигнатуры известных приложений (покрывают большинство объёма). Слой 2: gradient boosting на табличных признаках, ONNX, размер в килобайтах, микросекунды на объект. Слой 3 (опционально): компактный многоязычный embedder (multilingual-e5-small, 118M, MIT) для непокрытых имён. Его вектор идёт признаком в GBM, а не используется как решатель.

### Cited Findings
- multilingual-e5-small: 117.7M параметров, BERT-архитектура, лицензия MIT, ~100 языков, включая `ru` и `uk`; в репозитории есть ONNX- и OpenVINO-артефакты. — [HF](https://huggingface.co/intfloat/multilingual-e5-small)
- ONNX Runtime с CPU EP доступен на Windows 10 1809+, то есть подходит для классификаторов на всех целевых ОС без WinML. — [onnxruntime.ai](https://onnxruntime.ai/docs/get-started/with-windows.html)
- Калибровка: у современных нейросетей уверенность плохо калибрована; temperature scaling — простой эффективный пост-процесс. — [Guo et al. 2017, arXiv:1706.04599](https://arxiv.org/abs/1706.04599)
- Conformal prediction даёт множества классов с гарантированным покрытием без предположений о модели; при неоднозначности уместно отказаться от решения («не знаю»). — [Angelopoulos & Bates, arXiv:2107.07511](https://arxiv.org/abs/2107.07511)

### Inferences
- Признаки для GBM (LightGBM или XGBoost, экспорт в ONNX):
  - путь: токены сегментов, глубина, известные корни (`%LOCALAPPDATA%`, `\Cache\`, `node_modules`, `steamapps`);
  - расширения: распределение, доля бинарных, доля `.tmp`/`.log`/`.dmp`;
  - время: доля файлов с atime/mtime старше N дней, скорость роста по USN;
  - владелец: процесс или приложение из Uninstall-реестра, есть ли установленный владелец (сигнал leftover);
  - использование: Prefetch, BAM, SRUM;
  - VCS: маркеры `.git`, `package.json`, `.sln`;
  - игры: маркеры сохранений (`SaveGames`, `My Games`).
- Модели и выходы:
  - `category` — multi-class;
  - `consequence` ∈ {regenerated, redownloaded, lost} — отдельная модель;
  - аномалия роста — robust z-score или MAD по истории USN, без ML;
  - прогноз заполнения диска — линейная или Holt-регрессия по дневным снимкам с доверительным интервалом.
- Обучающие данные:
  - размеченный корпус путей с собственных и волонтёрских машин; собирать только пути без содержимого, по согласию, с обезличиванием имени пользователя;
  - синтетика из шаблонов известных приложений (Chrome, Steam, VS, npm, Gradle, Docker);
  - LLM-аугментация названий: генерировать офлайн при разработке, не на устройстве;
  - hard negatives: пользовательские папки с именами «cache», «temp», «backup».
- Стоимость на устройстве: GBM на ~10⁵ папок займёт доли секунды. e5-small на CPU грузить только для папок, которые не покрыли правила (`<5 %`), батчами, с кэшем векторов по hash(имени) в SQLite.
- Порог «safe to delete» задавать не на сыром score: калибровать по hold-out, затем порог на уровне precision ≥ 0.999 для категории. Если conformal-множество содержит больше одного класса, это «не уверен» и решение уходит человеку.

### Gaps
- Публичного размеченного корпуса Windows-путей с классами cache/save/project/leftover не найдено. Его придётся создавать.

## 4. Структурированный вывод, tool calling, grounding

### Takeaway
LLM никогда не видит и не выдаёт свободные пути. Она получает таблицу фактов с непрозрачными ID (`F123`), а grammar разрешает в ответе только `fact_id` из перечня. Приложение заново валидирует JSON: ID существует, действие разрешено, суммы сходятся.

### Cited Findings
- llama.cpp превращает подмножество JSON Schema в GBNF; полноту и семантику должен проверять прикладной валидатор (подробно в `model_product.md`). — [grammars/README](https://github.com/ggml-org/llama.cpp/blob/master/grammars/README.md)
- ORT GenAI: LLGuidance с Lark, JSON Schema и regex. — [Mintlify mirror](https://www.mintlify.com/microsoft/onnxruntime-genai/guides/constrained-decoding)
- M.E.AI `GetResponseAsync<T>` с native schema может упасть на моделях без поддержки. — [Learn](https://learn.microsoft.com/dotnet/ai/quickstarts/structured-output)

### Inferences
- Схема плана (пример):
  ```json
  {"goal_bytes": 32212254720,
   "items":[{"fact_id":"F123","action":"clean_cache","reason_code":"regenerated","explain_ru":"..."}],
   "excluded":[{"fact_id":"F77","why":"user_rule:no_games"}],
   "questions_to_user":["..."]}
  ```
  `fact_id` в grammar — enum из ID текущего контекста; `action` — enum из разрешённых для категории факта; `explain_ru` ограничен по длине.
- Числа (байты, даты) LLM не считает. Executor подставляет их из фактов, и сумма плана пересчитывается кодом. Если цель не достигнута, это честно показывается.
- Конвейер: retrieval нужных фактов детерминированно → prompt с таблицей → constrained decoding → JSON-валидатор → policy-checker → UI. При ошибке валидации делается одна повторная попытка с сообщением об ошибке, затем fallback на шаблонный план без LLM.
- Для ответов на вопросы свободный текст допустим. Но каждое утверждение о файлах должно ссылаться на `[F123]`, а UI превращает ссылки в реальные пути. Ссылки на несуществующие ID вырезаются, и ответ помечается.

### Gaps
- Не найдено публичных метрик надёжности function calling для 2–4B моделей на русском; нужен свой замер.

## 5. Prompt injection через имена файлов и содержимое

### Takeaway
Имена папок, файлов и метаданные — недоверенные данные. Главная защита архитектурная: у модели нет инструментов с побочными эффектами, авторизация происходит вне LLM. Санитизация вторична.

### Cited Findings
- OWASP LLM06 Excessive Agency: причины — «excessive functionality / permissions / autonomy»; меры: «Minimize extensions», «Avoid open-ended extensions», «Execute extensions in user's context», «Require user approval», «Complete mediation» (авторизация в downstream, не в LLM), логирование и rate-limiting как ограничение ущерба. — [OWASP LLM06:2025](https://genai.owasp.org/llmrisk/llm062025-excessive-agency/)
- OWASP LLM01: фильтры и системный prompt не дают абсолютной защиты (см. прошлые заметки). — [OWASP LLM01](https://genai.owasp.org/llmrisk/llm01-prompt-injection/)

### Inferences
- Меры для Cleaner:
  1. Содержимое файлов в LLM не передаётся вообще; в prompt идут только метаданные.
  2. Имена передаются в отдельном блоке данных, усечёнными до 64 символов, с экранированием, в кавычках JSON-строк. Управляющие символы, bidi-override (U+202E) и zero-width убираются.
  3. Имена с инструкционными паттернами («ignore previous», «удали», «system:») помечаются флагом `suspicious_name`, и это влияет на score в сторону человека.
  4. Grammar не позволяет выйти за enum действий и ID.
  5. Policy-checker не учитывает текст LLM.
  6. Red-team набор с вредоносными именами входит в CI (раздел 7).
- Удаление объектов с подозрительными именами никогда не попадает в Autopilot.

### Gaps
- Нет опубликованных случаев injection именно через имена файлов в cleaner-продуктах. Оценка риска теоретическая.

## 6. Управление решениями: калибровка, уровни автономии, аудит, undo, персонализация

### Takeaway
Аналоги подтверждают модель «коридора»:
- Storage Sense по умолчанию выключен, Downloads не трогает без явной настройки, облачные файлы переводит в online-only без удаления;
- Defender различает Full и Semi автоматизацию; все действия видны в Action Center и обратимы, а неподтверждённые истекают через 7 дней.

### Cited Findings
- Storage Sense выключен по умолчанию; «Downloads folder and cloud content will not be managed unless you specify»; OneDrive-файлы становятся online-only после 30 дней без открытия (22H2+), «will not permanently delete any files»; «Always keep on this device» исключает файл. — [Microsoft Support](https://support.microsoft.com/pt-BR/Windows/Experience/Storage-FileManagement/manage-drive-space-with-storage-sense)
- Defender AIR: Full — автоисправление объектов, признанных вредоносными, история в Action Center, «a remediation action can be undone»; Semi — часть или все действия ждут одобрения; pending истекает через 7 дней и тогда считается отклонённым; у клиентов с Full на 40 % больше удалённых high-confidence образцов. — [Learn: automation levels](https://learn.microsoft.com/en-us/defender-endpoint/automation-levels)
- OWASP: human approval для действий с высоким impact и mediation вне LLM. — [OWASP LLM06](https://genai.owasp.org/llmrisk/llm062025-excessive-agency/)

### Inferences
- Уровни автономии по аналогии с Defender:
  - L0 «Только анализ»;
  - L1 «Советник» — по умолчанию;
  - L2 «Полуавтомат»: auto только для `regenerated` + известная сигнатура, остальное в очередь одобрения с TTL 7 дней;
  - L3 «Автопилот в коридоре».
- Условия коридора проверяет executor, а не модель: категория ∈ allowlist; consequence = `regenerated`; калиброванная уверенность ≥ порога категории; размер ≤ лимита на запуск; нет флагов `suspicious_name`, `user_rule_protected`, `recently_used` (Prefetch/BAM за N дней); действие обратимо (Корзина или карантин) или регенерируемо.
- Undo: «мягкое удаление» через перемещение в карантин `%LOCALAPPDATA%\Cleaner\quarantine` (тот же том, rename, мгновенно) с TTL 7–14 дней. Для кэшей — прямое удаление, потому что они `regenerated`. Персональные файлы — только через Корзину и только вручную.
- Журнал (SQLite, append-only):
  - `decision_id`, `ts`, `mode`, `fact_id`, `path_hash`/`path`, `bytes`;
  - `category`, `p_calibrated`, `model_versions` (классификатор, LLM, rules), `policy_rule_ids` (сработавшие), `llm_explanation`;
  - `user_action` (accept/reject/undo), `executor_result`, `undo_token`.
  - Хеш-цепочка строк (hash предыдущей записи) защищает от незаметной правки.
- Персонализация без online learning:
  - на старте пересчитывать Beta-Binomial по (категория, приложение-владелец): `α = 1 + accepts`, `β = 1 + rejects`;
  - если posterior mean reject > 0.5 при n ≥ 3, понижать уровень автономии для этого ключа;
  - явные правила пользователя («не трогать игры») — жёсткие и выше всех весов.
  - В UI показывать: «Мы не предлагаем X, потому что вы 4 раза отклонили похожие (сбросить)».

### Gaps
- Документация CleanMyMac Assistant и macOS Optimize Storage не изучалась в этой сессии; уровни их автономии не подтверждены источниками.

## 7. Оценка и red-teaming

### Takeaway
Главная метрика — precision класса «безопасно удалить» на уровне ≥ 0.999 при отказе («не уверен») вместо ошибки. Recall вторичен. LLM оценивается отдельно: grounding, валидность JSON, соблюдение ограничений пользователя, качество русского.

### Cited Findings
- Калибровка (ECE, temperature scaling) — [Guo et al. 2017](https://arxiv.org/abs/1706.04599); множества с гарантией покрытия — [Angelopoulos & Bates](https://arxiv.org/abs/2107.07511).
- OWASP рекомендует SAST/DAST и мониторинг для LLM-расширений. — [OWASP LLM06](https://genai.owasp.org/llmrisk/llm062025-excessive-agency/)

### Inferences
- Наборы:
  - (a) golden-набор путей ≥ 5–10 тыс., размеченный вручную, со стратификацией по приложениям и языкам имён;
  - (b) «опасный» набор: сохранения игр, проекты без `.git`, документы в папках с именами `cache`/`temp`; на нём должно быть 0 ошибок auto;
  - (c) снимки реальных машин-добровольцев (метаданные).
- Метрики классификатора: precision и recall по классам, ECE, coverage при пороге precision ≥ 0.999, доля «не уверен».
- Метрики LLM:
  - JSON validity — 100 % после grammar;
  - hallucinated fact_id — 0 после валидатора; мерить частоту до него;
  - нарушение ограничений («не трогать игры») — 0;
  - доля планов, достигших цели;
  - RU fluency — оценка людьми по шкале 1–5 на 100 ответах;
  - латентность первого токена и tok/s по тирам.
- Red-team в CI: имена с инъекциями, bidi, очень длинные пути, symlink/junction на системные папки, OneDrive-placeholders, вопросы «удали System32».

### Gaps
- Нет публичного бенчмарка для cleaner-классификации; все пороги нужно выводить из собственных данных.

## 8. Распространение моделей

### Takeaway
Модели — опциональная загрузка после установки. Источник — собственный подписанный manifest с SHA-256 по каждой ревизии. Хранение в `%LOCALAPPDATA%\Cleaner\models\<id>\<sha256>`. Работа офлайн после загрузки, без телеметрии.

### Cited Findings
- Foundry Local скачивает модели при первом запуске, кэширует их и докачивает после обрыва. — [Foundry GA](https://devblogs.microsoft.com/foundry/foundry-local-ga/)
- Windows ML сам скачивает EP под железо; приложение не контролирует их состав. — [Windows Dev Blog](https://blogs.windows.com/windowsdeveloper/2025/09/23/windows-ml-is-generally-available-empowering-developers-to-scale-local-ai-across-windows-devices)
- Hugging Face публикует SHA256 файлов; ссылка `main` меняется, поэтому фиксировать нужно ревизию (см. прошлые заметки). — [HF файл](https://huggingface.co/lmstudio-community/Qwen3.5-2B-GGUF/blob/main/Qwen3.5-2B-Q4_K_M.gguf)
- Llama требует распространять файл Notice; Apache-2.0 и MIT требуют сохранять тексты лицензий. — [Meta license](https://developer.meta.com/ai/llama3_2/license/)

### Inferences
- Manifest (JSON) подписывается ключом издателя (Ed25519 или Authenticode каталога) и содержит: `model_id`, `revision`, `url[]` (зеркала), `size`, `sha256`, `license`, `min_tier`, `prompt_template_version`, `eval_scores`.
- Порядок загрузки:
  1. Проверить подпись manifest.
  2. Скачать файл с поддержкой докачки.
  3. Сверить SHA-256 до первой загрузки модели в runtime.
  4. Сделать атомарный rename.
- Обновления только по запросу пользователя. Старая версия хранится до успешной проверки новой.
- Классификаторы (единицы MB) и правила поставлять внутри инсталлятора. LLM вынести в отдельную загрузку на 1.3–5 GB.
- Для пути Foundry Local/WinML согласиться, что EP и кэш контролирует Windows. Это ещё один аргумент держать его вторичным adapter.
- Офлайн-режим: без LLM приложение полностью работоспособно (шаблонные объяснения и классификаторы). LLM — улучшение, не зависимость.

### Gaps
- Не проверено, поддерживает ли Foundry Local загрузку моделей из собственного источника или каталога с проверкой хеша.
