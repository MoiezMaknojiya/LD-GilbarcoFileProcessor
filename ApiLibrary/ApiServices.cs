using System.Net.Http.Headers;
using System.Text;
using ApiLibrary.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace ApiLibrary
{
    /// <summary>
    /// HTTP client for the lotteryscreen.app API. No method throws: each one returns a failure value and
    /// logs the reason (HTTP status and response body, timeout, or exception), so the service log always
    /// shows why a call did not succeed. Pass an ILogger to receive those messages; without one (the
    /// desktop app) they are dropped and the caller's MessageBox is all the user sees.
    /// </summary>
    public class ApiServices
    {
        private const string LoginUrl = "https://lotteryscreen.app/api/user/login";
        private const string LogoutUrl = "https://lotteryscreen.app/api/user/logout";
        private const string UploadUrl = "https://lotteryscreen.app/api/check-json";

        private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan LogoutTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(30);
        private const int MaxLoggedBodyLength = 500;   // response bodies are logged, but trimmed

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;

        public ApiServices(ILogger<ApiServices>? logger = null)
        {
            _httpClient = new HttpClient();
            _logger = logger ?? NullLogger<ApiServices>.Instance;
        }

        public async Task<LoginResponse> LoginAsync(string? uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid))
            {
                _logger.LogWarning("Login skipped: UUID is empty.");
                return FailedLogin();
            }

            try
            {
                // Serialized rather than string-concatenated, so a quote or backslash in the UUID cannot break the JSON
                string json = JsonConvert.SerializeObject(new { uuid });
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var cts = new CancellationTokenSource(LoginTimeout);
                using HttpResponseMessage response = await _httpClient.PostAsync(LoginUrl, content, cts.Token);
                string body = await ReadBodyAsync(response, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Login failed: HTTP {status} {reason}. Response: {body}", (int)response.StatusCode, response.ReasonPhrase, Trim(body));
                    return FailedLogin();
                }

                LoginResponse? result = JsonConvert.DeserializeObject<LoginResponse>(body);
                if (result == null)
                {
                    _logger.LogWarning("Login failed: empty or unreadable response. Response: {body}", Trim(body));
                    return FailedLogin();
                }

                if (result.success == 1 && result.user != null)
                {
                    _logger.LogInformation("Login succeeded for user {userId} (store {storeId}, dept {deptId}).", result.user.id, result.store_id, result.pos_dept_id);
                }
                else
                {
                    _logger.LogWarning("Login rejected by server (success={success}). Response: {body}", result.success, Trim(body));
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Login failed: no response within {timeout}.", LoginTimeout);
                return FailedLogin();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Login failed: {message}", ex.Message);
                return FailedLogin();
            }
        }

        public async Task<bool> LogoutAsync(string accessToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, LogoutUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Content = new StringContent("", Encoding.UTF8, "application/json");

                using var cts = new CancellationTokenSource(LogoutTimeout);
                using HttpResponseMessage response = await _httpClient.SendAsync(request, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    string body = await ReadBodyAsync(response, cts.Token);
                    _logger.LogWarning("Logout failed: HTTP {status} {reason}. Response: {body}", (int)response.StatusCode, response.ReasonPhrase, Trim(body));
                    return false;
                }

                _logger.LogInformation("Logout succeeded.");
                return true;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Logout failed: no response within {timeout}.", LogoutTimeout);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Logout failed: {message}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Uploads one transaction. Returns true only for an HTTP 2xx answer (the server answers 200 for
        /// duplicates too). Never throws: a false return always has a log entry saying why.
        /// </summary>
        public async Task<bool> UploadJsonAsync(string jsonContent, int storeId, string accessToken)
        {
            try
            {
                var payload = new { json = jsonContent, store_id = storeId };
                string payloadJson = JsonConvert.SerializeObject(payload);
                int requestSize = Encoding.UTF8.GetByteCount(payloadJson);
                _logger.LogDebug("Upload request size: {bytes:N0} bytes ({kb:F2} KB).", requestSize, requestSize / 1024.0);

                using var request = new HttpRequestMessage(HttpMethod.Post, UploadUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                using var cts = new CancellationTokenSource(UploadTimeout);
                using HttpResponseMessage response = await _httpClient.SendAsync(request, cts.Token);
                string body = await ReadBodyAsync(response, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Upload accepted: HTTP {status}. Response: {body}", (int)response.StatusCode, Trim(body));
                    return true;
                }

                _logger.LogWarning("Upload rejected: HTTP {status} {reason}. Response: {body}", (int)response.StatusCode, response.ReasonPhrase, Trim(body));
                return false;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Upload failed: no response within {timeout} (network down or server slow).", UploadTimeout);
                return false;
            }
            catch (HttpRequestException ex)
            {
                // DNS failure, connection refused, TLS error, socket reset: the network side, expected while offline
                _logger.LogWarning("Upload failed: network error: {message}", ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upload failed: {message}", ex.Message);
                return false;
            }
        }

        private static LoginResponse FailedLogin()
        {
            return new LoginResponse { success = 0, user = null, accessToken = null, store_id = 0 };
        }

        private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken token)
        {
            try
            {
                return await response.Content.ReadAsStringAsync(token);
            }
            catch
            {
                return "";
            }
        }

        private static string Trim(string body)
        {
            string s = body.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= MaxLoggedBodyLength ? s : s.Substring(0, MaxLoggedBodyLength) + "...";
        }
    }
}