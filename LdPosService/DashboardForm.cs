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
        private readonly DatabaseServices _dbHelper;
        private readonly ApiServices _apiService;
        private const string ServiceName = "LdFileProcessor";

        public DashboardForm(int uuid, string userName, string accessToken)
        {
            InitializeComponent();
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);  // app icon comes from the exe, nothing embedded in the .resx
            _uuid = uuid;
            _userName = userName;
            _accessToken = accessToken;
            _dbHelper = new DatabaseServices();
            _apiService = new ApiServices();
        }

        // onClick Logout Function 
        private async void btnLogout_Click(object sender, EventArgs e)
        {
            try
            {
                btnLogout.Enabled = false;

                // Stop the Windows Service
                StopWindowsService();

                bool apiLogoutSuccess = await _apiService.LogoutAsync(_accessToken);
                _dbHelper.DeleteUserByUUID(_uuid);
                _dbHelper.DeleteAllTransactions();

                LoginForm loginForm = new LoginForm();
                this.Hide();
                loginForm.ShowDialog();
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during logout: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                btnLogout.Enabled = true;
            }
        }

        // onFormLoad Function
        private void DashboardForm_Load(object sender, EventArgs e)
        {
            lblWelcome.Text = $"Welcome, {_userName}!";
        }

        // onClick Browse Folder
        private void btnBrowseFolder_Click(object sender, EventArgs e)
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

                            // Start or Restart service based on current state
                            StartOrRestartWindowsService();
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

        private void StartOrRestartWindowsService()
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
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                        MessageBox.Show("Service was running - restarted to apply new folder path.",
                            "Service Restarted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else if (currentStatus == ServiceControllerStatus.Stopped)
                    {
                        // Service is stopped - just START it
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                        MessageBox.Show("Service started successfully.",
                            "Service Started", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else if (currentStatus == ServiceControllerStatus.StartPending)
                    {
                        // Wait for it to start, then restart
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                        MessageBox.Show("Service was starting - restarted to apply new folder path.",
                            "Service Restarted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else if (currentStatus == ServiceControllerStatus.StopPending)
                    {
                        // Wait for it to stop, then start
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                        MessageBox.Show("Service was stopping - started successfully.",
                            "Service Started", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                MessageBox.Show($"Service '{ServiceName}' not found. Please ensure the service is installed.",
                    "Service Not Found", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                MessageBox.Show("Access denied. Please run this application as Administrator to control the service.",
                    "Permission Denied", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (System.TimeoutException)
            {
                MessageBox.Show("Service operation timed out. Please check the service manually.",
                    "Timeout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error controlling service: {ex.Message}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopWindowsService()
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
                            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                        }

                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                        MessageBox.Show("Windows Service stopped successfully.", "Service Stopped", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        MessageBox.Show("Windows Service is already stopped.", "Service Stopped", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                MessageBox.Show($"Service '{ServiceName}' not found. Please ensure the service is installed.",
                    "Service Not Found", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                MessageBox.Show("Access denied. Please run this application as Administrator to control the service.",
                    "Permission Denied", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (System.TimeoutException)
            {
                MessageBox.Show("Service stop operation timed out. Please check the service manually.",
                    "Timeout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error stopping service: {ex.Message}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
