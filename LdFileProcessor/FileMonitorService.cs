using ApiLibrary;
using ApiLibrary.Models;
using ApiLibrary.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Xml.Linq;

namespace LdFileProcessor
{
    public class FileMonitorService : BackgroundService
    {
        // SERVICES & UTILITIES
        private readonly ILogger<FileMonitorService> _logger;           // For logging errors and info
        private readonly DatabaseServices _dbHelper;                    // For database operations
        private readonly ApiServices _apiService;                       // For API calls (upload, internet check)
        private readonly XmlJsonConverter _xmlJsonConverter;            // Converts XML to JSON
        private readonly FileUtilities _fileUtilities;                  // File helper methods
        private FileSystemWatcher? _watcher;                           // Watches folder for new files

        // TEMP FOLDER PATH
        // Location where files are copied before processing (prevents locks on network files)
        // Example: C:\ProgramData\LdPosService\TempFiles
        private readonly string _tempFolderPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LdPosService",
            "TempFiles"
        );

        // CURRENT STATE
        private string _folderPath = "";        // Folder path to monitor (from database)
        private User? _currentUser;             // Current logged-in user info (contains DeptId, StoreId, etc.)

        private volatile bool _isUploading = false;                    // Flag: Is upload in progress? [volatile: accessed from multiple threads]
        private CancellationTokenSource? _watcherRestartCts;           // Cancels stale watcher restart tasks on new errors

        public FileMonitorService(ILogger<FileMonitorService> logger, XmlJsonConverter xmlJsonConverter, FileUtilities fileUtilities, ApiServices apiService)
        {
            _logger = logger;
            _dbHelper = new DatabaseServices();
            _apiService = apiService;
            _xmlJsonConverter = xmlJsonConverter;
            _fileUtilities = fileUtilities;
            _watcher = null;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("======================================== SERVICE START ========================================");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("File Monitor Service starting at: {time}.\n", DateTimeOffset.Now);

                    // Create temp folder if it doesn't exist
                    if (!Directory.Exists(_tempFolderPath))
                    {
                        Directory.CreateDirectory(_tempFolderPath);
                        _logger.LogInformation("Temp folder created: {path}.\n", _tempFolderPath);
                    }

                    // Create database tables if they don't exist
                    DatabaseServices.InitializeDatabase();

                    // Inner loop - keeps running until service is stopped
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        try
                        {
                            // Get the folder path to monitor from database
                            await GetFolderPathFromDatabaseAsync();

                            // If we have a valid folder path and can access it
                            if (!string.IsNullOrEmpty(_folderPath) && IsPathAccessible(_folderPath))
                            {
                                _logger.LogInformation("Folder path found and accessible: {path}. Starting file monitoring.\n", _folderPath);

                                // Start watching the folder for new files
                                SetupFileWatcher();

                                // Process any existing files in the folder
                                await CopyExistingFilesAsync();

                                // Keep monitoring and refreshing folder path every 30 seconds
                                while (!stoppingToken.IsCancellationRequested)
                                {
                                    //await Task.Delay(30000, stoppingToken);  // Wait 30 seconds
                                    //await RefreshFolderPathAsync();           // Check if folder path changed
                                    //// If folder path was cleared, stop monitoring
                                    //if (string.IsNullOrEmpty(_folderPath))
                                    //{
                                    //    _logger.LogWarning("Folder path cleared. Stopping watcher and resuming path search.\n");
                                    //    _watcher?.Dispose();
                                    //    break;
                                    //}

                                    await Task.Delay(Timeout.Infinite, stoppingToken);
                                }
                            }
                            else
                            {
                                // Folder path not found or not accessible - keep trying every 30 seconds
                                if (!string.IsNullOrEmpty(_folderPath))
                                {
                                    _logger.LogWarning("Folder path found but not accessible: {path}. Service will keep trying every 30 seconds.\n", _folderPath);
                                }
                                else
                                {
                                    _logger.LogWarning("No folder path found. Service will keep trying every 30 seconds.\n");
                                }
                                await Task.Delay(30000, stoppingToken);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.LogInformation("File Monitor Service cancellation requested.\n\n");
                            return;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error in File Monitor Service execution loop.\n\n");
                            await Task.Delay(5000, stoppingToken);  // Wait 5 seconds before retrying
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("File Monitor Service cancellation requested (outer).\n\n");
                    return;
                }
                catch (Exception ex)
                {
                    // Fatal error - service will restart after 10 seconds
                    _logger.LogError(ex, "Fatal error in File Monitor Service. Service will restart in 10 seconds.\n\n");

                    try
                    {
                        await Task.Delay(10000, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation("Service restart cancelled.\n\n");
                        return;
                    }
                }
            }
        }

        private Task GetFolderPathFromDatabaseAsync()
        {
            try
            {
                // Get the last user who logged in
                User? lastUser = _dbHelper.GetLastLoggedInUser();

                if (lastUser != null && !string.IsNullOrEmpty(lastUser.FolderPath))
                {
                    // We have a user with a folder path - save it
                    _currentUser = lastUser;
                    _folderPath = lastUser.FolderPath;

                    // Log different message for network (UNC) vs local paths
                    if (IsUncPath(_folderPath))
                    {
                        _logger.LogInformation("UNC network folder path retrieved from database: {path}.\n", _folderPath);
                    }
                    else
                    {
                        _logger.LogInformation("Local folder path retrieved from database: {path}.\n", _folderPath);
                    }
                }
                else
                {
                    // No user or folder path found
                    _logger.LogWarning("No user or folder path found in database.\n");
                    _currentUser = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving folder path from database.\n\n");
                // FIX #6: Clear stale state on DB error to avoid using an outdated path
                _currentUser = null;
                _folderPath = "";
            }

            return Task.CompletedTask;
        }
      
        private void SetupFileWatcher()
        {
            try
            {
                // Validate folder path
                if (string.IsNullOrEmpty(_folderPath))
                {
                    _logger.LogError("Folder path is invalid or does not exist: {path}.\n", _folderPath);
                    return;
                }

                if (!IsPathAccessible(_folderPath))
                {
                    _logger.LogError("Folder path is not accessible: {path}.\n", _folderPath);
                    return;
                }

                // Dispose old watcher if exists
                _watcher?.Dispose();

                // Create new file watcher
                _watcher = new FileSystemWatcher(_folderPath)
                {
                    Filter = "*.xml",                                                          // Only watch XML files
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,  // Watch for new files
                    EnableRaisingEvents = true,                                                // Start watching immediately
                    InternalBufferSize = 65536                                                 // 64KB buffer (handles multiple files)
                };

                // Register event handlers
                _watcher.Created += OnFileCreated;      // Called when new file appears
                _watcher.Error += OnWatcherError;       // Called when error occurs (e.g., network disconnect)

                // Log different message for network vs local paths
                if (IsUncPath(_folderPath))
                {
                    _logger.LogInformation("File watcher started for UNC network path: {path}.\n", _folderPath);
                }
                else
                {
                    _logger.LogInformation("File watcher started for local path: {path}.\n", _folderPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting up file watcher.\n\n");
            }
        }

        private async void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            // FIX #4: Capture path immediately — watcher may be recycled during async gaps
            string fullPath = e.FullPath;

            try
            {
                _logger.LogInformation("======================================== NEW FILE DETECTED ========================================");
                _logger.LogInformation("File detected: {file}.\n", fullPath);

                // Wait until the file is released by the POS system (retry up to 10 times, 500ms apart = 5s max)
                int retries = 0;
                while (_fileUtilities.IsFileLocked(fullPath) && retries < 50)
                {
                    _logger.LogWarning("File is still in use, retrying in 500ms ({retry}/10): {file}.\n", retries + 1, fullPath);
                    await Task.Delay(700);
                    retries++;
                }

                if (_fileUtilities.IsFileLocked(fullPath))
                {
                    _logger.LogError("File is still locked after 10 retries. Skipping: {file}.\n", fullPath);
                    return;
                }

                // FIX #1: Check file still exists before copying — POS system may have deleted it
                if (!File.Exists(fullPath))
                {
                    _logger.LogWarning("File no longer exists before copy (may have been moved or deleted): {file}.\n", fullPath);
                    return;
                }

                // Copy file to temp folder (safe location for processing)
                string fileName = Path.GetFileName(fullPath);
                string tempFilePath = Path.Combine(_tempFolderPath, fileName);

                File.Copy(fullPath, tempFilePath, true);  // Overwrite if exists
                _logger.LogInformation("File copied to temp: {path}.\n", tempFilePath);

                // Process the file
                await ProcessFileAsync(tempFilePath);

                _logger.LogInformation("======================================== FILE DETECTION END ========================================\n");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing file: {file}.\n\n", fullPath);
                _logger.LogInformation("======================================== FILE DETECTION END ========================================\n");
            }
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            Exception? ex = e.GetException();

            // Win32 error 64 = "The specified network name is no longer available"
            // This is a normal transient condition for UNC/network paths — log as Warning, not Error
            if (ex is System.ComponentModel.Win32Exception w32ex && w32ex.NativeErrorCode == 64)
            {
                _logger.LogWarning("Network path temporarily unavailable (Win32 error 64). " +
                    "Will retry every 30 seconds until it comes back: {path}.\n", _folderPath);
            }
            else
            {
                _logger.LogError(ex, "File watcher error. This may occur if network path becomes unavailable.\n");
            }

            // Cancel any previous restart task that is still waiting
            _watcherRestartCts?.Cancel();
            _watcherRestartCts?.Dispose();
            _watcherRestartCts = new CancellationTokenSource();
            var token = _watcherRestartCts.Token;

            // Retry loop — keeps trying every 30 seconds until path is accessible again
            // FIX #2: Wrapped entire Task.Run body in try/catch so exceptions don't silently kill the recovery loop
            Task.Run(async () =>
            {
                try
                {
                    int attempt = 0;

                    while (!token.IsCancellationRequested)
                    {
                        attempt++;

                        try
                        {
                            await Task.Delay(30000, token);
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.LogInformation("Watcher restart cancelled (superseded by newer error event).\n");
                            return;
                        }

                        if (string.IsNullOrEmpty(_folderPath))
                        {
                            _logger.LogWarning("Folder path is empty. Stopping watcher restart attempts.\n");
                            return;
                        }

                        if (IsPathAccessible(_folderPath))
                        {
                            _logger.LogInformation(
                                "Network path is accessible again (attempt {attempt}). Restarting file watcher.\n", attempt);
                            SetupFileWatcher();

                            // Process any files that arrived during the outage
                            _logger.LogInformation("Checking for files that arrived during network outage.\n");
                            await CopyExistingFilesAsync();

                            return;  // Success — exit retry loop
                        }

                        _logger.LogWarning(
                            "Network path still unavailable (attempt {attempt}): {path}. Retrying in 30 seconds.\n",
                            attempt, _folderPath);
                    }
                }
                catch (Exception innerEx)
                {
                    // Without this catch, any exception here would silently stop recovery with no log entry
                    _logger.LogError(innerEx, "Unhandled error in watcher restart loop. Watcher will not auto-recover.\n");
                }
            }, token);
        }

        private async Task CopyExistingFilesAsync()
        {
            try
            {
                // Check if folder is accessible
                if (!IsPathAccessible(_folderPath))
                {
                    _logger.LogWarning("Folder not found: {path}.\n", _folderPath);
                    return;
                }

                _logger.LogInformation("======================================== COPY EXISTING FILES START ========================================");

                // Get all XML files in folder
                var xmlFiles = Directory.GetFiles(_folderPath, "*.xml");
                _logger.LogInformation("Found {count} existing XML files.\n", xmlFiles.Length);

                // Step 1: Copy all files to temp folder
                var copiedFiles = new List<string>();

                foreach (var file in xmlFiles)
                {
                    try
                    {
                        string fileName = Path.GetFileName(file);
                        string tempFilePath = Path.Combine(_tempFolderPath, fileName);

                        // FIX #5: Always overwrite — a pre-existing temp file may be corrupt/incomplete from a previous crash
                        File.Copy(file, tempFilePath, true);
                        _logger.LogInformation("Copied existing file: {file}.\n", fileName);
                        copiedFiles.Add(tempFilePath);
                    }
                    catch (IOException ioEx)
                    {
                        _logger.LogError(ioEx, "IO error copying file from network: {file}.\n\n", file);
                    }
                    catch (UnauthorizedAccessException uaEx)
                    {
                        _logger.LogError(uaEx, "Access denied to network file: {file}.\n\n", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error copying file: {file}.\n\n", file);
                    }
                }

                _logger.LogInformation("======================================== PROCESS EXISTING FILES START ========================================");
                _logger.LogInformation("Starting to process {count} copied files.\n", copiedFiles.Count);

                // Step 2: Process all copied files
                foreach (var tempFilePath in copiedFiles)
                {
                    try
                    {
                        await ProcessFileAsync(tempFilePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing copied file: {file}.\n\n", tempFilePath);
                    }
                }

                _logger.LogInformation("======================================== PROCESS EXISTING FILES END ========================================");
                _logger.LogInformation("Finished processing all existing files.\n\n");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CopyExistingFilesAsync.\n\n");
            }
        }

        private async Task ProcessFileAsync(string filePath)
        {
            try
            {
                _logger.LogInformation("======================================== FILE PROCESS START ========================================");
                _logger.LogInformation("Processing file: {path}.\n", filePath);

                // Step 1: Check if file exists
                if (!File.Exists(filePath))
                {
                    _logger.LogWarning("File does not exist: {path}.\n", filePath);
                    _logger.LogInformation("======================================== FILE PROCESS END ========================================\n");
                    return;
                }

                // Step 2: Read XML content
                string xmlContent = File.ReadAllText(filePath);

                // Step 3: Validate file is not empty
                if (string.IsNullOrWhiteSpace(xmlContent))
                {
                    _logger.LogWarning("File is empty: {path}.\n", filePath);
                    _logger.LogInformation("======================================== FILE PROCESS END ========================================\n");
                    return;
                }

                // Step 4: Check if we have a logged-in user
                if (_currentUser == null)
                {
                    _logger.LogWarning("No current user found. Skipping transaction processing.\n");
                    _fileUtilities.DeleteFile(filePath, msg => _logger.LogWarning(msg), msg => _logger.LogError(msg), msg => _logger.LogInformation(msg));
                    _logger.LogInformation("======================================== FILE PROCESS END ========================================\n");
                    return;
                }

                // Get user's department ID (used to filter transactions)
                int userDeptId = _currentUser.DeptId;
                string fileName = Path.GetFileName(filePath);

                // Step 5: Parse XML (validate it's proper XML format)
                XDocument xDoc;
                try
                {
                    xDoc = XDocument.Parse(xmlContent);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error parsing XML file: {fileName}.\n\n", fileName);
                    _fileUtilities.DeleteFile(filePath, msg => _logger.LogWarning(msg), msg => _logger.LogError(msg), msg => _logger.LogInformation(msg));
                    _logger.LogInformation("======================================== FILE PROCESS END ========================================\n");
                    return;
                }

                // Step 6: Check if XML contains items matching user's department
                // (Only process transactions for this department)
                bool hasMatchingDeptId = CheckMerchandiseCodesInXml(xDoc, userDeptId, fileName);

                if (!hasMatchingDeptId)
                {
                    _logger.LogInformation("File {fileName} does not contain MerchandiseCode matching DeptId {deptId}.\n", fileName, userDeptId);
                    _fileUtilities.DeleteFile(filePath, msg => _logger.LogWarning(msg), msg => _logger.LogError(msg), msg => _logger.LogInformation(msg));
                    _logger.LogInformation("======================================== FILE PROCESS END ========================================\n");
                    return;
                }

                // Step 7: Convert XML to JSON (API requires JSON format)
                string jsonContent = _xmlJsonConverter.ConvertXmlToJson(
                   xDoc,
                   msg => _logger.LogWarning(msg),
                   msg => _logger.LogError(msg),
                   msg => _logger.LogInformation(msg)
                );

                // Step 8: Validate JSON conversion succeeded
                if (string.IsNullOrWhiteSpace(jsonContent) || jsonContent == "{}")
                {
                    _logger.LogWarning("Failed to convert XML to JSON for file: {file}.\n", fileName);
                    _fileUtilities.DeleteFile(filePath, msg => _logger.LogWarning(msg), msg => _logger.LogError(msg), msg => _logger.LogInformation(msg));
                    _logger.LogInformation("======================================== FILE PROCESS END ========================================\n");
                    return;
                }

                // Step 9: Extract transaction ID from JSON
                int transactionId = ExtractTransactionId(jsonContent, fileName);
                if (transactionId == 0)
                {
                    // Transaction ID not found or invalid - delete file and skip
                    _fileUtilities.DeleteFile(filePath, msg => _logger.LogWarning(msg), msg => _logger.LogError(msg), msg => _logger.LogInformation(msg));
                    _logger.LogInformation("======================================== FILE PROCESS END ========================================\n");
                    return;
                }

                // Step 10: Save transaction to local database
                // IMPORTANT: This ensures data is never lost, even if internet is down
                _dbHelper.AddTransaction(transactionId, fileName, jsonContent);
                _logger.LogInformation("File processed and saved to database: {fileName}.\n", fileName);

                // Step 11: Delete temp file (data is safely in database now)
                _fileUtilities.DeleteFile(filePath, msg => _logger.LogWarning(msg), msg => _logger.LogError(msg), msg => _logger.LogInformation(msg));

                // Step 12: Try to upload immediately
                await UploadUnprocessedTransactionsAsync(_currentUser);

                _logger.LogInformation("======================================== FILE PROCESS END ========================================\n");
            }
            catch (Exception ex)
            {
                // Catch any unexpected errors - log and delete temp file
                _logger.LogError(ex, "Error processing file: {filePath}.\n\n", filePath);
                _fileUtilities.DeleteFile(filePath, msg => _logger.LogWarning(msg), msg => _logger.LogError(msg), msg => _logger.LogInformation(msg));
                _logger.LogInformation("======================================== FILE PROCESS END ========================================\n");
            }
        }

        private async Task UploadUnprocessedTransactionsAsync(User currentUser)
        {
            // Prevent overlapping uploads
            if (_isUploading)
            {
                _logger.LogInformation("Upload already in progress. Skipping duplicate upload request.\n");
                return; // Note: No separator needed here as no UPLOAD START was logged
            }

            if (string.IsNullOrEmpty(currentUser.AccessToken))
            {
                _logger.LogWarning("AccessToken is null or empty. Skipping upload.\n");
                return;
            }

            _isUploading = true;  // Set flag to indicate upload in progress
            try
            {
                _logger.LogInformation("======================================== UPLOAD START ========================================");

                var unprocessedTransactions = _dbHelper.GetUnprocessedTransactions();

                if (unprocessedTransactions.Count == 0)
                {
                    // No pending transactions - nothing to do
                    _logger.LogInformation("No unprocessed transactions to upload.\n");
                    _logger.LogInformation("======================================== UPLOAD END ========================================\n");
                    return;
                }

                _logger.LogInformation("Found {count} unprocessed transaction(s) to upload.\n", unprocessedTransactions.Count);

                // Upload each transaction one by one
                foreach (var transaction in unprocessedTransactions)
                {
                    try
                    {
                        // Skip if transaction has no JSON data
                        if (string.IsNullOrEmpty(transaction.Json))
                        {
                            _logger.LogWarning("Transaction {transId} has empty JSON. Skipping.\n", transaction.TransId);
                            continue;
                        }

                        _logger.LogInformation("Uploading transaction {transId} to server...\n", transaction.TransId);

                        // Call API to upload transaction
                        bool uploadSuccess = await _apiService.UploadJsonAsync(
                            transaction.Json,
                            currentUser.StoreId,
                            currentUser.AccessToken);

                        if (uploadSuccess)
                        {
                            // Upload succeeded - delete from database
                            _dbHelper.DeleteTransaction(transaction.TransId);
                            _logger.LogInformation("Transaction {transId} uploaded successfully and deleted from database.\n", transaction.TransId);
                        }
                        else
                        {
                            // Upload failed - leave in database, will retry later
                            _logger.LogWarning("Failed to upload transaction {transId}. Will retry later.\n", transaction.TransId);
                        }
                    }
                    catch (HttpRequestException ex)
                    {
                        // Network error — no point trying remaining transactions if network is down
                        _logger.LogWarning("Network error uploading transaction {transId}: {message}. Will retry when network is available.\n", transaction.TransId, ex.Message);
                        break;
                    }
                    catch (TaskCanceledException ex)
                    {
                        // Request timeout (took longer than 30 seconds)
                        _logger.LogWarning("Request timeout for transaction {transId}: {message}. Will retry later.\n", transaction.TransId, ex.Message);
                        break;
                    }
                    catch (System.Net.Sockets.SocketException ex)
                    {
                        // Socket-level network error
                        _logger.LogWarning("Socket error uploading transaction {transId}: {message}. Will retry when network is available.\n", transaction.TransId, ex.Message);
                        break;
                    }
                    catch (System.ComponentModel.Win32Exception ex)
                    {
                        // Windows network error (ethernet disabled, adapter issues)
                        _logger.LogWarning("Network interface error uploading transaction {transId}: {message}. Will retry when network is available.\n", transaction.TransId, ex.Message);
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Unexpected per-transaction error - log but continue trying remaining transactions
                        _logger.LogError(ex, "Unexpected error uploading transaction {transId}: {message}.\n\n", transaction.TransId, ex.Message);
                    }
                }

                _logger.LogInformation("======================================== UPLOAD END ========================================\n");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("Network error in UploadUnprocessedTransactionsAsync: {message}. Transactions will be retried when network is available.\n", ex.Message);
                _logger.LogInformation("======================================== UPLOAD END ========================================\n");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _logger.LogWarning("Network interface error in UploadUnprocessedTransactionsAsync: {message}. Transactions will be retried when network is available.\n", ex.Message);
                _logger.LogInformation("======================================== UPLOAD END ========================================\n");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in UploadUnprocessedTransactionsAsync.\n\n");
                _logger.LogInformation("======================================== UPLOAD END ========================================\n");
            }
            finally
            {
                _isUploading = false;  // Always reset flag when done
            }
        }

        private bool CheckMerchandiseCodesInXml(XDocument xDoc, int userDeptId, string fileName)
        {
            try
            {
                // Find all MerchandiseCode elements and parse them as integers
                var merchandiseCodes = xDoc.Descendants()
                    .Where(e => e.Name.LocalName == "MerchandiseCode")     // Find MerchandiseCode elements
                    .Select(e => e.Value)                                   // Get their values
                    .Where(val => int.TryParse(val, out _))                // Keep only valid integers
                    .Select(val => int.Parse(val))                         // Convert to int
                    .Distinct()                                             // Remove duplicates
                    .ToList();

                if (merchandiseCodes.Count == 0)
                {
                    // No MerchandiseCode found in file
                    _logger.LogWarning("No MerchandiseCode found in file {fileName}. Skipping.\n", fileName);
                    return false;
                }

                // Check if any MerchandiseCode matches user's DeptId
                bool hasMatch = merchandiseCodes.Contains(userDeptId);
                if (hasMatch)
                {
                    _logger.LogInformation("Found matching MerchandiseCode ({deptId}) in file {fileName}.\n", userDeptId, fileName);
                }

                return hasMatch;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing XML to check MerchandiseCode in file {fileName}.\n\n", fileName);
                return false;
            }
        }

        private int ExtractTransactionId(string jsonContent, string fileName)
        {
            try
            {
                // Parse JSON and find TransactionID field
                var jsonObject = JObject.Parse(jsonContent);
                string? transIdString = jsonObject.SelectToken("$..TransactionID")?.ToString();

                if (string.IsNullOrEmpty(transIdString))
                {
                    _logger.LogWarning("TransactionID not found in JSON for file: {file}.\n", fileName);
                    return 0;
                }

                // Try to parse as integer
                if (!int.TryParse(transIdString, out int transactionId))
                {
                    _logger.LogError("TransactionID is not a valid integer: {transId}.\n", transIdString);
                    return 0;
                }

                return transactionId;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error parsing JSON to extract TransactionID: {message}.\n\n", ex.Message);
                return 0;
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("======================================== SERVICE STOP ========================================");
            _logger.LogInformation("File Monitor Service stopping.\n");

            _watcherRestartCts?.Cancel();
            _watcherRestartCts?.Dispose();

            // Stop file watcher
            _watcher?.Dispose();

            await base.StopAsync(cancellationToken);
        }

        public override void Dispose()
        {
            _watcherRestartCts?.Cancel();
            _watcherRestartCts?.Dispose();
            _watcher?.Dispose();
            base.Dispose();
        }

        private bool IsPathAccessible(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            try
            {
                return Directory.Exists(path);
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("Access denied to path: {path}.\n", path);
                return false;
            }
            catch (IOException ioEx)
            {
                _logger.LogWarning("IO error accessing path: {path}. Error: {error}.\n", path, ioEx.Message);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error accessing path: {path}. Error: {error}.\n", path, ex.Message);
                return false;
            }
        }

        private bool IsUncPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            // Check if path starts with \\ or //
            return path.StartsWith(@"\\") || path.StartsWith(@"//");
        }
    }
}