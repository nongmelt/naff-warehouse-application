using backend.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

var app = builder.Build();

// ── Warm up DB connection pool before accepting requests ──────────────────────
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.OpenConnectionAsync();
        await db.Database.CloseConnectionAsync();
    }
    catch { /* non-fatal — app still starts */ }
}

// ── GET /health ──────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// ── GET /packing-lists?q={tracking or order number} ─────────────────────────
app.MapGet("/packing-lists", async (string q, AppDbContext db) =>
    await db.PackingLists
        .Where(p => p.TrackingNumber == q || p.OrderNumber == q)
        .OrderByDescending(p => p.CreatedAt)
        .ToListAsync());

// ── GET /packing-lists/exists?barcode={barcode} ──────────────────────────────
app.MapGet("/packing-lists/exists", async (string barcode, AppDbContext db) =>
    Results.Ok(new { exists = await db.PackingLists.AnyAsync(p => p.TrackingNumber == barcode) }));

// ── PATCH /packing-lists/{id}/status ────────────────────────────────────────
app.MapPatch("/packing-lists/{id}/status", async (int id, StatusRequest req, AppDbContext db) =>
{
    var row = await db.PackingLists.FindAsync(id);
    if (row is null) return Results.NotFound();

    row.PackingStatus       = req.Status;
    row.CheckedBy           = req.CheckedBy;
    row.UpdatedAt           = DateTime.UtcNow;
    row.CheckedAt           = DateTime.UtcNow;
    row.UpdatedProductLists = req.UpdatedProductLists;
    await db.SaveChangesAsync();
    return Results.Ok();
});

// ── POST /packing-lists/{id}/reset ──────────────────────────────────────────
app.MapPost("/packing-lists/{id}/reset", async (int id, AppDbContext db) =>
{
    var row = await db.PackingLists.FindAsync(id);
    if (row is null) return Results.NotFound();

    row.PackingStatus       = "To be packed";
    row.CheckedBy           = null;
    row.CheckedAt           = null;
    row.UpdatedAt           = DateTime.UtcNow;
    row.UpdatedProductLists = null;
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.Run();

record StatusRequest(string Status, string UpdatedProductLists, string? CheckedBy);
