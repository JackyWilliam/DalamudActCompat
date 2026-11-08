using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Infrastructure.Storage;

// 登录 · 设备已满 against a scripted server: the refusal reaches the login page with the device
// list, the answer repeats the sign-in with the same credentials and the chosen devices, and
// cancelling or answering twice never resends a password.
internal static class CloudDeviceLimitSmokeTests
{
    internal static async Task RunAsync(string root)
    {
        var paths = new PluginPaths(Path.Combine(root, "device-limit"));
        paths.EnsureCreated();
        var logins = new List<JsonElement>();
        using var handler = new Handler(async request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/auth/login"))
                return Json(HttpStatusCode.NotFound, new { error = "not_found", message = "接口不存在。" });
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            logins.Add(body);
            if (logins.Count == 1)
                return Json(HttpStatusCode.Conflict, new
                {
                    error = "device_limit",
                    message = "这个账号已在 5 台设备上登录。请选择要登出的设备，或全部登出后再登录；旧版 DACT 请先更新。",
                    deviceLimit = 5,
                    devices = new object[]
                    {
                        new { id = "0123456789abcdef01234567", name = "LIVING-ROOM-PC", signedInAt = "2026-11-05T13:30:00.000Z", firstSeenAt = "2026-09-21T08:00:00.000Z" },
                        new { id = "fedcba9876543210fedcba98", name = (string?)null, signedInAt = "2026-11-01T02:00:00.000Z", firstSeenAt = (string?)null },
                    },
                });
            // The fixture stops after the answer arrives; the real success path is covered by the server tests.
            return Json(HttpStatusCode.Unauthorized, new { error = "invalid_credentials", message = "用户名或密码错误。" });
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid/") };
        using var api = new CloudApiClient(http);
        using var client = new CloudClientService(paths, api, new(paths.CloudCredentialFile), new(paths.CloudBanFile), new(paths.CloudDeviceFile), new(), new());

        await client.LoginAsync("fixture", "fixture-password-1", string.Empty, false, default);
        var first = logins.Single();
        Check(first.GetProperty("deviceName").GetString() == CloudMachineIdentity.GetDeviceName() &&
              first.GetProperty("signOutDevices").GetArrayLength() == 0 && !first.GetProperty("signOutAllDevices").GetBoolean(),
            "A plain sign-in must report this PC's name and choose nothing.");
        var limit = client.Snapshot.DeviceLimit ?? throw new Exception("The device-limit refusal did not reach the login page.");
        Check(limit.Limit == 5 && limit.Devices.Count == 2 && limit.Devices[0].Name == "LIVING-ROOM-PC" && limit.Devices[1].Name is null &&
              limit.Devices[0].FirstSeenAt is not null && client.Snapshot.StatusIsError && !client.Snapshot.IsSignedIn && !client.Snapshot.IsBusy,
            "The device list was not parsed for the prompt.");

        await client.ResolveDeviceLimitAsync(["0123456789abcdef01234567"], false, default);
        var answer = logins[1];
        Check(answer.GetProperty("username").GetString() == "fixture" && answer.GetProperty("password").GetString() == "fixture-password-1" &&
              answer.GetProperty("signOutDevices")[0].GetString() == "0123456789abcdef01234567" && !answer.GetProperty("signOutAllDevices").GetBoolean(),
            "The answer must repeat the sign-in with the same credentials and the chosen device.");
        Check(client.Snapshot.DeviceLimit is null, "The prompt must close once answered.");
        await client.ResolveDeviceLimitAsync([], true, default);
        Check(logins.Count == 2 && client.Snapshot.StatusIsError, "A second answer must not resend the credentials.");

        await client.LoginAsync("fixture", "fixture-password-1", string.Empty, false, default);
        Check(logins.Count == 3, "The fixture answers the third sign-in with a credential error.");
        // A fresh refusal, then cancel: nothing is kept for a later answer.
        logins.Clear();
        await client.LoginAsync("fixture", "fixture-password-1", string.Empty, false, default);
        Check(client.Snapshot.DeviceLimit is not null, "The refusal must show again for a new sign-in.");
        client.CancelDeviceLimit();
        await client.ResolveDeviceLimitAsync([], true, default);
        Check(client.Snapshot.DeviceLimit is null && logins.Count == 1, "Cancelling must forget the typed credentials.");
        Console.WriteLine("Device limit: computer name sent, device list prompt, chosen/all sign-out answer, single use and cancel passed.");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body)
        => new(status) { Content = JsonContent.Create(body) };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
