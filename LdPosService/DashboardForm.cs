using ApiLibrary;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;

namespace LdPosService
{
    public partial class DashboardForm : Form
    {
        private readonly int _uuid;
        private readonly string _userName;
        private readonly string _accessToken;
        private readonly int _storeId;
        private readonly int _deptId;
        private readonly DatabaseServices _dbHelper;
        private readonly ApiServices _apiService;
        private const string ServiceName = "LdFileProcessor";

        // Result of a service control action. The action runs on a worker thread; the UI thread shows this afterwards.
        private readonly record struct ServiceActionResult(string Title, string Message, MessageBoxIcon Icon);

        // Stopping can take up to 30 s: the service host waits for an upload in flight before it shuts down.
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

        private string _welcomeText = "";

        public DashboardForm(int uuid, string userName, string accessToken, int storeId, int deptId)
        {
            InitializeComponent();
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);  // app icon comes from the exe, nothing embedded in the .resx
            _uuid = uuid;
            _userName = userName;
            _accessToken = accessToken;
            _storeId = storeId;
            _deptId = deptId;
            _dbHelper = new DatabaseServices();
            _apiService = new ApiServices();
        }

        // onClick Logout Function 
        private async void btnLogout_Click(object sender, EventArgs e)
        {
            try
            {
                // Stop the Windows Service on a worker thread; the window keeps responding meanwhile
                await RunServiceActionAsync("Stopping the service, please wait...", StopWindowsService);

                SetBusy(true, "Logging out...");
                await _apiService.LogoutAsync(_accessToken);
                _dbHelper.DeleteUserByUUID(_uuid);
                _dbHelper.DeleteAllTransactions();

                // Hand control back to the login form that opened this dialog (LoginForm.ShowDashboard): it shows
                // itself again. No new LoginForm here, so dialogs no longer nest on every logout/login cycle.
                DialogResult = DialogResult.Retry;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error during logout: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetBusy(false, "");
            }
        }

        // onFormLoad Function
        private void DashboardForm_Load(object sender, EventArgs e)
        {
            _welcomeText = $"Welcome, {_userName}!   Store {_storeId}, Lottery Dept {_deptId}";
            lblWelcome.Text = _welcomeText;
        }

        // onClick Browse Folder
        private async void btnBrowseFolder_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog folderDialog = new FolderBrowserDialog())
            {
                folderDialog.Description = "Select Folder to Monitor";
                folderDialog.ShowNewFolderButton = false;

                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    string folderPath = folderDialog.SelectedPath;

                    // Validate that the path exists
                    if (Directory.Exists(folderPath))
                    {
                        // The service cannot see drive letters mapped by the logged-in user (Z:), so it is
                        // always given the real \\server\share path. UNC and local paths pass through unchanged.
                        if (!TryGetServicePath(folderPath, out string servicePath, out string? problem))
                        {
                            MessageBox.Show(problem, "Network Drive", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }

                        try
                        {
                            // UpdateUserFolderPath
                            _dbHelper.UpdateUserFolderPath(_uuid, servicePath);

                            string note = servicePath.Equals(folderPath, StringComparison.OrdinalIgnoreCase)
                                ? ""
                                : $"\n\n({folderPath} is a mapped drive. The service will use the network path shown above.)";
                            MessageBox.Show($"Folder saved successfully:\n\n{servicePath}{note}",
                                "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);

                            // Start or restart the service on a worker thread so the window stays responsive
                            await RunServiceActionAsync("Restarting the service, please wait...", StartOrRestartWindowsService);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Error saving folder path: {ex.Message}",
                                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                    else
                    {
                        MessageBox.Show("Selected folder does not exist. Please select a valid folder.",
                            "Invalid Path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
        }

        // ---- Mapped drive letter -> UNC path ----
        // The Windows service runs in session 0 under NetworkService. Drive letters mapped by the logged-in
        // user (e.g. Z:) do not exist in that session, so a saved "Z:\..." path would make the service log
        // "folder not accessible" forever. The real \\server\share path is stored instead.
        [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

        private const int NO_ERROR = 0;
        private const int ERROR_MORE_DATA = 234;

        /// <summary>
        /// Turns "Z:\BOOutBox" into "\\10.5.48.2\XMLGateway\BOOutBox" when Z: is a mapped network drive.
        /// UNC paths and local drives are returned unchanged. Returns false, with a message for the user,
        /// when the drive is a network drive whose share cannot be determined.
        /// </summary>
        private static bool TryGetServicePath(string selectedPath, out string servicePath, out string? problem)
        {
            servicePath = selectedPath;
            problem = null;

            // Already a network path
            if (selectedPath.StartsWith(@"\\"))
                return true;

            string? root = Path.GetPathRoot(selectedPath);   // "Z:\"
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
                return true;   // not a drive letter path, nothing to convert

            DriveType driveType;
            try
            {
                driveType = new DriveInfo(root).DriveType;
            }
            catch
            {
                return true;   // cannot tell; the Directory.Exists check above already passed
            }

            if (driveType != DriveType.Network)
                return true;   // local disk, fine as it is

            string driveLetter = root.Substring(0, 2);   // "Z:"
            var remote = new StringBuilder(260);
            int length = remote.Capacity;
            int result = WNetGetConnection(driveLetter, remote, ref length);
            if (result == ERROR_MORE_DATA)
            {
                remote = new StringBuilder(length);
                result = WNetGetConnection(driveLetter, remote, ref length);
            }

            if (result != NO_ERROR || remote.Length == 0)
            {
                problem = $"{driveLetter} is a mapped network drive, but Windows could not tell which network share it points to (error {result}).\n\n" +
                          "The background service cannot use drive letters. Please select the folder through its network path instead, for example:\n\n" +
                          @"\\10.5.48.2\XMLGateway\BOOutBox";
                return false;
            }

            // "\\server\share" + "\BOOutBox"
            string rest = selectedPath.Substring(2).TrimEnd('\\');
            servicePath = remote.ToString().TrimEnd('\\') + rest;
            return true;
        }

        private void SetBusy(bool busy, string status)
        {
            btnBrowseFolder.Enabled = !busy;
            btnLogout.Enabled = !busy;
            UseWaitCursor = busy;
            lblWelcome.Text = busy ? status : _welcomeText;
        }

        /// <summary>
        /// Runs a service control action on a worker thread, so the window keeps repainting and responding,
        /// then shows its result. ServiceController.Stop/Start/WaitForStatus block for seconds; on the UI
        /// thread that showed up as "(Not Responding)" and tempted people to kill the app mid-restart, which
        /// left the service stopped.
        /// </summary>
        private async Task RunServiceActionAsync(string status, Func<ServiceActionResult> action)
        {
            SetBusy(true, status);
            try
            {
                ServiceActionResult result = await Task.Run(action);
                MessageBox.Show(this, result.Message, result.Title, MessageBoxButtons.OK, result.Icon);
            }
            finally
            {
                SetBusy(false, "");
            }
        }

        // Runs on a worker thread: no UI access in here, the result is shown by RunServiceActionAsync
        private ServiceActionResult StartOrRestartWindowsService()
        {
            try
            {
                using (ServiceController sc = new ServiceController(ServiceName))
                {
                    sc.Refresh();
                    ServiceControllerStatus currentStatus = sc.Status;

                    if (currentStatus == ServiceControllerStatus.Running)
                    {
                        // Service is running - RESTART it to pick up new folder path
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);
                        return new ServiceActionResult("Service Restarted",
                            "Service was running - restarted to apply new folder path.", MessageBoxIcon.Information);
                    }

                    if (currentStatus == ServiceControllerStatus.Stopped)
                    {
                        // Service is stopped - just START it
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);
                        return new ServiceActionResult("Service Started",
                            "Service started successfully.", MessageBoxIcon.Information);
                    }

                    if (currentStatus == ServiceControllerStatus.StartPending)
                    {
                        // Wait for it to start, then restart
                        sc.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);
                        return new ServiceActionResult("Service Restarted",
                            "Service was starting - restarted to apply new folder path.", MessageBoxIcon.Information);
                    }

                    if (currentStatus == ServiceControllerStatus.StopPending)
                    {
                        // Wait for it to stop, then start
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);
                        return new ServiceActionResult("Service Started",
                            "Service was stopping - started successfully.", MessageBoxIcon.Information);
                    }

                    return new ServiceActionResult("Service",
                        $"Service is in state '{currentStatus}'. Nothing was changed; please check it manually.", MessageBoxIcon.Warning);
                }
            }
            catch (InvalidOperationException)
            {
                return new ServiceActionResult("Service Not Found",
                    $"Service '{ServiceName}' not found. Please ensure the service is installed.", MessageBoxIcon.Error);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return new ServiceActionResult("Permission Denied",
                    "Access denied. Please run this application as Administrator to control the service.", MessageBoxIcon.Error);
            }
            catch (System.TimeoutException)
            {
                return new ServiceActionResult("Timeout",
                    "Service operation timed out. Please check the service manually.", MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                return new ServiceActionResult("Error", $"Error controlling service: {ex.Message}", MessageBoxIcon.Error);
            }
        }

        // Runs on a worker thread: no UI access in here, the result is shown by RunServiceActionAsync
        private ServiceActionResult StopWindowsService()
        {
            try
            {
                using (ServiceController sc = new ServiceController(ServiceName))
                {
                    if (sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.StartPending)
                    {
                        // Wait for start to complete if starting
                        if (sc.Status == ServiceControllerStatus.StartPending)
                        {
                            sc.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);
                        }

                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
                        return new ServiceActionResult("Service Stopped", "Windows Service stopped successfully.", MessageBoxIcon.Information);
                    }

                    if (sc.Status == ServiceControllerStatus.StopPending)
                    {
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
                        return new ServiceActionResult("Service Stopped", "Windows Service stopped successfully.", MessageBoxIcon.Information);
                    }

                    return new ServiceActionResult("Service Stopped", "Windows Service is already stopped.", MessageBoxIcon.Information);
                }
            }
            catch (InvalidOperationException)
            {
                return new ServiceActionResult("Service Not Found",
                    $"Service '{ServiceName}' not found. Please ensure the service is installed.", MessageBoxIcon.Error);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return new ServiceActionResult("Permission Denied",
                    "Access denied. Please run this application as Administrator to control the service.", MessageBoxIcon.Error);
            }
            catch (System.TimeoutException)
            {
                return new ServiceActionResult("Timeout",
                    "Service stop operation timed out. Please check the service manually.", MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                return new ServiceActionResult("Error", $"Error stopping service: {ex.Message}", MessageBoxIcon.Error);
            }
        }
    }
}
