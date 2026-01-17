using System;

using System.Collections.Generic;

using System.IO;

using System.IO.Compression;

using System.Text;

using System.Threading;

using System.Threading.Tasks;

using System.Linq;

using System.Reflection;

using System.Net.Http;

using System.Globalization;

using System.Text.RegularExpressions;

using System.Runtime.Serialization;

using System.Runtime.Serialization.Json;

using WTelegram;

using TL;



namespace TgDialogsExport472

{

    class Program

    {

        // === Telegram auth ===

        private const int API_ID = 30203725;

        private const string API_HASH = "31a5296192dad9e44475d8dafa840c0f";

        private const string PHONE_NUMBER = "+79608656677";

        private const int PAGE_LIMIT = 100;

        private static int _neuroCalls = 0;

        // === Throttle ===

        private const int REQUEST_DELAY_MS = 1000;



        // === NeuroAPI ===

        private static readonly string[] NEURO_BASES = new[]

        {

            "https://neuroapi.host/v1",

            "https://api.neuroapi.dev/v1"

        };

        private const string NEUROAPI_KEY = "sk-hUrySVOzeVKvLC6hV8OMPwamHCamiOdG1e6u7eHthmX0MmgJ";

        private const string DEFAULT_MODEL = "gpt-5-nano";



        private static readonly HttpClient _http;



        // === Storage ===

        private static readonly string EXPORT_DIR = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Exports");

        private static readonly string LOG_DIR = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

        private static readonly string PERSONAS_DIR = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Personas");

        private static readonly string PERSONAS_FILE = Path.Combine(PERSONAS_DIR, "personas.json");

        private static readonly string PERSONA_ASSIGNMENTS_FILE = Path.Combine(PERSONAS_DIR, "assignments.json");



        // === Runtime ===

private static Client _client;

private static User _self;

private static long _selfId;

private static int _rpcCount = 0;



// Worker policy

private const bool SEND_INITIAL_REPLY = true;

private static readonly TimeSpan WORKER_POLL_DELAY = TimeSpan.FromSeconds(4);

private const int REPLY_THRESHOLD = 1; // минимальное число входящих перед ответом

private static readonly TimeSpan IDLE_KICKIN = TimeSpan.FromMinutes(8);

private const double DEFAULT_TYPING_SPEED_WPM = 220;

private const double MIN_TYPING_DELAY_SECONDS = 1.2;

private const double MAX_TYPING_DELAY_SECONDS = 6.0;



// Personas

private static List<Persona> _personas = new List<Persona>();

private static Dictionary<string, PersonaBindingRecord> _personaBindings = new Dictionary<string, PersonaBindingRecord>(StringComparer.OrdinalIgnoreCase);

private static readonly Dictionary<string, PersonaRuntimeConfig> _runtimeDialogConfigs = new Dictionary<string, PersonaRuntimeConfig>();

private static readonly object _personaLock = new object();



// Randomization

private static readonly ThreadLocal<Random> _random = new ThreadLocal<Random>(() => new Random(unchecked(Environment.TickCount * 31 + Thread.CurrentThread.ManagedThreadId)));

        // Color logging

        private static readonly Dictionary<long, ConsoleColor> _userColors = new Dictionary<long, ConsoleColor>();

        private static readonly ConsoleColor[] _palette = new[]

        {

            ConsoleColor.Yellow, ConsoleColor.Cyan, ConsoleColor.Magenta, ConsoleColor.Blue,

            ConsoleColor.Red, ConsoleColor.DarkYellow, ConsoleColor.DarkCyan, ConsoleColor.DarkMagenta,

            ConsoleColor.DarkBlue, ConsoleColor.DarkRed, ConsoleColor.White, ConsoleColor.Gray, ConsoleColor.DarkGray

        };

        private static readonly Dictionary<string, string> _mimeToExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

        {

            { "image/jpeg", ".jpg" },

            { "image/jpg", ".jpg" },

            { "image/png", ".png" },

            { "image/webp", ".webp" },

            { "image/gif", ".gif" },

            { "image/heic", ".heic" },

            { "video/mp4", ".mp4" },

            { "video/quicktime", ".mov" },

            { "video/x-matroska", ".mkv" },

            { "audio/mpeg", ".mp3" },

            { "audio/aac", ".aac" },

            { "audio/ogg", ".ogg" },

            { "audio/opus", ".opus" },

            { "audio/wav", ".wav" },

            { "application/pdf", ".pdf" },

            { "application/zip", ".zip" },

            { "application/x-zip-compressed", ".zip" },

            { "application/x-rar-compressed", ".rar" },

            { "application/x-7z-compressed", ".7z" },

            { "application/msword", ".doc" },

            { "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx" },

            { "application/vnd.ms-excel", ".xls" },

            { "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx" },

            { "application/vnd.ms-powerpoint", ".ppt" },

            { "application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx" },

            { "text/plain", ".txt" }

        };



        static Program()

        {

            Directory.CreateDirectory(EXPORT_DIR);

            Directory.CreateDirectory(LOG_DIR);

            Directory.CreateDirectory(PERSONAS_DIR);



            var handler = new HttpClientHandler();

            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };



            WTelegram.Helpers.Log = (lvl, str) => LogToFile("TL", str);

        }



        static void Main(string[] args)

        {

            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;

            try

            {

                Run().GetAwaiter().GetResult();

            }

            catch (Exception ex)

            {

                WriteLineColored("FATAL", ex.Message, ConsoleColor.Red);

            }

            WriteLineColored("APP", "Готово. Нажмите любую клавишу...", ConsoleColor.Gray);

            Console.ReadKey();

        }



        static async Task Run()

        {

            // Telegram login

            _client = new Client(Config);

            _self = await _client.LoginUserIfNeeded();

            _selfId = GetSelfIdSafe(_self);

            WriteLineColored("AUTH", $"Вошли как: {(_self.username ?? ($"{_self.first_name} {_self.last_name}"))} (id={_selfId})", ConsoleColor.Gray);



            // Load personas & assignments

            _personas = LoadPersonas();

            EnsureDefaultPersona();

            _personaBindings = LoadPersonaBindings();



            // List dialogs

            var dialogs = await CallWithThrottle(async () => await _client.Messages_GetAllDialogs(), "Messages_GetAllDialogs");

            var dialogList = new List<(InputPeer peer, string title)>();

            int index = 1;



            foreach (var dlg in dialogs.dialogs)

            {

                string title = "Unknown";

                InputPeer peer = null;



                if (dlg.Peer is PeerUser)

                {

                    var pu = (PeerUser)dlg.Peer;

                    if (dialogs.users.TryGetValue(pu.user_id, out var u))

                    {

                        title = $"{(u.first_name ?? string.Empty).Trim()} {(u.last_name ?? string.Empty).Trim()}".Trim();

                        peer = new InputPeerUser(pu.user_id, u.access_hash);

                    }

                    else

                    {

                        peer = new InputPeerUser(pu.user_id, 0);

                        title = "User_" + pu.user_id;

                    }

                }

                else if (dlg.Peer is PeerChat)

                {

                    var pc = (PeerChat)dlg.Peer;

                    var ch = dialogs.chats.TryGetValue(pc.chat_id, out var chb) ? chb as Chat : null;

                    if (ch != null) title = ch.title;

                    peer = new InputPeerChat(pc.chat_id);

                }

                else if (dlg.Peer is PeerChannel)

                {

                    var pch = (PeerChannel)dlg.Peer;

                    Channel ch = null;

                    if (dialogs.chats.TryGetValue(pch.channel_id, out var chb)) ch = chb as Channel;

                    if (ch != null) title = ch.title;

                    peer = new InputPeerChannel(pch.channel_id, ch != null ? ch.access_hash : 0);

                }



                if (peer != null)

                {

                    Console.ForegroundColor = ConsoleColor.Gray;

                    Console.WriteLine($"{index}. {title}");

                    Console.ResetColor();

                    dialogList.Add((peer, title));

                    index++;

                }

            }



            if (dialogList.Count == 0)

            {

                WriteLineColored("APP", "Диалогов не найдено.", ConsoleColor.Red);

                return;

            }



            Console.ForegroundColor = ConsoleColor.Gray;

            Console.WriteLine("\nВведите номера диалогов через запятую/пробелы и диапазоны (например: 1,3-5,8):");

            Console.ResetColor();

            var raw = Console.ReadLine();

            var selectedIdx = ParseSelection(raw, dialogList.Count);

            if (selectedIdx.Count == 0)

            {

                WriteLineColored("APP", "Неверный выбор", ConsoleColor.Red);

                return;

            }

            var selected = selectedIdx.Select(i => dialogList[i - 1]).ToList();



            Console.ForegroundColor = ConsoleColor.Gray;

            Console.WriteLine("\nРежимы:");

            Console.WriteLine("1 - Экспорт переписки в CSV");

            Console.WriteLine("2 - Поддержать диалог (NeuroAPI) + трекинг состояния");

            Console.WriteLine("3 - Скачать все вложения диалога в ZIP");

            Console.Write("Введите 1, 2 или 3: ");

            Console.ResetColor();

            var mode = Console.ReadLine()?.Trim();



            if (mode == "2")

            {

                PreparePersonaConfigs(selected);

                var cts = new CancellationTokenSource();

                var tasks = new List<Task>();

                foreach (var item in selected)

                    tasks.Add(RunDialogWorker(item.peer, item.title, cts.Token));



                WriteLineColored("APP", $"\nМониторинг запущен для {selected.Count} чатов. Нажмите ENTER для остановки.", ConsoleColor.Gray);

                Console.ReadLine();

                cts.Cancel();

                await Task.WhenAll(tasks);

            }



            else if (mode == "3")



            {



                foreach (var item in selected)



                    await DownloadDialogMediaArchive(item.peer, item.title);



            }

            else

            {

                foreach (var item in selected)

                    await ExportDialog(item.peer, item.title, dialogs);

            }



            WriteLineColored("APP", "Всего RPC-запросов за сессию: " + _rpcCount, ConsoleColor.Gray);

        }



        // ======================= Worker поддержки диалога =======================



        private static async Task RunDialogWorker(InputPeer peer, string dialogTitle, CancellationToken ct)

        {

            var personaConfig = EnsureRuntimePersona(peer, dialogTitle);

            if (personaConfig == null)

            {

                WriteLineColored(dialogTitle, "Персона не назначена, диалог пропущен.", ConsoleColor.DarkYellow);

                return;

            }



            try

            {

                WriteLineColored(dialogTitle, "Запуск обработчика диалога:", ConsoleColor.Gray);



                var history = await CallWithThrottle(async () =>

                    await _client.Messages_GetHistory(peer, limit: 100),

                    $"Messages_GetHistory[init:{dialogTitle}]");



                var messages = history?.Messages?

                    .OfType<Message>()

                    .Where(HasMessageContent)

                    .OrderBy(m => m.id)

                    .ToList() ?? new List<Message>();



                int lastProcessedId = messages.Count > 0 ? messages.Max(m => m.id) : 0;

                int nonSelfSinceLastReply = 0;



                var state = AnalyzeState(messages, dialogTitle, out var goals);

                await SaveStateToFavorites(dialogTitle, state, goals);

                SaveStateToFile(dialogTitle, state, goals);



                if (SEND_INITIAL_REPLY)

                {

                    var lastMsg = messages.LastOrDefault();

                    var lastFromOtherAgo = (lastMsg != null && !IsFromSelf(lastMsg))

                        ? DateTime.UtcNow - ToUtc(lastMsg)

                        : TimeSpan.MaxValue;



                    if (lastFromOtherAgo >= IDLE_KICKIN)

                    {

                        try

                        {

                            var reply = await GetNeuroReply(messages, history, personaConfig, state, goals, ct);

                            if (!string.IsNullOrWhiteSpace(reply))

                            {

                                reply = PostprocessPunctuation(reply);

                                await SendReplyWithHumanization(peer, dialogTitle, reply, personaConfig, ct, isInitial: true);

                            }

                        }

                        catch (Exception ex)

                        {

                            LogToFile(dialogTitle, "Initial reply error: " + ex);

                        }

                    }

                }



                while (!ct.IsCancellationRequested)

                {

                    try

                    {

                        var delta = await CallWithThrottle(async () =>

                            await _client.Messages_GetHistory(peer, min_id: lastProcessedId, limit: 50),

                            $"Messages_GetHistory[min:{lastProcessedId}][{dialogTitle}]");



                        var newMsgs = delta?.Messages?

                            .OfType<Message>()

                            .Where(m => HasMessageContent(m) && m.id > lastProcessedId)

                            .OrderBy(m => m.id)

                            .ToList() ?? new List<Message>();



                        if (newMsgs.Count > 0)

                        {

                            foreach (var m in newMsgs)

                            {

                                if (!IsFromSelf(m))

                                {

                                    nonSelfSinceLastReply++;

                                    PrintIncomingWithColor(dialogTitle, m, delta);

                                }

                                if (m.id > lastProcessedId) lastProcessedId = m.id;

                            }



                            var ctx = await CallWithThrottle(async () =>

                                await _client.Messages_GetHistory(peer, limit: 100),

                                $"Messages_GetHistory[ctx:{dialogTitle}]");



                            var ctxMsgs = ctx?.Messages?

                                .OfType<Message>()

                                .Where(HasMessageContent)

                                .OrderBy(m => m.id)

                                .ToList() ?? new List<Message>();



                            var st = AnalyzeState(ctxMsgs, dialogTitle, out var gs);

                            if (HasStateMeaningfulChange(state, st, goals, gs))

                            {

                                state = st; goals = gs;

                                await SaveStateToFavorites(dialogTitle, state, goals);

                                SaveStateToFile(dialogTitle, state, goals);

                            }

                            else

                            {

                                state = st;

                                goals = gs;

                            }

                        }



                        if (nonSelfSinceLastReply >= REPLY_THRESHOLD)

                        {

                            var ctx = await CallWithThrottle(async () =>

                                await _client.Messages_GetHistory(peer, limit: 100),

                                $"Messages_GetHistory[ctx2:{dialogTitle}]");



                            var ctxMsgs = ctx?.Messages?

                                .OfType<Message>()

                                .Where(HasMessageContent)

                                .OrderBy(m => m.id)

                                .ToList() ?? new List<Message>();



                            if (ctxMsgs.Count > 0)

                            {

                                state = AnalyzeState(ctxMsgs, dialogTitle, out var gs);

                                goals = gs;



                                try

                                {

                                    var reply = await GetNeuroReply(ctxMsgs, ctx, personaConfig, state, goals, ct);

                                    if (!string.IsNullOrWhiteSpace(reply))

                                    {

                                        reply = PostprocessPunctuation(reply);

                                        await SendReplyWithHumanization(peer, dialogTitle, reply, personaConfig, ct, isInitial: false);

                                    }

                                    else

                                    {

                                        LogToFile(dialogTitle, "LLM returned empty reply, skipping send.");

                                    }

                                }

                                catch (Exception ex)

                                {

                                    LogToFile(dialogTitle, "Reply error: " + ex);

                                }

                            }

                            nonSelfSinceLastReply = 0;

                        }

                    }

                    catch (Exception ex)

                    {

                        LogToFile(dialogTitle, "Worker loop error: " + ex);

                    }



                    try

                    {

                        await Task.Delay(WORKER_POLL_DELAY, ct);

                    }

                    catch (TaskCanceledException)

                    {

                    }

                }

            }

            catch (OperationCanceledException)

            {

                WriteLineColored(dialogTitle, "Остановлена обработка диалога", ConsoleColor.Gray);

            }

            catch (Exception ex)

            {

                WriteLineColored(dialogTitle, "Ошибка worker: " + ex.Message, ConsoleColor.Red);

                LogToFile(dialogTitle, "Worker fatal: " + ex);

            }

        }







        private static void PrintIncomingWithColor(string dialogTitle, Message m, Messages_MessagesBase hist)

        {

            long senderId = -1;

            string senderName = "Unknown";

            if (m.from_id is PeerUser)

            {

                var pu = (PeerUser)m.from_id;

                senderId = pu.user_id;

                var users = GetUsersFromHistory(hist) ?? new Dictionary<long, User>();

                if (users.TryGetValue(pu.user_id, out var u))

                    senderName = $"{(u.first_name ?? "").Trim()} {(u.last_name ?? "").Trim()}".Trim();

            }

            else if (m.from_id is PeerChat pc)

            {

                senderId = pc.chat_id;

                senderName = dialogTitle;

            }



            var color = GetColorForUser(senderId);

            var body = FormatMessageForDisplay(m, hist);

            if (string.IsNullOrWhiteSpace(body)) body = "(без текста)";

            WriteLineColored($"{dialogTitle} [{senderName}]", body, color);

        }





        private static ConsoleColor GetColorForUser(long userId)

        {

            if (userId <= 0) userId = 999999; // для чатов/каналов

            if (_userColors.TryGetValue(userId, out var c)) return c;

            var rnd = new Random(unchecked((int)userId) ^ Environment.TickCount);

            var pick = _palette[rnd.Next(_palette.Length)];

            _userColors[userId] = pick;

            return pick;

        }



        // ======================= Экспорт переписки (режим 1) =======================

        static async Task ExportDialog(InputPeer peer, string dialogTitle, Messages_Dialogs dialogs)

        {

            string safeTitle = MakeSafeFileName(dialogTitle);

            string csvPath = Path.Combine(EXPORT_DIR, safeTitle + ".csv");



            WriteLineColored("EXPORT", "Экспорт: " + dialogTitle, ConsoleColor.Gray);



            using (var sw = new StreamWriter(csvPath, false, new UTF8Encoding(true)))

            {

                sw.WriteLine("DateTime;DialogTitle;Side;SenderId;SenderName;MessageId;Text");



                int offsetId = 0;

                while (true)

                {

                    var history = await CallWithThrottle(async () =>

                        await _client.Messages_GetHistory(peer, offset_id: offsetId, limit: PAGE_LIMIT),

                        "Messages_GetHistory");



                    if (history == null || history.Messages == null || !history.Messages.Any())

                        break;



                    int lastIdInPage = -1;



                    foreach (var msgBase in history.Messages)

                    {

                        var msg = msgBase as Message;

                        if (msg == null) continue;

                        if (!HasMessageContent(msg)) continue;



                        string side = "Other";

                        string senderName = "Unknown";

                        long senderId = -1;



                        if (msg.from_id is PeerUser pu)

                        {

                            senderId = pu.user_id;

                            if (dialogs.users.TryGetValue(pu.user_id, out var u))

                                senderName = $"{(u.first_name ?? string.Empty).Trim()} {(u.last_name ?? string.Empty).Trim()}".Trim();



                            if (pu.user_id == _selfId) side = "Me";

                        }

                        else if (msg.from_id is PeerChannel pch)

                        {

                            senderId = pch.channel_id;

                            if (dialogs.chats.TryGetValue(pch.channel_id, out var chb))

                                senderName = (chb as Channel)?.title ?? senderName;

                        }

                        else if (msg.from_id is PeerChat pc)

                        {

                            senderId = pc.chat_id;

                            if (dialogs.chats.TryGetValue(pc.chat_id, out var chb))

                                senderName = (chb as Chat)?.title ?? senderName;

                        }



                        DateTime dt;

                        if (msg.date is DateTime)

                            dt = ((DateTime)msg.date).ToLocalTime();

                        else

                            dt = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(msg.date)).LocalDateTime;



                        var rendered = FormatMessageForDisplay(msg, history);



                        sw.WriteLine(

                            dt.ToString("yyyy-MM-dd HH:mm:ss") + ";" +

                            Escape(dialogTitle) + ";" +

                            side + ";" +

                            senderId + ";" +

                            Escape(senderName) + ";" +

                            msg.id + ";" +

                            Escape((rendered ?? string.Empty).Replace("\n", " ").Trim())

                        );



                        lastIdInPage = msg.id;

                    }



                    if (lastIdInPage <= 0) break;

                    offsetId = (lastIdInPage == offsetId) ? lastIdInPage - 1 : lastIdInPage;

                }

            }



            WriteLineColored("EXPORT", "Сообщения сохранены в: " + csvPath, ConsoleColor.Gray);


        static async Task DownloadDialogMediaArchive(InputPeer peer, string dialogTitle)
        {
            var safeTitle = MakeSafeFileName(string.IsNullOrWhiteSpace(dialogTitle) ? "dialog" : dialogTitle);
            if (string.IsNullOrWhiteSpace(safeTitle))
                safeTitle = "dialog";
            var zipName = $"{safeTitle}_media_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            var zipPath = Path.Combine(EXPORT_DIR, zipName);

            WriteLineColored("MEDIA", $"Скачиваю вложения из \"{dialogTitle}\" в {zipName}", ConsoleColor.Gray);

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int offsetId = 0;
            int downloaded = 0;

            using (var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false, entryNameEncoding: Encoding.UTF8))
            {
                while (true)
                {
                    var history = await CallWithThrottle(async () =>
                        await _client.Messages_GetHistory(peer, offset_id: offsetId, limit: PAGE_LIMIT),
                        $"Messages_GetHistory[media:{dialogTitle}]");

                    if (history?.Messages == null || !history.Messages.Any())
                        break;

                    int lastIdInPage = -1;

                    foreach (var msgBase in history.Messages)
                    {
                        if (msgBase is not Message msg)
                            continue;

                        lastIdInPage = msg.id;

                        if (msg.media == null)
                            continue;

                        if (await TryDownloadMessageMedia(msg, archive, usedNames))
                            downloaded++;
                    }

                    if (lastIdInPage <= 0)
                        break;

                    offsetId = (lastIdInPage == offsetId) ? lastIdInPage - 1 : lastIdInPage;
                }
            }

            if (downloaded == 0)
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
                WriteLineColored("MEDIA", $"Во \"{dialogTitle}\" нет вложений для скачивания", ConsoleColor.DarkYellow);
            }
            else
            {
                WriteLineColored("MEDIA", $"Готово: {downloaded} файлов упакованы в {zipPath}", ConsoleColor.Gray);
            }
        }

        private static async Task<bool> TryDownloadMessageMedia(Message msg, ZipArchive archive, HashSet<string> usedNames)
        {
            try
            {
                if (msg.media is MessageMediaPhoto photoMedia)
                {
                    var photo = photoMedia.photo as Photo;
                    if (photo == null)
                        return false;

                    var fileName = BuildPhotoFileName(msg, photo);
                    await DownloadIntoArchiveEntry(fileName, archive, usedNames, async stream =>
                    {
                        await _client.DownloadFileAsync(photo, stream);
                    }, $"photo:{msg.id}");
                    return true;
                }

                if (msg.media is MessageMediaDocument docMedia)
                {
                    var document = docMedia.document as Document;
                    if (document == null)
                        return false;

                    var fileName = ResolveDocumentFileName(document, msg);
                    await DownloadIntoArchiveEntry(fileName, archive, usedNames, async stream =>
                    {
                        await _client.DownloadFileAsync(document, stream);
                    }, $"doc:{msg.id}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogToFile("MEDIA", $"Ошибка скачивания медиа msg={msg?.id}: {ex}");
            }

            return false;
        }

        private static async Task DownloadIntoArchiveEntry(string desiredName, ZipArchive archive, HashSet<string> usedNames, Func<Stream, Task> downloader, string tag)
        {
            var entryName = EnsureUniqueEntryName(desiredName, usedNames);
            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            try
            {
                using (var entryStream = entry.Open())
                {
                    await CallWithThrottle(async () => await downloader(entryStream), $"Download[{tag}]");
                }
            }
            catch
            {
                try { entry.Delete(); }
                catch { }
                throw;
            }
        }

        private static string EnsureUniqueEntryName(string fileName, HashSet<string> usedNames)
        {
            var safe = MakeSafeFileName(string.IsNullOrWhiteSpace(fileName) ? "file" : fileName);
            if (string.IsNullOrWhiteSpace(safe))
                safe = "file";

            var ext = Path.GetExtension(safe);
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = ".bin";
                safe += ext;
            }

            var baseName = safe.Substring(0, safe.Length - ext.Length);
            var candidate = safe;
            int index = 1;
            while (!usedNames.Add(candidate))
            {
                candidate = $"{baseName}_{index++}{ext}";
            }
            return candidate;
        }

        private static string ResolveDocumentFileName(Document doc, Message msg)
        {
            var attr = doc?.attributes?.OfType<DocumentAttributeFilename>().FirstOrDefault();
            var name = attr?.file_name;
            if (string.IsNullOrWhiteSpace(name))
            {
                var dt = ResolveMessageLocalTime(msg);
                var mimeMarker = string.IsNullOrWhiteSpace(doc?.mime_type) ? "document" : doc.mime_type.Replace('/', '_');
                name = $"{dt:yyyyMMdd_HHmmss}_{msg?.id ?? 0}_{mimeMarker}";
            }

            name = MakeSafeFileName(name);
            if (string.IsNullOrWhiteSpace(Path.GetExtension(name)))
            {
                var ext = GuessExtensionFromMime(doc?.mime_type);
                if (!string.IsNullOrEmpty(ext))
                    name += ext;
            }

            if (string.IsNullOrWhiteSpace(Path.GetExtension(name)))
                name += ".bin";

            return name;
        }

        private static string BuildPhotoFileName(Message msg, Photo photo)
        {
            var dt = ResolveMessageLocalTime(msg);
            var idPart = photo?.id ?? 0;
            return $"{dt:yyyyMMdd_HHmmss}_{msg?.id ?? 0}_photo_{idPart}.jpg";
        }

        private static DateTime ResolveMessageLocalTime(Message msg)
        {
            if (msg?.date is DateTime dt)
                return dt.ToLocalTime();

            if (msg?.date is DateTimeOffset dto)
                return dto.LocalDateTime;

            if (msg?.date != null)
            {
                if (long.TryParse(msg.date.ToString(), out var unix))
                    return DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
            }

            return DateTime.Now;
        }

        private static string GuessExtensionFromMime(string mime)
        {
            if (string.IsNullOrWhiteSpace(mime))
                return null;

            if (_mimeToExtension.TryGetValue(mime.Trim(), out var ext))
                return ext;

            if (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                return ".txt";

            return null;
        }


        }



        // ======================= NeuroAPI (диалог) =======================



        static async Task<string> GetNeuroReply(List<Message> msgs, Messages_MessagesBase lookup, PersonaRuntimeConfig personaConfig, DialogState state, GoalState goals, CancellationToken ct)

        {

            var chatMessages = BuildChatMessages(msgs, lookup, personaConfig, state, goals);

            if (chatMessages == null || chatMessages.Count <= 1)

                return null;



            var payload = new ChatCompletionRequest

            {

                model = DEFAULT_MODEL,

                temperature = ComputeAdaptiveTemperature(personaConfig, state),

                max_tokens = ResolveMaxTokens(personaConfig),

                messages = chatMessages

            };



            return await SendNeuroChat(payload, ct);

        }







        private static async Task<string> SendNeuroChat(ChatCompletionRequest payload, CancellationToken ct)

        {

            var reqBytes = SerializeJson(payload);

            Exception lastEx = null;



            for (int attempt = 0; attempt < NEURO_BASES.Length; attempt++)

            {

                var baseUrl = NEURO_BASES[attempt];

                try

                {

                    using (var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions"))

                    {

                        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", NEUROAPI_KEY);

                        req.Headers.TryAddWithoutValidation("Accept", "application/json");

                        req.Headers.TryAddWithoutValidation("User-Agent", "TgDialogsExport472/1.0");

                        req.Content = new ByteArrayContent(reqBytes);

                        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");



                        _neuroCalls++;

                        var resp = await _http.SendAsync(req, ct);

                        var bodyBytes = await resp.Content.ReadAsByteArrayAsync();

                        var body = Encoding.UTF8.GetString(bodyBytes ?? Array.Empty<byte>());



                        LogToFile("NEURO", $"POST {baseUrl}/chat/completions -> {(int)resp.StatusCode} {resp.StatusCode}");

                        LogToFile("NEURO", $"Model={payload.model}; messages={payload.messages?.Count ?? 0}; temp={payload.temperature:F2}; max_tokens={payload.max_tokens}");

                        LogToFile("NEURO", $"Response body: {body}");



                        if (!resp.IsSuccessStatusCode)

                        {

                            LogToFile("NEURO", $"Non-success status {(int)resp.StatusCode} {resp.StatusCode} received from {baseUrl}");

                            continue;

                        }



                        try

                        {

                            var data = DeserializeJson<ChatCompletionResponse>(bodyBytes);

                            var txt = data?.choices?.FirstOrDefault()?.message?.content?.Trim();

                            if (!string.IsNullOrEmpty(txt)) return txt;

                        }

                        catch (Exception ex)

                        {

                            LogToFile("NEURO", "Failed to parse structured response: " + ex.Message);

                        }



                        try

                        {

                            var alt = System.Text.Json.JsonDocument.Parse(body);

                            if (alt.RootElement.TryGetProperty("content", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)

                                return c.GetString().Trim();

                        }

                        catch (Exception ex)

                        {

                            LogToFile("NEURO", "Fallback JSON parse failed: " + ex.Message);

                        }



                        WriteLineColored("NEURO", "Модель не вернула текст ответа, пробую следующий эндпоинт...", ConsoleColor.DarkYellow);

                    }

                }

                catch (OperationCanceledException)

                {

                    throw;

                }

                catch (Exception ex)

                {

                    lastEx = ex;

                    LogToFile("NEURO", $"Attempt {attempt + 1} failed: {ex.GetType().Name}: {ex.Message}");

                }

            }



            WriteLineColored("NEURO", "Не удалось получить ответ от модели. Подробности в логе.", ConsoleColor.DarkYellow);

            if (lastEx != null) LogToFile("NEURO", "Last exception: " + lastEx);

            return null;

        }











        private static string BuildDefaultPersonaPrompt()

        {

            return string.Join(" ", new[]

            {

                "Ты — тёплый и остроумный собеседник, говорящий на русском языке простыми живыми фразами.",

                "Избегай официального тона, допускай лёгкие эмоции и забавные комментарии, если это уместно.",

                "Пытайся уточнять важные детали, чтобы лучше понимать собеседника.",

                "Если ответ длинный, дели его на несколько сообщений и разделяй части тегом <msg>.",

                "Обращай внимание на вложения, описанные в квадратных скобках, и используй их в ответе."

            });

        }







        private static bool HasMessageContent(Message message)

        {

            if (message == null) return false;

            if (!string.IsNullOrWhiteSpace(message.message)) return true;

            return message.media != null;

        }



        private static string RenderContentForLLM(Message message, Messages_MessagesBase hist)

        {

            if (message == null) return string.Empty;



            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(message.message))

                parts.Add(message.message.Trim());



            var media = DescribeMedia(message);

            if (!string.IsNullOrWhiteSpace(media))

                parts.Add(media);



            return string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        }



        private static string FormatMessageForDisplay(Message message, Messages_MessagesBase hist)

        {

            var content = RenderContentForLLM(message, hist);

            if (string.IsNullOrWhiteSpace(content)) return string.Empty;

            return Regex.Replace(content, @"\s+", " ").Trim();

        }



        private static string DescribeMedia(Message message)

        {

            if (message == null || message.media == null) return null;



            var photoMedia = message.media as MessageMediaPhoto;

            if (photoMedia != null)

                return DescribePhotoMedia(photoMedia);



            var docMedia = message.media as MessageMediaDocument;

            if (docMedia != null)

                return DescribeDocumentMedia(docMedia);



            var contact = message.media as MessageMediaContact;

            if (contact != null)

                return $"[контакт {contact.phone_number} {contact.first_name} {contact.last_name}]".Trim();



            if (message.media is MessageMediaGeo)

                return "[геолокация]";



            var poll = message.media as MessageMediaPoll;

            if (poll != null)

                return $"[опрос: {poll.poll?.question}]";



            var dice = message.media as MessageMediaDice;

            if (dice != null)

                return $"[кубик {dice.emoticon}={dice.value}]";



            return $"[вложение {message.media.GetType().Name}]";

        }



        private static string DescribePhotoMedia(MessageMediaPhoto media)

        {

            if (media == null) return "[фото]";



            var sb = new StringBuilder("[фото");

            var photo = media.photo as Photo;

            if (photo != null && photo.sizes != null)

            {

                var size = photo.sizes.OfType<PhotoSize>().OrderByDescending(s => s.w * s.h).FirstOrDefault();

                if (size != null) sb.Append($" {size.w}x{size.h}");

            }

            sb.Append(']');

            return sb.ToString();

        }



        private static string DescribeDocumentMedia(MessageMediaDocument media)

        {

            if (media == null) return "[файл]";

            var doc = media.document as Document;

            if (doc == null) return "[файл]";



            var attrs = doc.attributes ?? Array.Empty<DocumentAttribute>();

            var sticker = attrs.OfType<DocumentAttributeSticker>().FirstOrDefault();

            if (sticker != null)

            {

                var emoji = string.IsNullOrWhiteSpace(sticker.alt) ? string.Empty : " " + sticker.alt;

                return $"[стикер{emoji}]";

            }



            var audio = attrs.OfType<DocumentAttributeAudio>().FirstOrDefault();

            if (audio != null)

                return $"[аудио ~{audio.duration}s]";



            var video = attrs.OfType<DocumentAttributeVideo>().FirstOrDefault();

            if (video != null)

                return $"[видео {video.w}x{video.h} ~{video.duration}s]";



            var fileAttr = attrs.OfType<DocumentAttributeFilename>().FirstOrDefault();

            var name = fileAttr != null && !string.IsNullOrWhiteSpace(fileAttr.file_name)

                ? fileAttr.file_name

                : (string.IsNullOrWhiteSpace(doc.mime_type) ? "файл" : doc.mime_type);

            var sizeKb = doc.size / 1024.0;

            return $"[файл {name} ~{sizeKb:F1}KB]";

        }



        private static async Task SendReplyWithHumanization(InputPeer peer, string dialogTitle, string reply, PersonaRuntimeConfig personaConfig, CancellationToken ct, bool isInitial)

        {

            if (string.IsNullOrWhiteSpace(reply)) return;

            var chunks = SplitIntoChunks(reply, 5);

            foreach (var chunk in chunks)

            {

                var typingDuration = CalculateTypingDuration(chunk, personaConfig);

                await SimulateTypingAsync(peer, typingDuration, ct, dialogTitle);



                await CallWithThrottle(async () =>

                {

                    await _client.Messages_SendMessage(peer, chunk, random_id: WTelegram.Helpers.RandomLong());

                }, $"Messages_SendMessage[{dialogTitle}]" + (isInitial ? "[initial]" : string.Empty));



                WriteLineColored(dialogTitle + " [BOT]", chunk, ConsoleColor.Green);



                var pause = ResolvePauseBetweenMessages(personaConfig);

                if (pause > 0)

                {

                    var rand = _random.Value?.NextDouble() ?? new Random().NextDouble();

                    var delaySeconds = pause + (rand - 0.5) * pause * 0.2;

                    if (delaySeconds > 0.05)

                    {

                        try

                        {

                            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);

                        }

                        catch (TaskCanceledException)

                        {

                        }

                    }

                }

            }

        }



        private static async Task SimulateTypingAsync(InputPeer peer, TimeSpan duration, CancellationToken ct, string tag)

        {

            if (duration <= TimeSpan.Zero) return;

            var elapsed = TimeSpan.Zero;

            while (elapsed < duration && !ct.IsCancellationRequested)

            {

                await SendTypingAction(peer, tag);

                var remaining = duration - elapsed;

                var slice = remaining > TimeSpan.FromSeconds(3) ? TimeSpan.FromSeconds(3) : remaining;

                try

                {

                    await Task.Delay(slice, ct);

                }

                catch (TaskCanceledException)

                {

                }

                elapsed += slice;

            }

        }



        private static async Task SendTypingAction(InputPeer peer, string tag)

        {

            try

            {

                await _client.Messages_SetTyping(peer, new SendMessageTypingAction());

            }

            catch (RpcException ex)

            {

                if (ex.Code == 420 || (ex.Message != null && ex.Message.IndexOf("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase) >= 0))

                {

                    int wait = ExtractFloodWaitSeconds(ex.Message);

                    await Task.Delay((wait + 1) * 1000);

                }

                else

                {

                    LogToFile(tag, "Typing action failed: " + ex.Message);

                }

            }

            catch (Exception ex)

            {

                LogToFile(tag, "Typing action failed: " + ex.Message);

            }

        }



        private static TimeSpan CalculateTypingDuration(string text, PersonaRuntimeConfig personaConfig)

        {

            if (string.IsNullOrWhiteSpace(text))

                return TimeSpan.FromSeconds(MIN_TYPING_DELAY_SECONDS);



            var speed = ResolveTypingSpeed(personaConfig);

            var cps = Math.Max(2.0, speed * 5.0 / 60.0);

            var rand = _random.Value?.NextDouble() ?? new Random().NextDouble();

            var seconds = text.Length / cps;

            seconds *= 0.9 + rand * 0.2;

            seconds = ClampDouble(seconds, MIN_TYPING_DELAY_SECONDS, MAX_TYPING_DELAY_SECONDS);

            return TimeSpan.FromSeconds(seconds);

        }



        private static double ResolveTypingSpeed(PersonaRuntimeConfig personaConfig)

        {

            var wpm = personaConfig?.Binding?.TypingSpeedWpm ?? DEFAULT_TYPING_SPEED_WPM;

            return ClampDouble(wpm, 60, 600);

        }



        private static double ResolvePauseBetweenMessages(PersonaRuntimeConfig personaConfig)

        {

            var pause = personaConfig?.Binding?.PauseBetweenMessagesSec ?? 0.8;

            return ClampDouble(pause, 0.1, 4.0);

        }



        private static double ComputeAdaptiveTemperature(PersonaRuntimeConfig config, DialogState state)

        {

            var temp = config?.Binding?.Temperature ?? 0.6;

            if (state != null)

                temp += (state.Arousal - 50) / 200.0;

            return ClampDouble(temp, 0.35, 0.95);

        }



        private static int ResolveMaxTokens(PersonaRuntimeConfig config)

        {

            var tokens = config?.Binding?.MaxTokens ?? 500;

            return Clamp(tokens, 200, 1200);

        }



        private static List<string> SplitIntoChunks(string content, int maxChunks)

        {

            if (string.IsNullOrWhiteSpace(content)) return new List<string>();



            var parts = content.Split(new[] { "<msg>" }, StringSplitOptions.RemoveEmptyEntries)

                                .Select(p => p.Trim())

                                .Where(p => p.Length > 0)

                                .ToList();



            if (parts.Count == 0)

                parts.Add(content.Trim());



            if (parts.Count == 1 && parts[0].Length > 280)

                parts = SmartSentenceSplit(parts[0], maxChunks);



            if (parts.Count > maxChunks)

                parts = parts.Take(maxChunks).ToList();



            return parts;

        }



        private static List<string> SmartSentenceSplit(string content, int maxChunks)

        {

            var sentences = Regex.Split(content, @"(?<=[.!?])\s+");

            var chunks = new List<string>();

            var builder = new StringBuilder();

            foreach (var sentence in sentences)

            {

                var trimmed = sentence.Trim();

                if (trimmed.Length == 0) continue;



                if (builder.Length > 0 && (builder.Length + trimmed.Length) > 220 && chunks.Count + 1 < maxChunks)

                {

                    chunks.Add(builder.ToString().Trim());

                    builder.Clear();

                }



                if (builder.Length > 0) builder.Append(' ');

                builder.Append(trimmed);



                if (chunks.Count >= maxChunks) break;

            }

            if (builder.Length > 0 && chunks.Count < maxChunks)

                chunks.Add(builder.ToString().Trim());



            if (chunks.Count == 0)

                chunks.Add(content.Trim());

            return chunks;

        }



        private static double ClampDouble(double value, double min, double max)

        {

            if (value < min) return min;

            if (value > max) return max;

            return value;

        }



        // === Utils: недостающие методы ===

        private static long GetSelfIdSafe(User u)

        {

            if (u == null) return -1;

            var t = u.GetType();

            var f = t.GetField("id") ?? t.GetField("ID");

            if (f != null) return Convert.ToInt64(f.GetValue(u));

            var p = t.GetProperty("Id") ?? t.GetProperty("ID") ?? t.GetProperty("id");

            if (p != null) return Convert.ToInt64(p.GetValue(u, null));

            var m = t.GetMethod("ID", BindingFlags.Public | BindingFlags.Instance);

            if (m != null) return Convert.ToInt64(m.Invoke(u, null));

            return -1;

        }

        private static int Clamp(int value, int min, int max)

        {

            if (value < min) return min;

            if (value > max) return max;

            return value;

        }

        private static IEnumerable<T> TakeLastCompat<T>(IEnumerable<T> source, int count)

        {

            if (source == null || count <= 0)

                yield break;

            var q = new Queue<T>(count);

            foreach (var item in source)

            {

                if (q.Count == count) q.Dequeue();

                q.Enqueue(item);

            }

            foreach (var x in q)

                yield return x;

        }









        private static List<Persona> LoadPersonas()

        {

            try

            {

                if (File.Exists(PERSONAS_FILE))

                {

                    var bytes = File.ReadAllBytes(PERSONAS_FILE);

                    var list = DeserializeJson<List<Persona>>(bytes);

                    return list ?? new List<Persona>();

                }

            }

            catch (Exception ex)

            {

                LogToFile("PERSONA", "LoadPersonas error: " + ex);

            }

            return new List<Persona>();

        }



        private static void SavePersonas(List<Persona> list)

        {

            try

            {

                var bytes = SerializeJson(list ?? new List<Persona>());

                File.WriteAllBytes(PERSONAS_FILE, bytes);

            }

            catch (Exception ex)

            {

                LogToFile("PERSONA", "SavePersonas error: " + ex);

            }

        }



        private static void EnsureDefaultPersona()

        {

            if (_personas.Any()) return;

            _personas.Add(new Persona { Name = "default", SystemPrompt = BuildDefaultPersonaPrompt() });

            SavePersonas(_personas);

        }



        private static Persona ChoosePersonaInteractive(List<Persona> personas)

        {

            while (true)

            {

                Console.ForegroundColor = ConsoleColor.Gray;

                Console.WriteLine();

                Console.WriteLine("Персоны:");

                for (int i = 0; i < personas.Count; i++)

                    Console.WriteLine($"{i + 1}. {personas[i].Name}");

                Console.WriteLine("n - добавить новую");

                Console.Write("Выберите номер или 'n': ");

                Console.ResetColor();



                var ans = (Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();

                if (ans == "n")

                {

                    Console.ForegroundColor = ConsoleColor.Gray;

                    Console.Write("Имя персоны: ");

                    Console.ResetColor();

                    var name = Console.ReadLine()?.Trim();

                    if (string.IsNullOrWhiteSpace(name)) continue;



                    Console.ForegroundColor = ConsoleColor.Gray;

                    Console.WriteLine("Введите system prompt:");

                    Console.ResetColor();

                    var prompt = Console.ReadLine() ?? string.Empty;



                    var p = new Persona { Name = name, SystemPrompt = prompt };

                    personas.Add(p);

                    SavePersonas(personas);

                    WriteLineColored("PERSONA", $"Добавлена персона '{name}'", ConsoleColor.Gray);

                    return p;

                }

                if (int.TryParse(ans, out var idx) && idx >= 1 && idx <= personas.Count)

                {

                    var chosen = personas[idx - 1];

                    WriteLineColored("PERSONA", $"Выбрана персона '{chosen.Name}'", ConsoleColor.Gray);

                    return chosen;

                }

            }

        }



        private static void PreparePersonaConfigs(List<(InputPeer peer, string title)> selected)

        {

            if (selected == null || selected.Count == 0) return;



            Console.ForegroundColor = ConsoleColor.Gray;

            Console.WriteLine();

            Console.WriteLine("Персоны для выбранных диалогов:");

            for (int i = 0; i < selected.Count; i++)

            {

                var key = GetPeerKey(selected[i].peer);

                var personaName = _personaBindings.TryGetValue(key, out var binding) && !string.IsNullOrWhiteSpace(binding.PersonaName)

                    ? binding.PersonaName

                    : "<не назначена>";

                Console.WriteLine($"{i + 1}. {selected[i].title} -> {personaName}");

            }

            Console.Write("Введите номера диалогов для смены персоны (например 1,3) или просто Enter: ");

            Console.ResetColor();

            var raw = Console.ReadLine();

            var indexes = ParseSelection(raw, selected.Count);



            var updated = false;

            foreach (var idx in indexes)

            {

                if (idx < 1 || idx > selected.Count) continue;

                var record = PromptPersonaBinding(selected[idx - 1].title);

                if (record != null)

                {

                    var key = GetPeerKey(selected[idx - 1].peer);

                    record.Key = key;

                    _personaBindings[key] = record;

                    updated = true;

                }

            }



            if (!_personas.Any())

                EnsureDefaultPersona();



            foreach (var item in selected)

            {

                var key = GetPeerKey(item.peer);

                if (!_personaBindings.TryGetValue(key, out var binding) || ResolvePersona(binding.PersonaName) == null)

                {

                    var persona = _personas.FirstOrDefault();

                    if (persona == null) continue;

                    _personaBindings[key] = new PersonaBindingRecord

                    {

                        Key = key,

                        PersonaName = persona.Name

                    };

                    updated = true;

                }

            }



            if (updated)

            {

                SavePersonaBindings(_personaBindings);

            }



            lock (_personaLock)

            {

                foreach (var item in selected)

                {

                    var key = GetPeerKey(item.peer);

                    _runtimeDialogConfigs.Remove(key);

                    if (_personaBindings.TryGetValue(key, out var binding))

                    {

                        var persona = ResolvePersona(binding.PersonaName);

                        if (persona != null)

                        {

                            _runtimeDialogConfigs[key] = new PersonaRuntimeConfig

                            {

                                Persona = persona,

                                Binding = binding

                            };

                        }

                    }

                }

            }

        }



        private static PersonaBindingRecord PromptPersonaBinding(string dialogTitle)

        {

            WriteLineColored("PERSONA", $"Настройка персоны для '{dialogTitle}'", ConsoleColor.Gray);

            if (!_personas.Any())

                EnsureDefaultPersona();



            var persona = ChoosePersonaInteractive(_personas);

            if (persona == null) return null;



            Console.ForegroundColor = ConsoleColor.Gray;

            Console.Write("Дополнительные инструкции (Enter — пропустить): ");

            Console.ResetColor();

            var extra = Console.ReadLine()?.Trim();



            Console.ForegroundColor = ConsoleColor.Gray;

            Console.Write("Температура (0.1-1.5, Enter=0.6): ");

            Console.ResetColor();

            var tempRaw = Console.ReadLine()?.Trim();

            double? temperature = null;

            if (double.TryParse(tempRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))

                temperature = ClampDouble(t, 0.1, 1.5);



            Console.ForegroundColor = ConsoleColor.Gray;

            Console.Write("Максимум токенов (Enter=500): ");

            Console.ResetColor();

            var tokensRaw = Console.ReadLine()?.Trim();

            int? maxTokens = null;

            if (int.TryParse(tokensRaw, out var mt) && mt > 0)

                maxTokens = Clamp(mt, 100, 2000);



            Console.ForegroundColor = ConsoleColor.Gray;

            Console.Write("Скорость печати WPM (Enter=220): ");

            Console.ResetColor();

            var wpmRaw = Console.ReadLine()?.Trim();

            double? typing = null;

            if (double.TryParse(wpmRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var wpm))

                typing = ClampDouble(wpm, 60, 600);



            Console.ForegroundColor = ConsoleColor.Gray;

            Console.Write("Пауза между сообщениями (сек, Enter=0.8): ");

            Console.ResetColor();

            var pauseRaw = Console.ReadLine()?.Trim();

            double? pause = null;

            if (double.TryParse(pauseRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))

                pause = ClampDouble(p, 0.1, 4.0);



            return new PersonaBindingRecord

            {

                PersonaName = persona.Name,

                ExtraInstructions = string.IsNullOrWhiteSpace(extra) ? null : extra,

                Temperature = temperature,

                MaxTokens = maxTokens,

                TypingSpeedWpm = typing,

                PauseBetweenMessagesSec = pause

            };

        }



        private static Dictionary<string, PersonaBindingRecord> LoadPersonaBindings()

        {

            try

            {

                if (File.Exists(PERSONA_ASSIGNMENTS_FILE))

                {

                    var bytes = File.ReadAllBytes(PERSONA_ASSIGNMENTS_FILE);

                    var list = DeserializeJson<List<PersonaBindingRecord>>(bytes);

                    if (list != null)

                    {

                        return list

                            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Key) && !string.IsNullOrWhiteSpace(r.PersonaName))

                            .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)

                            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

                    }

                }

            }

            catch (Exception ex)

            {

                LogToFile("PERSONA", "LoadPersonaBindings error: " + ex);

            }

            return new Dictionary<string, PersonaBindingRecord>(StringComparer.OrdinalIgnoreCase);

        }



        private static void SavePersonaBindings(Dictionary<string, PersonaBindingRecord> records)

        {

            try

            {

                var list = records?.Values

                    .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Key) && !string.IsNullOrWhiteSpace(r.PersonaName))

                    .Distinct()

                    .ToList() ?? new List<PersonaBindingRecord>();

                var bytes = SerializeJson(list);

                File.WriteAllBytes(PERSONA_ASSIGNMENTS_FILE, bytes);

            }

            catch (Exception ex)

            {

                LogToFile("PERSONA", "SavePersonaBindings error: " + ex);

            }

        }



        private static PersonaRuntimeConfig EnsureRuntimePersona(InputPeer peer, string dialogTitle)

        {

            var key = GetPeerKey(peer);

            lock (_personaLock)

            {

                if (_runtimeDialogConfigs.TryGetValue(key, out var cached))

                    return cached;

            }



            if (!_personaBindings.TryGetValue(key, out var binding) || ResolvePersona(binding.PersonaName) == null)

            {

                EnsureDefaultPersona();

                var persona = _personas.FirstOrDefault();

                if (persona == null) return null;

                binding = new PersonaBindingRecord { Key = key, PersonaName = persona.Name };

                _personaBindings[key] = binding;

                SavePersonaBindings(_personaBindings);

            }



            var resolved = ResolvePersona(binding.PersonaName) ?? _personas.FirstOrDefault();

            if (resolved == null) return null;



            var runtime = new PersonaRuntimeConfig

            {

                Persona = resolved,

                Binding = binding

            };



            lock (_personaLock)

            {

                _runtimeDialogConfigs[key] = runtime;

            }



            return runtime;

        }



        private static Persona ResolvePersona(string personaName)

        {

            if (string.IsNullOrWhiteSpace(personaName)) return null;

            return _personas.FirstOrDefault(p => string.Equals(p.Name, personaName, StringComparison.OrdinalIgnoreCase));

        }



        private static string GetPeerKey(InputPeer peer)

        {

            if (peer is InputPeerUser user)

                return $"user_{user.user_id}";



            var chat = peer as InputPeerChat;

            if (chat != null)

                return $"chat_{chat.chat_id}";



            var channel = peer as InputPeerChannel;

            if (channel != null)

                return $"channel_{channel.channel_id}";



            if (peer is InputPeerSelf)

                return "self";



            return $"peer_{peer.GetType().Name}";

        }





        private static List<ChatMessage> BuildChatMessages(List<Message> msgs, Messages_MessagesBase lookup, PersonaRuntimeConfig personaConfig, DialogState state, GoalState goals)

        {

            var systemParts = new List<string>();

            var personaPrompt = personaConfig?.Persona?.SystemPrompt;

            if (string.IsNullOrWhiteSpace(personaPrompt))

                personaPrompt = BuildDefaultPersonaPrompt();

            if (!string.IsNullOrWhiteSpace(personaPrompt))

                systemParts.Add(personaPrompt);

            systemParts.Add("Отвечай как живой человек, используй живые разговорные фразы и эмоции, если это уместно.");

            systemParts.Add("Если ответ получается длинным, разбивай его на несколько сообщений и разделяй части тегом <msg>.");

            systemParts.Add("Если в переписке встречаются вложения в квадратных скобках, учитывай их при формировании ответа.");

            if (!string.IsNullOrWhiteSpace(personaConfig?.Binding?.ExtraInstructions))

                systemParts.Add(personaConfig.Binding.ExtraInstructions);

            if (state != null)

                systemParts.Add($"Эмоциональный фон диалога: {state.Arousal}/100. Вероятность сближения в ближайшие 10 минут: {state.IntimacyProbabilityNext10}%.");

            var goalsLine = DescribeGoals(goals);

            if (!string.IsNullOrWhiteSpace(goalsLine))

                systemParts.Add(goalsLine);



            var chatMessages = new List<ChatMessage>

            {

                new ChatMessage { role = "system", content = string.Join("\n\n", systemParts.Where(s => !string.IsNullOrWhiteSpace(s))) }

            };



            if (msgs != null)

            {

                foreach (var message in msgs)

                {

                    var content = RenderContentForLLM(message, lookup);

                    if (string.IsNullOrWhiteSpace(content)) continue;

                    chatMessages.Add(new ChatMessage

                    {

                        role = IsFromSelf(message) ? "assistant" : "user",

                        content = content

                    });

                }

            }



            return chatMessages;

        }



        private static string PostprocessPunctuation(string content)

        {

            if (string.IsNullOrWhiteSpace(content)) return content;

            return Regex.Replace(content, @"\s+", " ").Trim();

        }



        private static string DescribeGoals(GoalState goals)

        {

            if (goals == null) return string.Empty;

            var items = new List<string>();

            if (goals.G1_MaleFMaleKnown) items.Add("собеседник знает о других отношениях");

            if (goals.G2_OtherPartnerInterest) items.Add("интерес к другим партнёрам");

            if (goals.G3_WithoutMe) items.Add("рассказы о близости без меня");

            if (goals.G4_WithGirl) items.Add("интерес к девушке");

            if (goals.G5_SelfVideo) items.Add("готовность отправить видео");

            if (goals.G6_VoicedFantasies) items.Add("делится фантазиями");

            if (!items.Any()) return string.Empty;

            return "Контекст целей: " + string.Join(", ", items) + ".";

        }



        private static Dictionary<long, User> GetUsersFromHistory(Messages_MessagesBase hist)

        {

            if (hist is Messages_Messages mm && mm.users != null) return mm.users;

            if (hist is Messages_ChannelMessages mcm && mcm.users != null) return mcm.users;

            return null;

        }



        private static Dictionary<long, ChatBase> GetChatsFromHistory(Messages_MessagesBase hist)

        {

            if (hist is Messages_Messages mm && mm.chats != null) return mm.chats;

            if (hist is Messages_ChannelMessages mcm && mcm.chats != null) return mcm.chats;

            return null;

        }



        private static bool IsFromSelf(Message msg)

        {

            if (msg?.from_id is PeerUser pu)

                return pu.user_id == _selfId;

            return false;

        }



        private static async Task<T> CallWithThrottle<T>(Func<Task<T>> call, string tag)

        {

            while (true)

            {

                try

                {

                    var result = await call();

                    _rpcCount++;

                    await Task.Delay(REQUEST_DELAY_MS);

                    return result;

                }

                catch (RpcException ex)

                {

                    if (ex.Code == 420 || (ex.Message != null && ex.Message.IndexOf("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase) >= 0))

                    {

                        int waitSec = ExtractFloodWaitSeconds(ex.Message);

                        if (waitSec < 1) waitSec = 1;

                        WriteLineColored("FLOOD_WAIT", $"Жду {waitSec} сек перед повтором ({tag})", ConsoleColor.DarkYellow);

                        await Task.Delay((waitSec + 1) * 1000);

                        continue;

                    }

                    throw;

                }

            }

        }



        private static async Task CallWithThrottle(Func<Task> call, string tag)

        {

            while (true)

            {

                try

                {

                    await call();

                    _rpcCount++;

                    await Task.Delay(REQUEST_DELAY_MS);

                    return;

                }

                catch (RpcException ex)

                {

                    if (ex.Code == 420 || (ex.Message != null && ex.Message.IndexOf("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase) >= 0))

                    {

                        int waitSec = ExtractFloodWaitSeconds(ex.Message);

                        if (waitSec < 1) waitSec = 1;

                        WriteLineColored("FLOOD_WAIT", $"Жду {waitSec} сек перед повтором ({tag})", ConsoleColor.DarkYellow);

                        await Task.Delay((waitSec + 1) * 1000);

                        continue;

                    }

                    throw;

                }

            }

        }



        private static int ExtractFloodWaitSeconds(string message)

        {

            if (string.IsNullOrEmpty(message)) return 1;

            int num = 0;

            foreach (var ch in message)

            {

                if (ch >= '0' && ch <= '9') num = num * 10 + (ch - '0');

                else if (num > 0) break;

            }

            return num > 0 ? num : 1;

        }



        private static string Config(string what)

        {

            if (what == "api_id") return API_ID.ToString();

            if (what == "api_hash") return API_HASH;

            if (what == "phone_number") return PHONE_NUMBER;

            if (what == "verification_code") { Console.Write("Код из Telegram: "); return Console.ReadLine(); }

            if (what == "password") { Console.Write("Пароль 2FA: "); return ReadPassword(); }

            return null;

        }



        private static string ReadPassword()

        {

            var sb = new StringBuilder();

            while (true)

            {

                var key = Console.ReadKey(true);

                if (key.Key == ConsoleKey.Enter) break;

                if (key.Key == ConsoleKey.Backspace && sb.Length > 0)

                {

                    sb.Length--;

                    continue;

                }

                if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);

            }

            Console.WriteLine();

            return sb.ToString();

        }



        private static void LogToFile(string tag, string text)

        {

            try

            {

                var logPath = Path.Combine(LOG_DIR, $"{DateTime.Now:yyyyMMdd}.log");

                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] [{tag}] {text}" + Environment.NewLine);

            }

            catch

            {

            }

        }



        private static void WriteLineColored(string tag, string text, ConsoleColor color)

        {

            var prev = Console.ForegroundColor;

            Console.ForegroundColor = color;

            Console.WriteLine($"[{tag}] {text}");

            Console.ForegroundColor = prev;

        }



        private static string MakeSafeFileName(string name)

        {

            var invalid = Path.GetInvalidFileNameChars();

            var sb = new StringBuilder(name.Length);

            foreach (var ch in name)

                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

            return sb.ToString().Trim();

        }



        private static byte[] SerializeJson<T>(T obj)

        {

            var ser = new DataContractJsonSerializer(typeof(T));

            using (var ms = new MemoryStream())

            {

                ser.WriteObject(ms, obj);

                return ms.ToArray();

            }

        }



        private static T DeserializeJson<T>(byte[] bytes)

        {

            var ser = new DataContractJsonSerializer(typeof(T));

            using (var ms = new MemoryStream(bytes))

            {

                return (T)ser.ReadObject(ms);

            }

        }



        private static List<int> ParseSelection(string input, int max)

        {

            var result = new SortedSet<int>();

            if (string.IsNullOrWhiteSpace(input)) return result.ToList();



            var tokens = input.Replace(';', ',').Replace(' ', ',').Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var token in tokens)

            {

                var trimmed = token.Trim();

                if (trimmed.Contains('-'))

                {

                    var parts = trimmed.Split('-');

                    if (parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b))

                    {

                        if (a > b) (a, b) = (b, a);

                        for (int i = a; i <= b; i++)

                            if (i >= 1 && i <= max) result.Add(i);

                    }

                }

                else if (int.TryParse(trimmed, out var single))

                {

                    if (single >= 1 && single <= max) result.Add(single);

                }

            }

            return result.ToList();

        }



        private static DialogState AnalyzeState(List<Message> lastMessages, string dialogTitle, out GoalState goals)

        {

            goals = new GoalState();

            if (lastMessages == null) return new DialogState { Title = dialogTitle };



            var text = string.Join(" ", lastMessages.Select(m => m.message ?? string.Empty));

            var lower = text.ToLowerInvariant();



            string[] affectionate = { "мила", "дорог", "солныш", "котик", "милая", "лапушк", "зайч", "любим" };

            string[] flirty = { "хочу", "целу", "не могу", "обож", "жарко", "фантаз", "прикосн", "ласкай" };

            string[] intimacy = { "гол", "страст", "ночь", "обним", "постел", "поцел", "обожаю тебя", "нежно" };



            int aff = affectionate.Count(lower.Contains);

            int flr = flirty.Count(lower.Contains);

            int intim = intimacy.Count(lower.Contains);



            int arousal = Clamp((aff * 8) + (flr * 15) + (intim * 10), 0, 100);



            var lastOther = TakeLastCompat(

                lastMessages.Where(m => !IsFromSelf(m)).Select(m => (m.message ?? string.Empty).ToLowerInvariant()),

                20).ToList();



            int heat = 0;

            foreach (var msg in lastOther)

            {

                heat += flirty.Any(msg.Contains) ? 6 : 0;

                heat += affectionate.Any(msg.Contains) ? 3 : 0;

                heat += intimacy.Any(msg.Contains) ? 5 : 0;

                if (msg.IndexOf('?') >= 0) heat += 1;

            }

            int intimacyProb = Clamp(heat * 4, 0, 100);



            goals = new GoalState

            {

                G1_MaleFMaleKnown = lower.Contains("рассказал") || lower.Contains("другая"),

                G2_OtherPartnerInterest = lower.Contains("другую") || lower.Contains("кого-то ещё"),

                G3_WithoutMe = lower.Contains("без тебя") || lower.Contains("самостоятельно"),

                G4_WithGirl = lower.Contains("с девушкой") || lower.Contains("третья"),

                G5_SelfVideo = lower.Contains("видео") || lower.Contains("запись"),

                G6_VoicedFantasies = lower.Contains("фантаз") || lower.Contains("мечтаю")

            };



            return new DialogState

            {

                Title = dialogTitle,

                Arousal = arousal,

                IntimacyProbabilityNext10 = intimacyProb

            };

        }



        private static async Task SaveStateToFavorites(string dialogTitle, DialogState state, GoalState goals)

        {

            try

            {

                var sb = new StringBuilder();

                sb.AppendLine($"[{DateTime.Now:HH:mm}] {dialogTitle}");

                sb.AppendLine($"Arousal={state.Arousal}/100, IntimacyProb={state.IntimacyProbabilityNext10}%");

                sb.AppendLine($"Goals: {DescribeGoalState(goals)}");



                await CallWithThrottle(async () =>

                {

                    await _client.Messages_SendMessage(new InputPeerSelf(), sb.ToString().Trim(), random_id: WTelegram.Helpers.RandomLong());

                }, "Messages_SendMessage[SavedMessages]");

            }

            catch (Exception ex)

            {

                LogToFile("STATE", "Save to favorites failed: " + ex);

            }

        }



        private static string DescribeGoalState(GoalState goals)

        {

            if (goals == null) return "нет данных";

            var flags = new List<string>();

            if (goals.G1_MaleFMaleKnown) flags.Add("знают о других");

            if (goals.G2_OtherPartnerInterest) flags.Add("интерес к другим");

            if (goals.G3_WithoutMe) flags.Add("без меня");

            if (goals.G4_WithGirl) flags.Add("с девушкой");

            if (goals.G5_SelfVideo) flags.Add("видео");

            if (goals.G6_VoicedFantasies) flags.Add("фантазии");

            return flags.Count > 0 ? string.Join(", ", flags) : "нет";

        }



        private static void SaveStateToFile(string dialogTitle, DialogState state, GoalState goals)

        {

            try

            {

                var snapshot = new PersistedState

                {

                    Timestamp = DateTime.UtcNow,

                    Title = dialogTitle,

                    Arousal = state.Arousal,

                    IntimacyProbabilityNext10 = state.IntimacyProbabilityNext10,

                    Goals = goals

                };

                var bytes = SerializeJson(snapshot);

                File.WriteAllBytes(Path.Combine(EXPORT_DIR, "dialog_state.json"), bytes);

            }

            catch (Exception ex)

            {

                LogToFile("STATE", "Save to file failed: " + ex);

            }

        }



        private static DateTime ToUtc(Message m)

        {

            try

            {

                long unix = Convert.ToInt64(m.date);

                if (unix > 0) return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

            }

            catch

            {

            }

            return DateTime.UtcNow;

        }



        private static bool HasStateMeaningfulChange(DialogState a, DialogState b, GoalState ga, GoalState gb)

        {

            if (a == null || b == null) return true;

            if (Math.Abs(a.Arousal - b.Arousal) >= 10) return true;

            if (Math.Abs(a.IntimacyProbabilityNext10 - b.IntimacyProbabilityNext10) >= 10) return true;

            if ((ga?.Equals(gb) ?? (gb == null)) == false) return true;

            return false;

        }



        private static string Escape(string s)

        {

            if (string.IsNullOrEmpty(s)) return string.Empty;

            if (s.Contains(';') || s.Contains('"'))

                return "\"" + s.Replace("\"", "\"\"") + "\"";

            return s;

        }



        // ======================= DTOs =======================

        [DataContract]

        class DialogState

        {

            [DataMember] public string Title { get; set; }

            [DataMember] public int Arousal { get; set; } // 0–100

            [DataMember] public int IntimacyProbabilityNext10 { get; set; } // 0–100

        }



        [DataContract]

        class GoalState : IEquatable<GoalState>

        {

            [DataMember] public bool G1_MaleFMaleKnown { get; set; }

            [DataMember] public bool G2_OtherPartnerInterest { get; set; }

            [DataMember] public bool G3_WithoutMe { get; set; }

            [DataMember] public bool G4_WithGirl { get; set; }

            [DataMember] public bool G5_SelfVideo { get; set; }

            [DataMember] public bool G6_VoicedFantasies { get; set; }

            public bool Equals(GoalState other)

            {

                if (other == null) return false;

                return G1_MaleFMaleKnown == other.G1_MaleFMaleKnown

                    && G2_OtherPartnerInterest == other.G2_OtherPartnerInterest

                    && G3_WithoutMe == other.G3_WithoutMe

                    && G4_WithGirl == other.G4_WithGirl

                    && G5_SelfVideo == other.G5_SelfVideo

                    && G6_VoicedFantasies == other.G6_VoicedFantasies;

            }

        }



        [DataContract]

        class PersistedState

        {

            [DataMember] public DateTime Timestamp { get; set; }

            [DataMember] public string Title { get; set; }

            [DataMember] public int Arousal { get; set; }

            [DataMember] public int IntimacyProbabilityNext10 { get; set; }

            [DataMember] public GoalState Goals { get; set; }

        }



        [DataContract]

        class Persona

        {

            [DataMember] public string Name { get; set; }

            [DataMember] public string SystemPrompt { get; set; }

        }





        [DataContract]

        class PersonaBindingRecord : IEquatable<PersonaBindingRecord>

        {

            [DataMember] public string Key { get; set; }

            [DataMember] public string PersonaName { get; set; }

            [DataMember] public string ExtraInstructions { get; set; }

            [DataMember] public double? Temperature { get; set; }

            [DataMember] public int? MaxTokens { get; set; }

            [DataMember] public double? TypingSpeedWpm { get; set; }

            [DataMember] public double? PauseBetweenMessagesSec { get; set; }



            public bool Equals(PersonaBindingRecord other)

            {

                if (other == null) return false;

                return string.Equals(Key, other.Key)

                    && string.Equals(PersonaName, other.PersonaName)

                    && string.Equals(ExtraInstructions, other.ExtraInstructions)

                    && Temperature == other.Temperature

                    && MaxTokens == other.MaxTokens

                    && TypingSpeedWpm == other.TypingSpeedWpm

                    && PauseBetweenMessagesSec == other.PauseBetweenMessagesSec;

            }



            public override bool Equals(object obj) => Equals(obj as PersonaBindingRecord);



            public override int GetHashCode() => (Key ?? string.Empty).GetHashCode();

        }



        class PersonaRuntimeConfig

        {

            public Persona Persona { get; set; }

            public PersonaBindingRecord Binding { get; set; }

        }

    }



    // ======================= DTOs для ChatCompletion =======================

    [DataContract]

    public class ChatMessage

    {

        [DataMember(Name = "role")] public string role { get; set; }

        [DataMember(Name = "content")] public string content { get; set; }

    }



    [DataContract]

    public class ChatCompletionRequest

    {

        [DataMember(Name = "model")] public string model { get; set; }

        [DataMember(Name = "messages")] public List<ChatMessage> messages { get; set; }

        [DataMember(Name = "temperature")] public double temperature { get; set; }

        [DataMember(Name = "max_tokens")] public int max_tokens { get; set; }

    }



    [DataContract]

    public class ChatCompletionChoice

    {

        [DataMember(Name = "message")] public ChatMessage message { get; set; }

    }



    [DataContract]

    public class ChatCompletionResponse

    {

        [DataMember(Name = "choices")] public List<ChatCompletionChoice> choices { get; set; }

    }

}

