using System.Text;
using Newtonsoft.Json;
using ApiLibrary.Models;


namespace ApiLibrary
{
    public class ApiServices
    {
        private const string LoginUrl = "https://lotteryscreen.app/api/user/login";
        private const string LogoutUrl = "https://lotteryscreen.app/api/user/logout";
        private const string UploadUrl = "https://lotteryscreen.app/api/check-json";

        private readonly HttpClient _httpClient;

        public ApiServices()
        {
            _httpClient = new HttpClient();
        }

        public async Task<LoginResponse> LoginAsync(string? uuid)  // ← Add ?
        {
            try
            {
                if (string.IsNullOrWhiteSpace(uuid))  // ← Add validation
                {
                    return new LoginResponse
                    {
                        success = 0,
                        user = null,
                        accessToken = null,
                        store_id = 0
                    };
                }

                string json = $"{{\"uuid\":\"{uuid}\"}}";
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                HttpResponseMessage response = await _httpClient.PostAsync(LoginUrl, content, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    string responseContent = await response.Content.ReadAsStringAsync();
                    return JsonConvert.DeserializeObject<LoginResponse>(responseContent) ?? new LoginResponse  // ← Add ?? new
                    {
                        success = 0,
                        user = null,
                        accessToken = null,
                        store_id = 0
                    };
                }

                return new LoginResponse
                {
                    success = 0,
                    user = null,
                    accessToken = null,
                    store_id = 0
                };
            }
            catch
            {
                return new LoginResponse
                {
                    success = 0,
                    user = null,
                    accessToken = null,
                    store_id = 0
                };
            }
        }

        public async Task<bool> LogoutAsync(string accessToken)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, LogoutUrl);
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                request.Content = new StringContent("", Encoding.UTF8, "application/json");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                HttpResponseMessage response = await _httpClient.SendAsync(request, cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> UploadJsonAsync(string jsonContent, int storeId, string accessToken)
        {
            try
            {
                var payload = new
                {
                    json = jsonContent,
                    store_id = storeId
                };

                string payloadJson = JsonConvert.SerializeObject(payload);
                int requestSize = Encoding.UTF8.GetByteCount(payloadJson);
                Console.WriteLine($"Direct Upload Request Size: {requestSize:N0} bytes ({requestSize / 1024.0:F2} KB)");

                var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                using var request = new HttpRequestMessage(HttpMethod.Post, UploadUrl);
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                request.Content = content;

                // ✅ FIXED: Use CancellationTokenSource for per-request timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

                // ✅ FIXED: Pass cancellation token to SendAsync
                HttpResponseMessage response = await _httpClient.SendAsync(request, cts.Token);

                // responseBody intentionally not read — only status code is used
                return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"Network error uploading JSON: {ex.Message}");
                return false;
            }
            catch (TaskCanceledException ex)
            {
                Console.WriteLine($"Request timeout uploading JSON: {ex.Message}");
                return false;
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                Console.WriteLine($"Socket error uploading JSON: {ex.Message}");
                return false;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Console.WriteLine($"Win32 network error uploading JSON: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error uploading JSON: {ex.Message}");
                return false;
            }
        }
    }
}