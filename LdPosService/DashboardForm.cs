using ApiLibrary;
using System.ServiceProcess;

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
                        try
                        {
                            // UpdateUserFolderPath
                            _dbHelper.UpdateUserFolderPath(_uuid, folderPath);

                            MessageBox.Show($"Folder saved successfully:\n\n{folderPath}",
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
