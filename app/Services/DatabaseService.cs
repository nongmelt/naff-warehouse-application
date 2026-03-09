using app.Models;
using Npgsql;
using NpgsqlTypes;
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
                       packing_status, created_at, packed_by, product_lists, platform,
                       updated_at, checked_by, updated_product_lists, checked_at
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
                    PackedBy       = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ProductLists   = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Platform       = reader.IsDBNull(8) ? null : reader.GetString(8),
                    UpdatedAt      = reader.IsDBNull(9)  ? null : reader.GetDateTime(9),
                    CheckedBy             = reader.IsDBNull(10) ? null : reader.GetString(10),
                    UpdatedProductLists   = reader.IsDBNull(11) ? null : reader.GetString(11),
                    CheckedAt             = reader.IsDBNull(12) ? null : reader.GetDateTime(12),
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"DatabaseService.SearchAsync: {ex.Message}");
        }
        return results;
    }

    /// <summary>Returns true if a row with this exact tracking_number exists.</summary>
    public static async Task<bool> ExistsAsTrackingAsync(string barcode)
    {
        var cs = AppSettings.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs)) return false;
        try
        {
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT 1 FROM public.packing_lists WHERE tracking_number = @input LIMIT 1";
            cmd.Parameters.AddWithValue("input", barcode);
            return await cmd.ExecuteScalarAsync() != null;
        }
        catch (Exception ex)
        {
            Logger.Log($"DatabaseService.ExistsAsTrackingAsync: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Updates packing_status, checked_by, updated_at, and updated_product_lists.
    /// Pass checkedBy only for QC Passed; null leaves checked_by as NULL in DB.
    /// </summary>
    public static async Task<bool> UpdatePackingStatusAsync(
        int packingId, string status, string updatedProductLists,
        string? checkedBy = null)
    {
        var cs = AppSettings.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs)) return false;
        try
        {
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE public.packing_lists
                   SET packing_status        = @status,
                       checked_by            = @checked_by,
                       updated_at            = NOW(),
                       checked_at            = NOW(),
                       updated_product_lists = @updated_product_lists
                 WHERE packing_id = @id
                """;
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.Add(new NpgsqlParameter("checked_by", NpgsqlDbType.Text)
                { Value = checkedBy ?? (object)DBNull.Value });
            cmd.Parameters.AddWithValue("id", packingId);
            cmd.Parameters.Add(new NpgsqlParameter("updated_product_lists", NpgsqlDbType.Json)
                { Value = updatedProductLists });
            await cmd.ExecuteNonQueryAsync();
            Logger.Log($"DatabaseService: packing_id {packingId} → status={status}, checked_by={checkedBy ?? "null"}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"DatabaseService.UpdatePackingStatusAsync: {ex.Message}");
            return false;
        }
    }

    /// <summary>Clears QC Hold state: nulls out packing_status, checked_by, updated_product_lists and refreshes updated_at.</summary>
    public static async Task<bool> ResetQcHoldAsync(int packingId)
    {
        var cs = AppSettings.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs)) return false;
        try
        {
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE public.packing_lists
                   SET packing_status        = NULL,
                       checked_by            = NULL,
                       updated_at            = NOW(),
                       checked_at            = NULL,
                       updated_product_lists = NULL
                 WHERE packing_id = @id
                """;
            cmd.Parameters.AddWithValue("id", packingId);
            await cmd.ExecuteNonQueryAsync();
            Logger.Log($"DatabaseService: packing_id {packingId} → QC Hold reset");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"DatabaseService.ResetQcHoldAsync: {ex.Message}");
            return false;
        }
    }
}
