namespace ApiLibrary.Models
{
    public class User
    {
        public int Id { get; set; }
        public int UUID { get; set; }
        public string? Email { get; set; }
        public string? Username { get; set; }
        public string? FolderPath { get; set; }
        public string? AccessToken { get; set; }
        public int StoreId { get; set; }
        public int DeptId { get; set; }
        public string? LoginTime { get; set; }
    }
}
