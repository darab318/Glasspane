using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Glasspane.Core;

namespace Glasspane.Widgets.Weather
{
    public sealed class WeatherOptions
    {
        public string Place { get; set; } = "London";
        public double Latitude { get; set; } = 51.5072;
        public double Longitude { get; set; } = -0.1276;
        public bool Fahrenheit { get; set; }
        public bool WindKmh { get; set; }
        public bool ShowHourly { get; set; } = true;
        public bool ShowDaily { get; set; } = true;
    }

    public sealed class Place
    {
        public string Name { get; init; } = "";
        public string Detail { get; init; } = "";
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public override string ToString() => string.IsNullOrEmpty(Detail) ? Name : $"{Name}, {Detail}";
    }

    public sealed class HourForecast
    {
        public DateTime Time;
        public double Temperature;
        public int Code;
        public bool IsDay;
        public int RainChance;
    }

    public sealed class DayForecast
    {
        public DateTime Date;
        public int Code;
        public double High, Low;
        public int RainChance;
    }

    public sealed class Forecast
    {
        public DateTime FetchedAt;
        public double Temperature, FeelsLike, Wind;
        public int Humidity, Code;
        public bool IsDay;
        public DateTime? Sunrise, Sunset;
        public List<HourForecast> Hours = new();
        public List<DayForecast> Days = new();
    }

    /// <summary>
    /// Weather from Open-Meteo (free, no account, no tracking). One small download per update;
    /// the last forecast is kept on disk so the widget shows something straight away at start-up.
    /// </summary>
    public sealed class WeatherService
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        private readonly string _folder;

        public WeatherService(string folder)
        {
            _folder = folder;
            Directory.CreateDirectory(folder);
            Options = LoadOptions();
        }

        public WeatherOptions Options { get; }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Glasspane/0.1 (desktop weather widget)");
            return client;
        }

        // ---------------------------------------------------------------- options

        private WeatherOptions LoadOptions()
        {
            try
            {
                string path = Path.Combine(_folder, "options.json");
                if (File.Exists(path)) return JsonSerializer.Deserialize<WeatherOptions>(File.ReadAllText(path)) ?? new WeatherOptions();
            }
            catch (Exception ex)
            {
                Log.Write("Weather options could not be read: " + ex.Message);
            }
            return new WeatherOptions();
        }

        public void SaveOptions()
        {
            try { File.WriteAllText(Path.Combine(_folder, "options.json"), JsonSerializer.Serialize(Options, Json)); }
            catch (Exception ex) { Log.Write("Weather options could not be saved: " + ex.Message); }
        }

        // ---------------------------------------------------------------- forecast

        private string CacheFile => Path.Combine(_folder, "last-forecast.json");

        /// <summary>The forecast saved last time, if it's for the current place and settings.</summary>
        public Forecast? LoadCached()
        {
            try
            {
                if (!File.Exists(CacheFile)) return null;
                var lines = File.ReadAllText(CacheFile).Split('\n', 2);
                if (lines.Length < 2 || lines[0].Trim() != CacheKey()) return null;
                var forecast = Parse(lines[1]);
                forecast.FetchedAt = File.GetLastWriteTime(CacheFile);
                return forecast;
            }
            catch
            {
                return null;
            }
        }

        private string CacheKey() =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.####},{1:0.####},{2},{3}", Options.Latitude, Options.Longitude, Options.Fahrenheit, Options.WindKmh);

        public async Task<Forecast> FetchAsync()
        {
            string url = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0:0.####}&longitude={1:0.####}" +
                "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,is_day,wind_speed_10m" +
                "&hourly=temperature_2m,weather_code,precipitation_probability,is_day" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset" +
                "&timezone=auto&forecast_days=6&forecast_hours=24" +
                "&temperature_unit={2}&wind_speed_unit={3}",
                Options.Latitude, Options.Longitude,
                Options.Fahrenheit ? "fahrenheit" : "celsius",
                Options.WindKmh ? "kmh" : "mph");

            string body = await Http.GetStringAsync(url);
            var forecast = Parse(body);
            forecast.FetchedAt = DateTime.Now;
            try { File.WriteAllText(CacheFile, CacheKey() + "\n" + body); } catch { /* cache is optional */ }
            return forecast;
        }

        private static Forecast Parse(string body)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var f = new Forecast();

            var current = root.GetProperty("current");
            f.Temperature = current.GetProperty("temperature_2m").GetDouble();
            f.FeelsLike = current.GetProperty("apparent_temperature").GetDouble();
            f.Humidity = (int)Math.Round(current.GetProperty("relative_humidity_2m").GetDouble());
            f.Code = current.GetProperty("weather_code").GetInt32();
            f.IsDay = current.GetProperty("is_day").GetInt32() == 1;
            f.Wind = current.GetProperty("wind_speed_10m").GetDouble();

            var hourly = root.GetProperty("hourly");
            var times = hourly.GetProperty("time").EnumerateArray().Select(t => DateTime.Parse(t.GetString()!, CultureInfo.InvariantCulture)).ToList();
            var temps = Doubles(hourly.GetProperty("temperature_2m"));
            var codes = Ints(hourly.GetProperty("weather_code"));
            var rain = Ints(hourly.GetProperty("precipitation_probability"));
            var day = Ints(hourly.GetProperty("is_day"));
            for (int i = 0; i < times.Count; i++)
                f.Hours.Add(new HourForecast { Time = times[i], Temperature = temps[i], Code = codes[i], RainChance = rain[i], IsDay = day[i] == 1 });

            var daily = root.GetProperty("daily");
            var dates = daily.GetProperty("time").EnumerateArray().Select(t => DateTime.Parse(t.GetString()!, CultureInfo.InvariantCulture)).ToList();
            var dCodes = Ints(daily.GetProperty("weather_code"));
            var highs = Doubles(daily.GetProperty("temperature_2m_max"));
            var lows = Doubles(daily.GetProperty("temperature_2m_min"));
            var dRain = Ints(daily.GetProperty("precipitation_probability_max"));
            var sunrise = daily.GetProperty("sunrise").EnumerateArray().Select(t => t.GetString()).ToList();
            var sunset = daily.GetProperty("sunset").EnumerateArray().Select(t => t.GetString()).ToList();
            for (int i = 0; i < dates.Count; i++)
                f.Days.Add(new DayForecast { Date = dates[i], Code = dCodes[i], High = highs[i], Low = lows[i], RainChance = dRain[i] });
            if (sunrise.Count > 0 && sunrise[0] != null) f.Sunrise = DateTime.Parse(sunrise[0]!, CultureInfo.InvariantCulture);
            if (sunset.Count > 0 && sunset[0] != null) f.Sunset = DateTime.Parse(sunset[0]!, CultureInfo.InvariantCulture);
            return f;
        }

        // Values can be null in the feed (e.g. no rain chance for a past hour)
        private static List<double> Doubles(JsonElement array) =>
            array.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0).ToList();

        private static List<int> Ints(JsonElement array) =>
            array.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? (int)Math.Round(v.GetDouble()) : 0).ToList();

        // ---------------------------------------------------------------- place search

        public static async Task<List<Place>> SearchAsync(string query)
        {
            string url = "https://geocoding-api.open-meteo.com/v1/search?count=6&language=en&format=json&name=" + Uri.EscapeDataString(query.Trim());
            string body = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(body);
            var list = new List<Place>();
            if (!doc.RootElement.TryGetProperty("results", out var results)) return list;
            foreach (var r in results.EnumerateArray())
            {
                string Get(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                string detail = string.Join(", ", new[] { Get("admin1"), Get("country") }.Where(s => s.Length > 0));
                list.Add(new Place
                {
                    Name = Get("name"),
                    Detail = detail,
                    Latitude = r.GetProperty("latitude").GetDouble(),
                    Longitude = r.GetProperty("longitude").GetDouble()
                });
            }
            return list;
        }

        // ---------------------------------------------------------------- weather codes

        /// <summary>Describes a WMO weather code, e.g. 61 → "Light rain".</summary>
        public static string Describe(int code) => code switch
        {
            0 => "Clear",
            1 => "Mostly clear",
            2 => "Partly cloudy",
            3 => "Cloudy",
            45 or 48 => "Fog",
            51 or 53 or 55 => "Drizzle",
            56 or 57 => "Freezing drizzle",
            61 => "Light rain",
            63 => "Rain",
            65 => "Heavy rain",
            66 or 67 => "Freezing rain",
            71 => "Light snow",
            73 => "Snow",
            75 => "Heavy snow",
            77 => "Snow grains",
            80 => "Light showers",
            81 => "Showers",
            82 => "Heavy showers",
            85 or 86 => "Snow showers",
            95 => "Thunderstorm",
            96 or 99 => "Thunderstorm with hail",
            _ => "—"
        };

        /// <summary>A symbol for the weather (drawn in the Segoe UI Symbol font).</summary>
        public static string Symbol(int code, bool isDay) => code switch
        {
            0 or 1 => isDay ? "☀" : "☾",       // ☀ / ☾
            2 => isDay ? "⛅" : "☁",            // ⛅ / ☁
            3 => "☁",                               // ☁
            45 or 48 => "≈",                        // ≈ fog
            51 or 53 or 55 or 56 or 57 => "☂",      // ☂ drizzle
            61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "⛆", // ⛆ rain
            71 or 73 or 75 or 77 or 85 or 86 => "❄", // ❄
            95 or 96 or 99 => "⛈",                  // ⛈
            _ => "☁"
        };
    }
}
