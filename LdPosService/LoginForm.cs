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
                    DashboardForm dashboard = new DashboardForm(loginResponse.user.id, loginResponse.user.username ?? "", loginResponse.accessToken ?? "");

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

        private void LoginForm_Load(object sender, EventArgs e)
        {
            // Initialize database
            DatabaseServices.InitializeDatabase();

            try
            {
                var hasUser = _dbHelper.GetLastLoggedInUser();

                if (hasUser != null)
                {
                    // Auto-login and open dashboard
                    DashboardForm dashboard = new DashboardForm(
                        hasUser.UUID,
                        hasUser.Username ?? "",
                        hasUser.AccessToken ?? ""
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

        
    }
}
