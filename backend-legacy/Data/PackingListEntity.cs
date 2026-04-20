using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace backend.Data;

[Table("packing_lists", Schema = "public")]
public class PackingListEntity
{
    [Key]
    [Column("packing_id")]
    public int PackingId { get; set; }

    [Column("tracking_number")]
    public string TrackingNumber { get; set; } = "";

    [Column("order_number")]
    public string OrderNumber { get; set; } = "";

    [Column("total_items")]
    public int? TotalItems { get; set; }

    [Column("packing_status")]
    public string? PackingStatus { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("packed_by")]
    public string? PackedBy { get; set; }

    [Column("product_lists", TypeName = "json")]
    public string? ProductLists { get; set; }

    [Column("platform")]
    public string? Platform { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    [Column("checked_by")]
    public string? CheckedBy { get; set; }

    [Column("updated_product_lists", TypeName = "json")]
    public string? UpdatedProductLists { get; set; }

    [Column("checked_at")]
    public DateTime? CheckedAt { get; set; }
}
