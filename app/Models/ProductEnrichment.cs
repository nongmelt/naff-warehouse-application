using System.Text.Json.Serialization;

namespace app.Models;

public class ProductEnrichment
{
    [JsonPropertyName("id")]           public int Id              { get; set; }
    [JsonPropertyName("sellerSku")]    public string SellerSku    { get; set; } = "";
    [JsonPropertyName("categoryName")] public string? CategoryName { get; set; }
    [JsonPropertyName("categoryId")]   public int? CategoryId     { get; set; }
    [JsonPropertyName("imagePath")]    public string? ImagePath    { get; set; }
    [JsonPropertyName("qcNotes")]      public string? QcNotes      { get; set; }
    [JsonPropertyName("brand")]        public string? Brand        { get; set; }
    [JsonPropertyName("productType")]  public string ProductType   { get; set; } = "single";
    [JsonPropertyName("allSkus")]      public List<string> AllSkus  { get; set; } = [];
    [JsonPropertyName("components")]   public List<EnrichmentComponent> Components { get; set; } = [];
}

public class EnrichmentComponent
{
    [JsonPropertyName("componentProductId")] public int ComponentProductId { get; set; }
    [JsonPropertyName("sellerSku")]          public string SellerSku       { get; set; } = "";
    [JsonPropertyName("quantity")]           public int Quantity            { get; set; }
    [JsonPropertyName("productName")]        public string ProductName     { get; set; } = "";
    [JsonPropertyName("productVariation")]   public string? ProductVariation { get; set; }
    [JsonPropertyName("imagePath")]          public string? ImagePath       { get; set; }
}
