using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EarthquakeIntelligenceMonitor
{
    /// <summary>
    /// Уровни тревоги в зависимости от анализа землетрясений
    /// </summary>
    public enum AlertLevel
    {
        Info,      // Обычная информация
        Watch,     // Следить внимательно
        Warning,   // Предупреждение
        Critical   // Критическая ситуация
    }

    /// <summary>
    /// Модель данных одного события землетрясения
    /// </summary>
    public record EarthquakeEvent(
        string Id,                    // Уникальный идентификатор от USGS
        string Place,                 // Место происшествия (например: "12 km NNW of San Jose, CA")
        double? Magnitude,            // Магнитуда (может быть null)
        double DepthKm,               // Глубина в километрах
        DateTimeOffset OccurredAt,    // Время occurrence в UTC
        double Latitude,              // Широта
        double Longitude              // Долгота
    );

    /// <summary>
    /// Результат анализа землетрясений выбранной стратегией
    /// </summary>
    public record AnalysisResult(
        string StrategyName,          // Название применённой стратегии
        AlertLevel Level,             // Уровень тревоги
        double Score,                 // Нормализованная оценка от 0.0 до 1.0
        string Summary,               // Краткое описание результата
        EarthquakeEvent? PrimaryEvent // Главное событие, на которое опирается анализ
    );

    /// <summary>
    /// Интерфейс стратегии анализа землетрясений (Strategy Pattern)
    /// </summary>
    public interface IEarthquakeStrategy
    {
        /// <summary>
        /// Название стратегии (для отображения пользователю)
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Выполняет анализ списка землетрясений и возвращает результат
        /// </summary>
        AnalysisResult Evaluate(IReadOnlyList<EarthquakeEvent> events);
    }

    /// <summary>
    /// Контекст стратегии — позволяет менять алгоритм анализа "на лету"
    /// </summary>
    public sealed class EarthquakeContext
    {
        private IEarthquakeStrategy? _strategy;

        /// <summary>
        /// Возвращает название текущей активной стратегии
        /// </summary>
        public string CurrentStrategyName => _strategy?.Name ?? "NONE";

        /// <summary>
        /// Устанавливает новую стратегию анализа
        /// </summary>
        public void SetStrategy(IEarthquakeStrategy strategy)
        {
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        }

        /// <summary>
        /// Выполняет анализ событий с помощью текущей стратегии
        /// </summary>
        public AnalysisResult Analyze(IReadOnlyList<EarthquakeEvent> events)
        {
            if (_strategy == null)
                throw new InvalidOperationException("Strategy is not set.");

            return _strategy.Evaluate(events);
        }
    }

    // ====================== КОНКРЕТНЫЕ СТРАТЕГИИ ======================

    /// <summary>
    /// Стратегия анализа по максимальной магнитуде
    /// </summary>
    public sealed class MagnitudeStrategy : IEarthquakeStrategy
    {
        public string Name => "MAGNITUDE";

        /// <summary>
        /// Определяет уровень тревоги по самому сильному землетрясению в списке
        /// </summary>
        public AnalysisResult Evaluate(IReadOnlyList<EarthquakeEvent> events)
        {
            if (events.Count == 0)
            {
                return new AnalysisResult(Name, AlertLevel.Info, 0, "No earthquake data available.", null);
            }

            // Находим землетрясение с наибольшей магнитудой
            var strongest = events
                .OrderByDescending(e => e.Magnitude ?? 0.0)
                .First();

            double mag = strongest.Magnitude ?? 0.0;

            // Определяем уровень тревоги в зависимости от магнитуды
            AlertLevel level =
                mag >= 6.5 ? AlertLevel.Critical :
                mag >= 5.5 ? AlertLevel.Warning :
                mag >= 4.5 ? AlertLevel.Watch :
                AlertLevel.Info;

            double score = Math.Min(mag / 8.0, 1.0);

            string summary = $"Strongest event: M{mag:F1} | {strongest.Place} | depth {strongest.DepthKm:F1} km";

            return new AnalysisResult(Name, level, score, summary, strongest);
        }
    }

    /// <summary>
    /// Стратегия анализа по свежести и количеству недавних событий
    /// </summary>
    public sealed class RecencyStrategy : IEarthquakeStrategy
    {
        public string Name => "RECENCY";

        /// <summary>
        /// Оценивает, насколько "свежие" и частые землетрясения происходят в данный момент
        /// </summary>
        public AnalysisResult Evaluate(IReadOnlyList<EarthquakeEvent> events)
        {
            if (events.Count == 0)
            {
                return new AnalysisResult(Name, AlertLevel.Info, 0, "No earthquake data available.", null);
            }

            var now = DateTimeOffset.UtcNow;

            // Самое новое событие
            var newest = events
                .OrderByDescending(e => e.OccurredAt)
                .First();

            double newestAgeMinutes = Math.Max(0, (now - newest.OccurredAt).TotalMinutes);

            // Сколько событий произошло за последний час
            int recentCount = events.Count(e => (now - e.OccurredAt).TotalMinutes <= 60);

            double score = Math.Min(
                1.0,
                (recentCount / 25.0) + (1.0 / (newestAgeMinutes + 1.0))
            );

            AlertLevel level =
                recentCount >= 20 ? AlertLevel.Critical :
                recentCount >= 10 ? AlertLevel.Warning :
                recentCount >= 4 ? AlertLevel.Watch :
                AlertLevel.Info;

            string summary =
                $"Newest event: {newest.Place} | {newest.OccurredAt:HH:mm:ss} UTC | {newestAgeMinutes:F1} min ago | recent in 60 min: {recentCount}";

            return new AnalysisResult(Name, level, score, summary, newest);
        }
    }

    /// <summary>
    /// Стратегия анализа по кластерам (скоплениям) землетрясений в одном регионе
    /// </summary>
    public sealed class ClusterStrategy : IEarthquakeStrategy
    {
        public string Name => "CLUSTER";

        /// <summary>
        /// Находит регион с наибольшим количеством землетрясений и оценивает его активность
        /// </summary>
        public AnalysisResult Evaluate(IReadOnlyList<EarthquakeEvent> events)
        {
            if (events.Count == 0)
            {
                return new AnalysisResult(Name, AlertLevel.Info, 0, "No earthquake data available.", null);
            }

            var grouped = events
                .GroupBy(e => GetRegion(e.Place))
                .Select(g =>
                {
                    var strongest = g.OrderByDescending(x => x.Magnitude ?? 0.0).First();
                    double avgMag = g.Average(x => x.Magnitude ?? 0.0);
                    return new
                    {
                        Region = g.Key,
                        Count = g.Count(),
                        AvgMagnitude = avgMag,
                        Strongest = strongest
                    };
                })
                .OrderByDescending(x => x.Count)
                .ThenByDescending(x => x.AvgMagnitude)
                .First();

            double score = Math.Min(1.0, (grouped.Count / 15.0) + (grouped.AvgMagnitude / 8.0));

            AlertLevel level =
                grouped.Count >= 10 ? AlertLevel.Critical :
                grouped.Count >= 6 ? AlertLevel.Warning :
                grouped.Count >= 3 ? AlertLevel.Watch :
                AlertLevel.Info;

            string summary =
                $"Hot region: {grouped.Region} | events: {grouped.Count} | avg magnitude: {grouped.AvgMagnitude:F2} | strongest: {grouped.Strongest.Place}";

            return new AnalysisResult(Name, level, score, summary, grouped.Strongest);
        }

        /// <summary>
        /// Пытается извлечь название региона из строки места (например, "California", "Japan")
        /// </summary>
        private static string GetRegion(string place)
        {
            if (string.IsNullOrWhiteSpace(place))
                return "Unknown";

            // Пытаемся взять часть после последней запятой
            int commaIndex = place.LastIndexOf(',');
            if (commaIndex >= 0 && commaIndex < place.Length - 1)
                return place[(commaIndex + 1)..].Trim();

            // Пытаемся взять часть после "of "
            const string marker = " of ";
            int ofIndex = place.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (ofIndex >= 0 && ofIndex + marker.Length < place.Length)
                return place[(ofIndex + marker.Length)..].Trim();

            // Если не получилось — берём последние 1-2 слова
            var parts = place.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 2 ? string.Join(' ', parts.Skip(Math.Max(0, parts.Length - 2))) : place.Trim();
        }
    }

    /// <summary>
    /// Стратегия анализа по глубине очага (мелкофокусные землетрясения опаснее)
    /// </summary>
    public sealed class DepthStrategy : IEarthquakeStrategy
    {
        public string Name => "DEPTH";

        /// <summary>
        /// Оценивает опасность по самому мелкому (shallow) землетрясению
        /// </summary>
        public AnalysisResult Evaluate(IReadOnlyList<EarthquakeEvent> events)
        {
            if (events.Count == 0)
            {
                return new AnalysisResult(Name, AlertLevel.Info, 0, "No earthquake data available.", null);
            }

            // Находим самое мелкое землетрясение (сортируем по возрастанию глубины)
            var shallowest = events
                .OrderBy(e => e.DepthKm)
                .ThenByDescending(e => e.Magnitude ?? 0.0)
                .First();

            double mag = shallowest.Magnitude ?? 0.0;

            AlertLevel level =
                shallowest.DepthKm <= 10 && mag >= 5.5 ? AlertLevel.Critical :
                shallowest.DepthKm <= 20 && mag >= 4.5 ? AlertLevel.Warning :
                shallowest.DepthKm <= 35 ? AlertLevel.Watch :
                AlertLevel.Info;

            double score = Math.Min(1.0, (1.0 / (shallowest.DepthKm + 1.0)) + (mag / 10.0));

            string summary =
                $"Shallowest event: M{mag:F1} | {shallowest.Place} | depth {shallowest.DepthKm:F1} km";

            return new AnalysisResult(Name, level, score, summary, shallowest);
        }
    }

    // ====================== РАБОТА С API ======================

    /// <summary>
    /// Клиент для получения данных о землетрясениях с USGS (United States Geological Survey)
    /// </summary>
    public sealed class UsgsEarthquakeApi
    {
        private static readonly HttpClient Http = new();

        public UsgsEarthquakeApi()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 EarthquakeIntelligenceMonitor");
            Http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }

        /// <summary>
        /// Загружает данные о землетрясениях из публичного USGS GeoJSON фида
        /// </summary>
        /// <param name="feed">Название фида: all_day, all_week, all_month и т.д.</param>
        public async Task<List<EarthquakeEvent>> GetFeedAsync(string feed = "all_day")
        {
            string url = $"https://earthquake.usgs.gov/earthquakes/feed/v1.0/summary/{feed}.geojson";

            try
            {
                using var response = await Http.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    return CreateFallbackFeed();

                await using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);

                return ParseFeed(doc.RootElement);
            }
            catch
            {
                // В случае любой ошибки возвращаем тестовые данные
                return CreateFallbackFeed();
            }
        }

        /// <summary>
        /// Парсит GeoJSON ответ от USGS в список объектов EarthquakeEvent
        /// </summary>
        private static List<EarthquakeEvent> ParseFeed(JsonElement root)
        {
            var result = new List<EarthquakeEvent>();

            if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var feature in features.EnumerateArray())
            {
                if (!feature.TryGetProperty("id", out var idEl)) continue;
                if (!feature.TryGetProperty("properties", out var props)) continue;
                if (!feature.TryGetProperty("geometry", out var geom)) continue;

                string id = idEl.GetString() ?? Guid.NewGuid().ToString("N");
                string place = TryGetString(props, "place") ?? "Unknown place";
                double? magnitude = TryGetNullableDouble(props, "mag");
                long timeMs = TryGetLong(props, "time");

                DateTimeOffset occurredAt = timeMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(timeMs)
                    : DateTimeOffset.UtcNow;

                double latitude = 0, longitude = 0, depth = 0;

                if (geom.TryGetProperty("coordinates", out var coords) &&
                    coords.ValueKind == JsonValueKind.Array &&
                    coords.GetArrayLength() >= 3)
                {
                    longitude = ReadDouble(coords[0]);
                    latitude = ReadDouble(coords[1]);
                    depth = ReadDouble(coords[2]);
                }

                result.Add(new EarthquakeEvent(id, place, magnitude, depth, occurredAt, latitude, longitude));
            }

            // Сортируем по времени (самые новые сверху)
            return result
                .OrderByDescending(e => e.OccurredAt)
                .ToList();
        }

        // Вспомогательные методы парсинга JSON
        private static string? TryGetString(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
                ? prop.GetString()
                : null;

        private static double? TryGetNullableDouble(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number
                ? prop.GetDouble()
                : null;

        private static long TryGetLong(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number
                ? prop.GetInt64()
                : 0;

        private static double ReadDouble(JsonElement element) =>
            element.ValueKind == JsonValueKind.Number ? element.GetDouble() : 0.0;

        /// <summary>
        /// Создаёт тестовые данные на случай, если USGS API недоступен
        /// </summary>
        private static List<EarthquakeEvent> CreateFallbackFeed()
        {
            var now = DateTimeOffset.UtcNow;

            return new List<EarthquakeEvent>
            {
                new("fb-1", "Fallback Alaska", 4.8, 12.0, now.AddMinutes(-3), 61.2, -149.9),
                new("fb-2", "Fallback California", 3.9, 8.0, now.AddMinutes(-12), 36.7, -121.7),
                new("fb-3", "Fallback Japan Trench", 5.6, 24.0, now.AddMinutes(-28), 38.0, 142.0)
            };
        }
    }

    /// <summary>
    /// Основной монитор, который периодически получает данные и анализирует их
    /// </summary>
    public sealed class EarthquakeMonitor
    {
        private readonly UsgsEarthquakeApi _api;
        private readonly EarthquakeContext _context;
        private readonly HashSet<string> _seenIds = new();     // Чтобы не дублировать события
        private readonly List<EarthquakeEvent> _history = new(); // История всех уникальных событий
        private int _cycle = 0;

        public EarthquakeMonitor(UsgsEarthquakeApi api, EarthquakeContext context)
        {
            _api = api;
            _context = context;
        }

        /// <summary>
        /// Основной цикл мониторинга: получает данные → обновляет историю → анализирует → выводит результат
        /// </summary>
        public async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                List<EarthquakeEvent> feed = await _api.GetFeedAsync("all_day");

                int newCount = 0;

                // Добавляем только новые события (по Id)
                foreach (var item in feed)
                {
                    if (_seenIds.Add(item.Id))
                    {
                        _history.Add(item);
                        newCount++;
                    }
                }

                // Ограничиваем размер истории
                if (_history.Count > 200)
                {
                    _history.Sort((a, b) => b.OccurredAt.CompareTo(a.OccurredAt));
                    _history.RemoveRange(200, _history.Count - 200);
                }

                // Актуальный снимок событий (от новых к старым)
                IReadOnlyList<EarthquakeEvent> snapshot = _history
                    .OrderByDescending(e => e.OccurredAt)
                    .ToList();

                // === Вывод информации в консоль ===
                Console.WriteLine();
                Console.WriteLine($"=== CYCLE #{++_cycle} | {DateTime.Now:HH:mm:ss} ===");
                Console.WriteLine($"Strategy: {_context.CurrentStrategyName}");
                Console.WriteLine($"New events this cycle: {newCount}");
                Console.WriteLine($"Cached unique events: {snapshot.Count}");

                // Показываем 5 самых свежих событий
                foreach (var item in snapshot.Take(5))
                {
                    string mag = item.Magnitude.HasValue ? item.Magnitude.Value.ToString("F1") : "?";
                    Console.WriteLine($"- M{mag} | {item.Place} | depth {item.DepthKm:F1} km");
                }

                if (snapshot.Count > 0)
                {
                    var result = _context.Analyze(snapshot);

                    Console.WriteLine();
                    Console.WriteLine("=== STRATEGY RESULT ===");
                    Console.WriteLine($"Level: {result.Level}");
                    Console.WriteLine($"Score: {result.Score:F2}");
                    Console.WriteLine($"Summary: {result.Summary}");
                }

                await Task.Delay(TimeSpan.FromSeconds(15), token);
            }
        }
    }

    /// <summary>
    /// Главный класс приложения — управляет запуском монитора и обработкой команд пользователя
    /// </summary>
    public sealed class AppRunner
    {
        private readonly EarthquakeContext _context;
        private readonly EarthquakeMonitor _monitor;
        private readonly CancellationTokenSource _cts = new();

        public AppRunner()
        {
            var api = new UsgsEarthquakeApi();
            _context = new EarthquakeContext();

            // Стратегия по умолчанию
            _context.SetStrategy(new MagnitudeStrategy());

            _monitor = new EarthquakeMonitor(api, _context);
        }

        /// <summary>
        /// Запускает монитор и параллельно читает команды от пользователя
        /// </summary>
        public async Task RunAsync()
        {
            PrintHeader();

            var monitorTask = _monitor.RunAsync(_cts.Token);
            var inputTask = Task.Run(ReadInputLoop);

            await Task.WhenAny(monitorTask, inputTask);

            _cts.Cancel();
        }

        /// <summary>
        /// Цикл чтения команд пользователя (1,2,3,4,q)
        /// </summary>
        private void ReadInputLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                var input = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (input == null) continue;

                switch (input)
                {
                    case "1":
                        _context.SetStrategy(new MagnitudeStrategy());
                        Console.WriteLine("[Client] Strategy switched to MAGNITUDE");
                        break;

                    case "2":
                        _context.SetStrategy(new RecencyStrategy());
                        Console.WriteLine("[Client] Strategy switched to RECENCY");
                        break;

                    case "3":
                        _context.SetStrategy(new ClusterStrategy());
                        Console.WriteLine("[Client] Strategy switched to CLUSTER");
                        break;

                    case "4":
                        _context.SetStrategy(new DepthStrategy());
                        Console.WriteLine("[Client] Strategy switched to DEPTH");
                        break;

                    case "q":
                        _cts.Cancel();
                        return;
                }
            }
        }

        /// <summary>
        /// Выводит приветственную информацию и список команд
        /// </summary>
        private static void PrintHeader()
        {
            Console.WriteLine("=== EARTHQUAKE INTELLIGENCE MONITOR ===");
            Console.WriteLine("USGS real-time feed + Strategy pattern");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("1 - Magnitude strategy");
            Console.WriteLine("2 - Recency strategy");
            Console.WriteLine("3 - Cluster strategy");
            Console.WriteLine("4 - Depth strategy");
            Console.WriteLine("q - quit");
            Console.WriteLine();
        }
    }

    public static class Program
    {
        public static async Task Main()
        {
            var app = new AppRunner();
            await app.RunAsync();
        }
    }
}