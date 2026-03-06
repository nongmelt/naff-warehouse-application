using app.Models;
using Npgsql;
using System.Runtime.Versioning;

namespace app.Services;

[SupportedOSPlatform("windows")]
public static class DatabaseService
{
    public static async Task<(bool Success, string? Error)> TestConnectionAsync()
    {
        var cs = AppSettings.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs))
            return (false, "No connection details configured.");
        try
        {
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            return (true, null);
        }
        catch (Exception ex)
        {
            Logger.Log($"DatabaseService.TestConnectionAsync: {ex.Message}");
            return (false, ex.Message);
        }
    }

    public static async Task<List<PackingList>> SearchAsync(string input)
    {
        var cs = AppSettings.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs)) return [];

        var results = new List<PackingList>();
        try
        {
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT packing_id, tracking_number, order_number, total_items,
                       packing_status, created_at, packing_station, product_lists, platform
                FROM public.packing_lists
                WHERE tracking_number = @input OR order_number = @input
                ORDER BY created_at DESC
                """;
            cmd.Parameters.AddWithValue("input", input);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new PackingList
                {
                    PackingId      = reader.GetInt32(0),
                    TrackingNumber = reader.GetString(1),
                    OrderNumber    = reader.GetString(2),
                    TotalItems     = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    PackingStatus  = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedAt      = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                    PackingStation = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ProductLists   = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Platform       = reader.IsDBNull(8) ? null : reader.GetString(8),
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"DatabaseService.SearchAsync: {ex.Message}");
        }
        return results;
    }
}
