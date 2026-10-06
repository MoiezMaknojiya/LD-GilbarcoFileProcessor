namespace ApiLibrary.Models
{
    public class Transaction
    {
        public int TransId { get; set; }
        public string? FileName { get; set; }
        public string? Json { get; set; }
        public string? CreatedAt { get; set; }
        public int IsProcessed { get; set; } = 0;
    }
}
