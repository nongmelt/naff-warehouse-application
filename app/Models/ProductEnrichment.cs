using System.Text.Json.Serialization;

namespace app.Models;

public class ProductEnrichment
{
    [JsonPropertyName("sellerSku")]    public string SellerSku    { get; set; } = "";
    [JsonPropertyName("categoryName")] public string? CategoryName { get; set; }
    [JsonPropertyName("categoryId")]   public int? CategoryId     { get; set; }
    [JsonPropertyName("imagePath")]    public string? ImagePath    { get; set; }
    [JsonPropertyName("qcNotes")]      public string? QcNotes      { get; set; }
    [JsonPropertyName("brand")]        public string? Brand        { get; set; }
}
