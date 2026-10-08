using ApiLibrary;
using ApiLibrary.Models;

namespace LdPosService
{
    public partial class LoginForm : Form
    {
        private readonly DatabaseServices _dbHelper;
        private readonly ApiServices _apiService;

        public LoginForm()
        {
            InitializeComponent();
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);  // app icon comes from the exe, nothing embedded in the .resx
            _dbHelper = new DatabaseServices();
            _apiService = new ApiServices();
        }

        // onClick Login Function
        private async void btnLogin_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtUUID.Text))
            {
                MessageBox.Show("Please enter UUID", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnLogin.Enabled = false;

            try
            {
                var loginResponse = await _apiService.LoginAsync(txtUUID.Text);

                if (loginResponse.success == 1 && loginResponse.user != null)
                {
                    // The service filters every transaction by this department. Without it every sale would be
                    // ignored silently, so the login is refused and the token just issued is released again.
                    if (loginResponse.pos_dept_id <= 0)
                    {
                        MessageBox.Show(StoreNotConfiguredMessage("Login succeeded, but"), "Store Not Configured",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        if (!string.IsNullOrEmpty(loginResponse.accessToken))
                        {
                            await _apiService.LogoutAsync(loginResponse.accessToken);   // do not leave an unused token on the server
                        }
                        btnLogin.Enabled = true;
                        return;
                    }

                    var user = new User
                    {
                        UUID = loginResponse.user.id,
                        Email = loginResponse.user.email,
                        Username = loginResponse.user.username,
                        AccessToken = loginResponse.accessToken,
                        StoreId = loginResponse.store_id,
                        DeptId = loginResponse.pos_dept_id,
                        LoginTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    };

                    _dbHelper.AddUser(user);
                    DashboardForm dashboard = new DashboardForm(loginResponse.user.id, loginResponse.user.username ?? "", loginResponse.accessToken ?? "", loginResponse.store_id, loginResponse.pos_dept_id);

                    this.Hide();
                    dashboard.ShowDialog();
                    this.Close();
                }
                else
                {
                    MessageBox.Show("Login Failed: Invalid UUID", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    btnLogin.Enabled = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                btnLogin.Enabled = true;
            }
        }

        private async void LoginForm_Load(object sender, EventArgs e)
        {
            // Initialize database
            DatabaseServices.InitializeDatabase();

            try
            {
                var hasUser = _dbHelper.GetLastLoggedInUser();

                if (hasUser != null && hasUser.DeptId <= 0)
                {
                    // Saved by an older build, before the login check existed: drop it and ask for a fresh login
                    MessageBox.Show(StoreNotConfiguredMessage("The saved login for this store has been removed because"), "Store Not Configured",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    if (!string.IsNullOrEmpty(hasUser.AccessToken))
                    {
                        await _apiService.LogoutAsync(hasUser.AccessToken);   // release the token on the server
                    }
                    _dbHelper.DeleteUserByUUID(hasUser.UUID);
                    _dbHelper.DeleteAllTransactions();
                    hasUser = null;
                }

                if (hasUser != null)
                {
                    // Auto-login and open dashboard
                    DashboardForm dashboard = new DashboardForm(
                        hasUser.UUID,
                        hasUser.Username ?? "",
                        hasUser.AccessToken ?? "",
                        hasUser.StoreId,
                        hasUser.DeptId
                    );

                    this.Hide();
                    dashboard.ShowDialog();
                    this.Close();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error checking saved user: {ex.Message}",
                                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string StoreNotConfiguredMessage(string lead)
        {
            return lead + " this store has no \"Pos Lottery Dept ID\" on lotteryscreen.app.\n\n" +
                   "The background service filters every transaction by that department, so it would ignore all sales.\n\n" +
                   "Open lotteryscreen.app > Store Settings, set \"Pos Lottery Dept ID\" (and \"Pos Payout Dept ID\"), " +
                   "then log in again and select the BOOutBox folder.";
        }

        
    }
}
