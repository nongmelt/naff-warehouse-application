using System.Text.Json.Serialization;

namespace app.Models;

public class DailySummary
{
    [JsonPropertyName("total")]               public int Total    { get; set; }
    [JsonPropertyName("qcPassed")]            public int QcPassed { get; set; }
    [JsonPropertyName("packed")]              public int Packed   { get; set; }
    [JsonPropertyName("qcHold")]              public int QcHold   { get; set; }
    [JsonPropertyName("waiting")]             public int Waiting  { get; set; }
    [JsonPropertyName("byPlatform")]          public Dictionary<string, int> ByPlatform        { get; set; } = [];
    [JsonPropertyName("byPlatformPassed")]    public Dictionary<string, int> ByPlatformPassed  { get; set; } = [];
    [JsonPropertyName("byPlatformPacked")]    public Dictionary<string, int> ByPlatformPacked  { get; set; } = [];
    [JsonPropertyName("byPlatformHold")]      public Dictionary<string, int> ByPlatformHold    { get; set; } = [];
    [JsonPropertyName("byPlatformWaiting")]   public Dictionary<string, int> ByPlatformWaiting { get; set; } = [];
}
