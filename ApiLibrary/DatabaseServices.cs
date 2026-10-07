using ApiLibrary.Models;
using System.Data.SQLite;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ApiLibrary
{
    public class DatabaseServices
    {
        private static readonly string dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LdPosService",
            "PosData.db"
        );

        private static string ConnectionString => $"Data Source={dbPath}";
        public static SQLiteConnection GetConnection()
        {
            return new SQLiteConnection(ConnectionString);
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static void InitializeDatabase()
        {
            string? directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(directory))
            {
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                try
                {
                    DirectoryInfo dirInfo = new DirectoryInfo(directory);
                    DirectorySecurity security = dirInfo.GetAccessControl();
                    SecurityIdentifier users = new SecurityIdentifier(
                        WellKnownSidType.BuiltinUsersSid, null);

                    security.AddAccessRule(new FileSystemAccessRule(
                        users,
                        FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow));

                    dirInfo.SetAccessControl(security);
                    Console.WriteLine("Permissions set successfully.");
                }
                catch (UnauthorizedAccessException ex)
                {
                    Console.WriteLine($"PERMISSION ERROR: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR setting permissions: {ex.Message}");
                }
            }

            // Create the database file if it doesn't exist
            if (!File.Exists(dbPath))
            {
                SQLiteConnection.CreateFile(dbPath);
            }

            using (var connection = GetConnection())
            {
                connection.Open();

                string createUserTableQ = @"
                    CREATE TABLE IF NOT EXISTS User (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        UUID INTEGER NOT NULL UNIQUE,
                        Username TEXT,
                        Email TEXT NOT NULL,
                        FolderPath TEXT,
                        AccessToken TEXT NOT NULL,
                        StoreId INTEGER NOT NULL DEFAULT 0,
                        DeptId INTEGER NOT NULL DEFAULT 0,
                        LoginTime TEXT NOT NULL
                    )";

                string createTransactionsTableQ = @"
                    CREATE TABLE IF NOT EXISTS Transactions (
                        TransId INTEGER NOT NULL UNIQUE,
                        FileName TEXT NOT NULL,
                        TransJson TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL
                    );";

                using (var command = new SQLiteCommand(connection))
                {
                    command.CommandText = createUserTableQ;
                    command.ExecuteNonQuery();

                    command.CommandText = createTransactionsTableQ;
                    command.ExecuteNonQuery();
                }
            }
        }

        public void AddUser(User user)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    connection.Open();
                    string query = @"
                            INSERT INTO User (UUID, Username, Email, FolderPath, AccessToken, StoreId, DeptId, LoginTime)
                            VALUES (@UUID, @Username, @Email, @FolderPath, @AccessToken, @StoreId, @DeptId, @LoginTime)";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@UUID", user.UUID);
                        command.Parameters.AddWithValue("@Username", user.Username ?? "");
                        command.Parameters.AddWithValue("@Email", user.Email ?? "");
                        command.Parameters.AddWithValue("@FolderPath", user.FolderPath ?? "");
                        command.Parameters.AddWithValue("@AccessToken", user.AccessToken ?? "");
                        command.Parameters.AddWithValue("@StoreId", user.StoreId);
                        command.Parameters.AddWithValue("@DeptId", user.DeptId);
                        command.Parameters.AddWithValue("@LoginTime", user.LoginTime ?? "");
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to add user: {ex.Message}");
            }
        }

        public void DeleteUserByUUID(int uuid)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    connection.Open();
                    string query = "DELETE FROM User WHERE UUID = @UUID";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@UUID", uuid);
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to delete user: {ex.Message}");
            }
        }

        public void DeleteAllTransactions()
        {
            try
            {
                using (var connection = GetConnection())
                {
                    connection.Open();
                    string query = "DELETE FROM Transactions";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to delete transactions: {ex.Message}");
            }
        }

        public void UpdateUserFolderPath(int uuid, string? folderPath)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    connection.Open();
                    string query = "UPDATE User SET FolderPath = @FolderPath WHERE UUID = @UUID";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@FolderPath", folderPath ?? "");
                        command.Parameters.AddWithValue("@UUID", uuid);
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to update folder path: {ex.Message}");
            }
        }

        public void AddTransaction(int transId, string? fileName, string? transJson)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    connection.Open();
                    string query = @"
                            INSERT OR IGNORE INTO Transactions (TransId, FileName, TransJson, CreatedAt)
                            VALUES (@TransId, @FileName, @TransJson, @CreatedAt)";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@TransId", transId);
                        command.Parameters.AddWithValue("@FileName", fileName ?? "");
                        command.Parameters.AddWithValue("@TransJson", transJson ?? "");
                        command.Parameters.AddWithValue("@CreatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to add transaction: {ex.Message}");
            }
        }

        public void DeleteTransaction(int transId)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    connection.Open();
                    string query = "DELETE FROM Transactions WHERE TransId = @TransId";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@TransId", transId);
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to delete transaction: {ex.Message}");
            }
        }

        public User? GetLastLoggedInUser()
        {
            try
            {
                using (var connection = GetConnection())
                {
                    connection.Open();
                    string query = "SELECT * FROM User ORDER BY LoginTime DESC LIMIT 1";

                    using (var command = new SQLiteCommand(query, connection))
                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            return new User
                            {
                                Id = reader.GetInt32(0),
                                UUID = reader.GetInt32(1),
                                Username = reader.IsDBNull(2) ? "" : reader.GetString(2),
                                Email = reader.GetString(3),
                                FolderPath = reader.IsDBNull(4) ? null : reader.GetString(4),
                                AccessToken = reader.IsDBNull(5) ? null : reader.GetString(5),
                                StoreId = reader.GetInt32(6),
                                DeptId = reader.GetInt32(7),
                                LoginTime = reader.GetString(8)
                            };
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to load last logged-in user: {ex.Message}");
            }
        }

        public int CountUnprocessedTransactions()
        {
            try
            {
                using (var connection = GetConnection())
                {
                    connection.Open();
                    string query = "SELECT COUNT(*) FROM Transactions";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        return Convert.ToInt32(command.ExecuteScalar());
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to count unprocessed transactions: {ex.Message}");
            }
        }

        public List<Transaction> GetUnprocessedTransactions()
        {
            var transactions = new List<Transaction>();
            try
            {
                using (var connection = GetConnection())
                {
                    connection.Open();
                    string query = "SELECT TransId, FileName, TransJson, CreatedAt FROM Transactions";

                    using (var command = new SQLiteCommand(query, connection))
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            transactions.Add(new Transaction
                            {
                                TransId = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0)),
                                FileName = reader.IsDBNull(1) ? null : reader.GetValue(1)?.ToString(),
                                Json = reader.IsDBNull(2) ? null : reader.GetValue(2)?.ToString(),
                                CreatedAt = reader.IsDBNull(3) ? null : reader.GetValue(3)?.ToString()
                            });
                        }
                    }
                }
                return transactions;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get unprocessed transactions: {ex.Message}");
            }
        }

    }
}