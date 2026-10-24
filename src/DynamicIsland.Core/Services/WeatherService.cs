using System.Net;
using System.Net.Http;
using System.Text.Json;
using DynamicIsland.Core.Models;

namespace DynamicIsland.Core.Services;

/// <summary>天气获取（Open-Meteo，免 Key）。</summary>
public sealed class WeatherService
{
    private static readonly HttpClient Http = CreateClient();

    private WeatherInfo _cached = WeatherInfo.Empty;
    private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;

    public string CityName { get; set; } = "北京";
    public double Latitude { get; set; } = 39.9042;
    public double Longitude { get; set; } = 116.4074;

    /// <summary>缓存有效期。</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(10);

    public string? LastStatus { get; private set; }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/0.1 (Windows)");
        return client;
    }

    public async Task<WeatherInfo> GetAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _cached.Ok && DateTimeOffset.Now - _lastFetch < CacheDuration)
        {
            return _cached;
        }

        try
        {
            string url = "https://api.open-meteo.com/v1/forecast" +
                         $"?latitude={Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                         $"&longitude={Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                         "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m" +
                         "&daily=weather_code,temperature_2m_max,temperature_2m_min" +
                         "&timezone=auto&forecast_days=4";

            using var response = await Http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(ct);
            var info = Parse(json);
            if (info is not null)
            {
                _cached = info;
                _lastFetch = DateTimeOffset.Now;
                LastStatus = "OK";
                LogService.Info($"天气更新：{info.Description} {info.Temperature:F0}°C");
                return info;
            }

            LastStatus = "解析失败";
        }
        catch (Exception ex)
        {
            LastStatus = $"请求失败：{ex.Message}";
            LogService.Warn("天气获取失败：" + ex.Message);
        }

        return _cached;
    }

    private WeatherInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("current", out var current))
            {
                return null;
            }

            double temperature = GetDouble(current, "temperature_2m");
            double feelsLike = GetDouble(current, "apparent_temperature");
            double humidity = GetDouble(current, "relative_humidity_2m");
            double wind = GetDouble(current, "wind_speed_10m");
            int code = (int)GetDouble(current, "weather_code");

            var daily = new List<DailyForecast>();
            if (root.TryGetProperty("daily", out var dailyNode) &&
                dailyNode.TryGetProperty("time", out var times) &&
                times.ValueKind == JsonValueKind.Array)
            {
                var codes = dailyNode.TryGetProperty("weather_code", out var c) ? c : default;
                var maxes = dailyNode.TryGetProperty("temperature_2m_max", out var mx) ? mx : default;
                var mins = dailyNode.TryGetProperty("temperature_2m_min", out var mn) ? mn : default;

                int count = Math.Min(times.GetArrayLength(), 4);
                for (int i = 0; i < count; i++)
                {
                    string? dateText = times[i].GetString();
                    if (!DateTime.TryParse(dateText, out var date))
                    {
                        continue;
                    }

                    daily.Add(new DailyForecast(
                        date,
                        GetArrayInt(codes, i),
                        GetArrayDouble(maxes, i),
                        GetArrayDouble(mins, i)));
                }
            }

            var (description, icon) = WeatherInfo.DescribeCode(code);

            return new WeatherInfo
            {
                Ok = true,
                LocationName = CityName,
                Temperature = temperature,
                FeelsLike = feelsLike,
                Humidity = (int)Math.Round(humidity),
                WindSpeed = wind,
                WeatherCode = code,
                Description = description,
                Icon = icon,
                Daily = daily,
                UpdatedAt = DateTimeOffset.Now
            };
        }
        catch (Exception ex)
        {
            LogService.Warn("解析天气 JSON 失败：" + ex.Message);
            return null;
        }
    }

    private static double GetDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => double.TryParse(value.GetString(), out double parsed) ? parsed : 0,
            _ => 0
        };
    }

    private static double GetArrayDouble(JsonElement element, int index)
    {
        try
        {
            if (element.ValueKind == JsonValueKind.Array && index < element.GetArrayLength())
            {
                return element[index].GetDouble();
            }
        }
        catch
        {
            // 忽略
        }

        return 0;
    }

    private static int GetArrayInt(JsonElement element, int index) => (int)Math.Round(GetArrayDouble(element, index));
}
