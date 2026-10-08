using ApiLibrary;
using ApiLibrary.Models;
using ApiLibrary.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Xml.Linq;

namespace LdFileProcessor
{
    public class FileMonitorService : BackgroundService
    {
        // SERVICES & UTILITIES
        private readonly ILogger<FileMonitorService> _logger;           // For logging errors and info
        private readonly DatabaseServices _dbHelper;                    // For database operations
        private readonly ApiServices _apiService;                       // For API calls (upload)
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

        private readonly SemaphoreSlim _uploadLock = new SemaphoreSlim(1, 1);   // Guards uploads: only one upload pass runs at a time (file events + periodic retry)
        private readonly object _watcherLock = new object();                     // Guards _watcher create/dispose (loop thread + watcher event threads)
        private readonly ConcurrentDictionary<FileSystemWatcher, byte> _deadWatchers = new ConcurrentDictionary<FileSystemWatcher, byte>();   // Watchers that raised Error before being published (see OnWatcherError)

        // Files already picked up in this run, by file name. The folder scan only processes files that are NOT in
        // here, so a file the watcher already handled is not processed twice in one run. In-memory only: after a
        // restart everything in the folder is processed again, as before (the server rejects repeats itself).
        private readonly ConcurrentDictionary<string, byte> _handledFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastFolderScanUtc = DateTime.MinValue;

        // How often pending (not yet uploaded) transactions are retried while the watcher is running.
        // Covers two cases a file event alone cannot: a transaction saved while another upload pass was
        // already running, and uploads that failed because the network or API was unavailable.
        private static readonly TimeSpan UploadRetryInterval = TimeSpan.FromMinutes(1);

        // Wait between attempts when the watcher cannot be created at startup (path answers Directory.Exists,
        // but the directory handle could not be opened, e.g. a share that is still reconnecting).
        private static readonly TimeSpan WatcherRetryDelay = TimeSpan.FromSeconds(30);

        // Safety net for a watcher that dies silently (no Error event, e.g. the POS rebooted and the SMB session
        // went stale): the folder is scanned on this interval and anything not handled in this run is processed.
        private static readonly TimeSpan FolderScanInterval = TimeSpan.FromMinutes(5);

        // The scan skips files modified more recently than this (the POS may still be writing them); they are
        // picked up by the watcher or by the next scan.
        private static readonly TimeSpan ScanSettleTime = TimeSpan.FromSeconds(10);

        // How long a new file may stay locked by the POS before it is skipped (wall clock, so a check that itself
        // hangs on a dying share cannot stretch the wait). A skipped file is not lost, the periodic folder scan
        // picks it up later.
        private static readonly TimeSpan LockWaitBudget = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(500);

        // Timeouts for file-system calls on the network share. Client logs (1-3 Oct 2026) showed Directory.Exists
        // blocking 12-16 minutes per call while the share was unreachable; nothing on the keep-alive loop may wait
        // that long. A call that times out counts as "not accessible right now" and is retried on the next pass;
        // the abandoned call finishes on its own in the background.
        private static readonly TimeSpan PathCheckTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan WatcherCreateTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FolderListTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan FileCopyTimeout = TimeSpan.FromSeconds(30);

        // The Directory.Exists call currently in flight (it may be blocked). The next check reuses it instead of
        // starting another blocked thread every minute.
        private readonly object _pathCheckLock = new object();
        private Task<bool>? _pathCheck;
        private string? _pathCheckPath;

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
                    DatabaseServices.InitializeDatabase(_logger);

                    // Inner loop - keeps running until service is stopped
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        try
                        {
                            // Get the folder path to monitor from database
                            await GetFolderPathFromDatabaseAsync();

                            // If we have a valid folder path and can access it
                            if (!string.IsNullOrEmpty(_folderPath) && await IsPathAccessibleAsync(_folderPath))
                            {
                                _logger.LogInformation("Folder path found and accessible: {path}. Starting file monitoring.\n", _folderPath);

                                // Start watching the folder for new files. If the watcher cannot be created
                                // (path reachable, but the directory handle fails, e.g. a share that is still
                                // reconnecting), wait and go round again: re-read the path and retry.
                                if (!await TrySetupFileWatcherAsync())
                                {
                                    _logger.LogWarning("File watcher could not be started. Retrying in {delay}.\n", WatcherRetryDelay);
                                    await Task.Delay(WatcherRetryDelay, stoppingToken);
                                    continue;
                                }

                                // Process any files already in the folder
                                await ScanFolderAsync("startup");

                                // Keep-alive loop. Every minute: make sure the watcher is alive (recreate it if it
                                // died), scan the folder every FolderScanInterval as a safety net, retry pending
                                // uploads. Only ends when the service is stopped.
                                // (The old 30-second folder path refresh was removed on purpose; the desktop app
                                // restarts the service when the folder changes.)
                                while (!stoppingToken.IsCancellationRequested)
                                {
                                    await Task.Delay(UploadRetryInterval, stoppingToken);
                                    await EnsureWatcherAliveAsync();
                                    await ScanFolderIfDueAsync();
                                    await RetryPendingUploadsAsync();
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

                    if (lastUser.DeptId <= 0)
                    {
                        _logger.LogError("The saved login has no POS lottery department (DeptId {deptId}). Every transaction will be ignored " +
                            "until \"Pos Lottery Dept ID\" is set in Store Settings on lotteryscreen.app and the user logs in again.\n", lastUser.DeptId);
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
      
        /// <summary>
        /// Creates the FileSystemWatcher for _folderPath and publishes it as _watcher. Returns false (leaving
        /// _watcher as it was) when it cannot be created or does not come up within WatcherCreateTimeout. On a
        /// UNC path that happens when the share answers Directory.Exists but the directory handle cannot be
        /// opened yet (reconnect in progress, Wi-Fi blip). Both the constructor (its Path setter calls
        /// Directory.Exists) and EnableRaisingEvents can block for minutes on a dying share, so they run on a
        /// thread-pool thread with a timeout. The caller decides how to retry; this method never throws.
        /// </summary>
        private async Task<bool> TrySetupFileWatcherAsync()
        {
            if (string.IsNullOrEmpty(_folderPath))
            {
                _logger.LogError("Folder path is empty. Cannot start file watcher.\n");
                return false;
            }

            if (!await IsPathAccessibleAsync(_folderPath))
            {
                _logger.LogWarning("Folder path is not accessible, cannot start file watcher: {path}.\n", _folderPath);
                return false;
            }

            string path = _folderPath;
            Task<FileSystemWatcher?> create = Task.Run(() => CreateWatcher(path));
            Task finished = await Task.WhenAny(create, Task.Delay(WatcherCreateTimeout));
            if (finished != create)
            {
                _logger.LogWarning("Creating the file watcher for {path} did not finish within {timeout}. Will try again later.\n", path, WatcherCreateTimeout);
                // If the abandoned attempt completes later, throw its watcher away
                _ = create.ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion)
                    {
                        try { t.Result?.Dispose(); } catch { /* ignore */ }
                    }
                }, TaskScheduler.Default);
                return false;
            }

            FileSystemWatcher? watcher = await create;   // CreateWatcher never throws; null means failure (already logged)
            if (watcher == null)
                return false;

            lock (_watcherLock)
            {
                if (_deadWatchers.TryRemove(watcher, out _))
                {
                    // It raised Error in the moment between being enabled and being published: do not keep it
                    _logger.LogWarning("File watcher for {path} failed right after starting. Will try again later.\n", path);
                    try { watcher.Dispose(); } catch { /* ignore */ }
                    return false;
                }

                try { _watcher?.Dispose(); } catch { /* old one is dead anyway */ }
                _watcher = watcher;
            }

            // Log different message for network vs local paths
            if (IsUncPath(path))
            {
                _logger.LogInformation("File watcher started for UNC network path: {path}.\n", path);
            }
            else
            {
                _logger.LogInformation("File watcher started for local path: {path}.\n", path);
            }

            return true;
        }

        /// <summary>
        /// Builds and enables a watcher for the given path. Runs on a thread-pool thread because both the
        /// constructor and EnableRaisingEvents touch the share and can block. Returns null on failure (logged).
        /// </summary>
        private FileSystemWatcher? CreateWatcher(string path)
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(path)
                {
                    Filter = "*.xml",                                                                              // Only watch XML files
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,  // Watch for new files
                    InternalBufferSize = 65536                                                                     // 64KB buffer (handles multiple files)
                };

                // Register event handlers
                watcher.Created += OnFileCreated;      // Called when new file appears
                watcher.Error += OnWatcherError;       // Called when error occurs (e.g., network disconnect)

                // This is the call that actually opens the directory handle and can throw on a flaky share
                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting up file watcher for {path}.\n\n", path);
                try { watcher?.Dispose(); } catch { /* nothing useful to do */ }
                return null;
            }
        }

        /// <summary>
        /// Runs from the keep-alive loop every minute. If the watcher is gone (it failed to start, or
        /// OnWatcherError tore it down after a network error) and the folder is reachable again, recreate it
        /// and scan the folder for files that arrived while it was down. Never throws.
        /// </summary>
        private async Task EnsureWatcherAliveAsync()
        {
            try
            {
                if (_watcher != null)
                    return;

                if (!await IsPathAccessibleAsync(_folderPath))
                {
                    _logger.LogWarning("File watcher is down and folder path is not accessible: {path}. Will check again in {interval}.\n", _folderPath, UploadRetryInterval);
                    return;
                }

                _logger.LogInformation("Folder path is accessible again: {path}. Restarting file watcher.\n", _folderPath);

                if (!await TrySetupFileWatcherAsync())
                {
                    _logger.LogWarning("File watcher could not be restarted. Will try again in {interval}.\n", UploadRetryInterval);
                    return;
                }

                // Process any files that arrived while the watcher was down
                await ScanFolderAsync("watcher recovery");
            }
            catch (Exception ex)
            {
                // Must never throw: an exception here would bubble up into ExecuteAsync
                _logger.LogError(ex, "Error while checking/restarting the file watcher.\n\n");
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

                // Wait until the file is released by the POS system, at most LockWaitBudget of wall-clock time.
                // (Counted by the clock, not by attempts: on a dying share a single IsFileLocked call can itself
                // hang for minutes; the client logs showed one "30 second" wait that took 3.5 minutes.)
                int delayMs = (int)LockRetryDelay.TotalMilliseconds;
                var waited = System.Diagnostics.Stopwatch.StartNew();
                bool locked = _fileUtilities.IsFileLocked(fullPath);
                while (locked && waited.Elapsed < LockWaitBudget)
                {
                    _logger.LogWarning("File is still in use, retrying in {delayMs}ms ({elapsed:F0}s of {budget:F0}s): {file}.\n", delayMs, waited.Elapsed.TotalSeconds, LockWaitBudget.TotalSeconds, fullPath);
                    await Task.Delay(LockRetryDelay);
                    locked = _fileUtilities.IsFileLocked(fullPath);
                }

                if (locked)
                {
                    _logger.LogError("File is still locked after {seconds:F0}s. Skipping for now; the periodic folder scan will pick it up: {file}.\n",
                        waited.Elapsed.TotalSeconds, fullPath);
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
                _handledFiles[fileName] = 0;  // Mark as handled in this run so the periodic folder scan skips it
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
                _logger.LogWarning("Network path temporarily unavailable (Win32 error 64): {path}. " +
                    "The watcher will be recreated by the keep-alive loop once the path is reachable again.\n", _folderPath);
            }
            else
            {
                _logger.LogError(ex, "File watcher error (network path unavailable, or the watcher buffer overflowed). " +
                    "The watcher will be recreated by the keep-alive loop.\n");
            }

            // Tear the broken watcher down. EnsureWatcherAliveAsync (every minute) sees _watcher == null,
            // recreates it when the path is reachable and scans the folder for anything that was missed.
            var dead = sender as FileSystemWatcher;
            lock (_watcherLock)
            {
                if (dead != null && ReferenceEquals(_watcher, dead))
                {
                    _watcher = null;
                }
                else if (dead != null)
                {
                    // Error from a watcher that is not (or not yet) the published one: remember it so that a
                    // creation still in progress does not publish a watcher that has already failed
                    _deadWatchers[dead] = 0;
                }
            }

            if (dead != null)
            {
                // Dispose off the watcher's own event thread
                Task.Run(() =>
                {
                    try { dead.Dispose(); } catch { /* already gone */ }
                });
            }
        }

        /// <summary>
        /// Runs from the keep-alive loop. Scans the folder every FolderScanInterval as a safety net for a
        /// watcher that died silently (no Error event). Never throws.
        /// </summary>
        private async Task ScanFolderIfDueAsync()
        {
            try
            {
                if (_watcher == null)
                    return;  // path is down; EnsureWatcherAliveAsync scans once it is back

                if (DateTime.UtcNow - _lastFolderScanUtc < FolderScanInterval)
                    return;

                await ScanFolderAsync("periodic");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in periodic folder scan.\n\n");
            }
        }

        /// <summary>
        /// Copies every *.xml in the folder that has not been handled in this run to the temp folder and
        /// processes it. On "startup" nothing has been handled yet, so everything in the folder is processed
        /// (the server rejects repeats itself). After that only files the watcher missed are picked up.
        /// Never throws.
        /// </summary>
        private async Task ScanFolderAsync(string reason)
        {
            try
            {
                // Check if folder is accessible
                if (!await IsPathAccessibleAsync(_folderPath))
                {
                    _logger.LogWarning("Folder scan ({reason}) skipped, folder not accessible: {path}.\n", reason, _folderPath);
                    return;
                }

                _lastFolderScanUtc = DateTime.UtcNow;

                // List the folder on a thread-pool thread with a timeout: Directory.GetFiles and GetLastWriteTimeUtc
                // can block for minutes if the share drops mid-scan, and this runs on the keep-alive loop.
                string folder = _folderPath;
                DateTime cutoff = DateTime.UtcNow - ScanSettleTime;
                var listing = await RunWithTimeoutAsync(() =>
                {
                    string[] all = Directory.GetFiles(folder, "*.xml");
                    var fresh = new List<string>();
                    foreach (var file in all)
                    {
                        if (_handledFiles.ContainsKey(Path.GetFileName(file)))
                            continue;   // already handled in this run
                        if (File.GetLastWriteTimeUtc(file) > cutoff)
                            continue;   // too fresh, may still be being written; pick it up next time
                        fresh.Add(file);
                    }
                    return (all, fresh);
                }, FolderListTimeout, "Listing " + folder);

                if (!listing.completed)
                {
                    _logger.LogWarning("Folder scan ({reason}) skipped, listing the folder timed out: {path}.\n", reason, folder);
                    return;
                }

                string[] xmlFiles = listing.result.all;
                List<string> newFiles = listing.result.fresh;

                // Forget handled files that are no longer in the folder (Modisoft / Passport removed them)
                var present = new HashSet<string>(xmlFiles.Select(f => Path.GetFileName(f)), StringComparer.OrdinalIgnoreCase);
                foreach (var handled in _handledFiles.Keys)
                {
                    if (!present.Contains(handled))
                        _handledFiles.TryRemove(handled, out _);
                }

                if (newFiles.Count == 0)
                {
                    if (reason == "startup")
                        _logger.LogInformation("Folder scan ({reason}): no XML files to process.\n", reason);
                    return;  // periodic scans stay silent when there is nothing to do
                }

                _logger.LogInformation("======================================== FOLDER SCAN ({reason}) START ========================================", reason);
                _logger.LogInformation("Found {count} XML file(s) to process ({total} in folder).\n", newFiles.Count, xmlFiles.Length);

                if (reason == "periodic")
                {
                    // The watcher should have reported these files. It has most likely died without raising an
                    // Error event (POS reboot, stale SMB session). Recreate it so new files are seen immediately
                    // again instead of only on the next scan. Recreating a healthy watcher is harmless.
                    _logger.LogWarning("Periodic scan found {count} file(s) the watcher did not report. Recreating the file watcher.\n", newFiles.Count);
                    if (!await TrySetupFileWatcherAsync())
                    {
                        _logger.LogWarning("File watcher could not be recreated now. The keep-alive loop will keep trying every {interval}.\n", UploadRetryInterval);
                    }
                }

                // Step 1: Copy all files to temp folder
                var copiedFiles = new List<string>();

                foreach (var file in newFiles)
                {
                    try
                    {
                        string fileName = Path.GetFileName(file);
                        string tempFilePath = Path.Combine(_tempFolderPath, fileName);

                        // FIX #5: Always overwrite — a pre-existing temp file may be corrupt/incomplete from a previous crash
                        var copy = await RunWithTimeoutAsync(() => { File.Copy(file, tempFilePath, true); return true; }, FileCopyTimeout, "Copying " + fileName);
                        if (!copy.completed)
                        {
                            // Not marked as handled: the next scan tries again once the share is back
                            _logger.LogWarning("Copying {file} timed out; will retry on the next scan.\n", fileName);
                            continue;
                        }
                        _handledFiles[fileName] = 0;  // Mark as handled in this run
                        _logger.LogInformation("Copied file: {file}.\n", fileName);
                        copiedFiles.Add(tempFilePath);
                    }
                    catch (IOException ioEx)
                    {
                        // Locked by the POS or a network hiccup: not marked as handled, the next scan retries it
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

                _logger.LogInformation("Starting to process {count} copied file(s).\n", copiedFiles.Count);

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

                _logger.LogInformation("======================================== FOLDER SCAN ({reason}) END ========================================\n", reason);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in folder scan ({reason}).\n\n", reason);
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
                    _fileUtilities.DeleteFile(filePath, msg => _logger.LogError(msg), msg => _logger.LogInformation(msg));
                    _logger.LogInformation("======================================== FILE PROCESS END ========================================\n");
                    return;
                }

                // Get user's department ID (used to filter transactions)
                int userDeptId = _currentUser.DeptId;
                string fileName = Path.GetFileName(filePath);

                if (userDeptId <= 0)
                {
                    // Misconfigured store (see GetFolderPathFromDatabaseAsync): nothing can match, so say so loudly
                    _logger.LogError("Ignoring {fileName}: the saved login has no POS lottery department (DeptId {deptId}). Set \"Pos Lottery Dept ID\" in Store Settings and log in again.\n", fileName, userDeptId);
                    _fileUtilities.DeleteFile(filePath, msg => _logger.LogError(msg), msg => _logger.LogInformation(msg));
                    _logger.LogInformation("======================================== FILE PROCESS END ========================================\n");
                    return;
                }

                // Step 5: Parse XML (validate it's proper XML format)
                XDocument xDoc;
                try
                {
                    xDoc = XDocument.Parse(xmlContent);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error parsing XML file: {fileName}.\n\n", fileName);
                    _fileUtilities.DeleteFile(filePath, msg => _logger.LogError(msg), msg => _logger.LogInformation(msg));
                    _logger.LogInformation("======================================== FILE PROCESS END ========================================\n");
                    return;
                }

                // Step 6: Check if XML contains items matching user's department
                // (Only process transactions for this department)
                bool hasMatchingDeptId = CheckMerchandiseCodesInXml(xDoc, userDeptId, fileName);

                if (!hasMatchingDeptId)
                {
                    _logger.LogInformation("File {fileName} does not contain MerchandiseCode matching DeptId {deptId}.\n", fileName, userDeptId);
                    _fileUtilities.DeleteFile(filePath, msg => _logger.LogError(msg), msg => _logger.LogInformation(msg));
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
                    _fileUtilities.DeleteFile(filePath, msg => _logger.LogError(msg), msg => _logger.LogInformation(msg));
                    _logger.LogInformation("======================================== FILE PROCESS END ========================================\n");
                    return;
                }

                // Step 9: Extract transaction ID from JSON
                int transactionId = ExtractTransactionId(jsonContent, fileName);
                if (transactionId == 0)
                {
                    // Transaction ID not found or invalid - delete file and skip
                    _fileUtilities.DeleteFile(filePath, msg => _logger.LogError(msg), msg => _logger.LogInformation(msg));
                    _logger.LogInformation("======================================== FILE PROCESS END ========================================\n");
                    return;
                }

                // Step 10: Save transaction to local database
                // IMPORTANT: This ensures data is never lost, even if internet is down
                _dbHelper.AddTransaction(transactionId, fileName, jsonContent);
                _logger.LogInformation("File processed and saved to database: {fileName}.\n", fileName);

                // Step 11: Delete temp file (data is safely in database now)
                _fileUtilities.DeleteFile(filePath, msg => _logger.LogError(msg), msg => _logger.LogInformation(msg));

                // Step 12: Try to upload immediately
                await UploadUnprocessedTransactionsAsync(_currentUser);

                _logger.LogInformation("======================================== FILE PROCESS END ========================================\n");
            }
            catch (Exception ex)
            {
                // Catch any unexpected errors - log and delete temp file
                _logger.LogError(ex, "Error processing file: {filePath}.\n\n", filePath);
                _fileUtilities.DeleteFile(filePath, msg => _logger.LogError(msg), msg => _logger.LogInformation(msg));
                _logger.LogInformation("======================================== FILE PROCESS END ========================================\n");
            }
        }

        private async Task UploadUnprocessedTransactionsAsync(User currentUser)
        {
            if (string.IsNullOrEmpty(currentUser.AccessToken))
            {
                _logger.LogWarning("AccessToken is null or empty. Skipping upload.\n");
                return;
            }

            // Prevent overlapping uploads (a file event and the periodic retry can both land here).
            // Wait(0) is an atomic try-acquire: it never blocks, and the check-and-take is a single
            // step, so two callers can never both get through.
            if (!_uploadLock.Wait(0))
            {
                _logger.LogInformation("Upload already in progress. Skipping duplicate upload request.\n");
                return; // Note: No separator needed here as no UPLOAD START was logged
            }

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
                            // Upload failed (network down, timeout or server error). UploadJsonAsync never throws,
                            // so this is the only place a failure shows up. Stop this pass here: trying the
                            // remaining transactions would just repeat the same failure (and timeouts) for each
                            // one. Everything still in the database is picked up by the periodic retry.
                            _logger.LogWarning("Failed to upload transaction {transId}. Stopping this pass; will retry in {interval}.\n", transaction.TransId, UploadRetryInterval);
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Unexpected per-transaction error (e.g. database) - log but continue trying remaining transactions
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
                _uploadLock.Release();  // Always release so the next upload pass can run
            }
        }

        /// <summary>
        /// Runs every UploadRetryInterval while the watcher is active. Uploads anything still pending in
        /// the database: transactions saved while another upload pass was running, and uploads that failed
        /// earlier because the network or API was down. Stays silent when there is nothing to do, so the
        /// log does not fill up with empty UPLOAD START/END blocks every minute.
        /// </summary>
        private async Task RetryPendingUploadsAsync()
        {
            try
            {
                var user = _currentUser;
                if (user == null)
                    return;

                int pending = _dbHelper.CountUnprocessedTransactions();
                if (pending == 0)
                    return;

                _logger.LogInformation("Periodic retry: {count} pending transaction(s) found in database.\n", pending);
                await UploadUnprocessedTransactionsAsync(user);
            }
            catch (Exception ex)
            {
                // Must never throw: an exception here would bubble up into ExecuteAsync and recreate the file watcher
                _logger.LogError(ex, "Error in periodic upload retry.\n\n");
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

            // Stop file watcher
            DisposeWatcher();

            await base.StopAsync(cancellationToken);
        }

        public override void Dispose()
        {
            DisposeWatcher();
            base.Dispose();
        }

        private void DisposeWatcher()
        {
            lock (_watcherLock)
            {
                try { _watcher?.Dispose(); } catch { /* shutting down, nothing useful to do */ }
                _watcher = null;
            }
        }

        /// <summary>
        /// Directory.Exists with a timeout. On an unreachable share the call blocks for 12-16 minutes (client logs,
        /// 1-3 Oct 2026); "no answer within PathCheckTimeout" counts as not accessible and the keep-alive loop tries
        /// again on its next pass. Only one check is in flight at a time: while one is still blocked, callers wait
        /// on that same call instead of starting another blocked thread every minute.
        /// </summary>
        private async Task<bool> IsPathAccessibleAsync(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            Task<bool> check;
            lock (_pathCheckLock)
            {
                if (_pathCheck == null || _pathCheck.IsCompleted || !string.Equals(_pathCheckPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _pathCheckPath = path;
                    _pathCheck = Task.Run(() => DirectoryExistsSafe(path));
                }
                check = _pathCheck;
            }

            Task finished = await Task.WhenAny(check, Task.Delay(PathCheckTimeout));
            if (finished != check)
            {
                _logger.LogWarning("Checking {path} did not answer within {timeout}; treating it as not accessible for now.\n", path, PathCheckTimeout);
                return false;
            }

            return await check;
        }

        private bool DirectoryExistsSafe(string path)
        {
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

        /// <summary>
        /// Runs a blocking file-system call on a thread-pool thread and waits at most the given timeout. Returns
        /// (false, default) on timeout; the abandoned call finishes on its own. An exception thrown by the call is
        /// rethrown to the caller, exactly as if it had been called directly.
        /// </summary>
        private async Task<(bool completed, T? result)> RunWithTimeoutAsync<T>(Func<T> operation, TimeSpan timeout, string what)
        {
            Task<T> task = Task.Run(operation);
            Task finished = await Task.WhenAny(task, Task.Delay(timeout));
            if (finished != task)
            {
                _logger.LogWarning("{what} did not finish within {timeout}.\n", what, timeout);
                _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);   // observe a late failure quietly
                return (false, default);
            }
            return (true, await task);
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