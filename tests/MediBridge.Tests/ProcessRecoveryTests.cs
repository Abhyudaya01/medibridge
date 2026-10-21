using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace MediBridge.Tests;

public class ProcessRecoveryTests
{
    [Fact]
    public async Task RealProcessCrashAfterHandlerCommitRecoversWithoutRepeatingEffect()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.GetFullPath($"../../../../../src/MediBridge.Api/bin/{configuration}/net10.0/MediBridge.Api.dll", AppContext.BaseDirectory);
        var path = Path.Combine(Path.GetTempPath(), $"process-recovery-{Guid.NewGuid():N}.db");
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(3) };
        Process? process = null;
        try
        {
            process = Start(true); await Healthy();
            client.DefaultRequestHeaders.Add("Idempotency-Key", "SYN-PROCESS-RECOVERY");
            var request = new ClaimSubmission("CLM-PROCESS", "0000000006", "SYN-PROCESS", new(2026, 1, 20), ["SYN-PROCESS"], 1m);
            try { await client.PostAsJsonAsync("/api/claims", request); } catch (HttpRequestException) { }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotEqual(0, process.ExitCode); process.Dispose();
            process = Start(false); await Healthy();
            var replay = await client.PostAsJsonAsync("/api/claims", request);
            Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotency-Replayed")));
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                var messages = await client.GetFromJsonAsync<JsonElement>("/api/outbox");
                if (messages.EnumerateArray().Any(m => m.GetProperty("claimId").GetString() == "CLM-PROCESS" && m.GetProperty("state").GetString() == "Completed")) break;
                await Task.Delay(500);
            }
            var detail = await client.GetFromJsonAsync<JsonElement>("/api/claims/CLM-PROCESS");
            Assert.Equal(1, detail.GetProperty("claim").GetProperty("attemptCount").GetInt32());
            Assert.Single(detail.GetProperty("timeline").EnumerateArray(), e => e.GetProperty("outcome").GetString() == "AttemptSucceeded");
            Assert.Contains(detail.GetProperty("timeline").EnumerateArray(), e => e.GetProperty("outcome").GetString() == "DuplicateDeliveryIgnored");
        }
        finally { if (process is not null) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); process.Dispose(); } }

        Process Start(bool crash)
        {
            var info = new ProcessStartInfo("dotnet", dll) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.Environment["Database__Path"] = path; info.Environment["ASPNETCORE_URLS"] = client.BaseAddress!.ToString();
            info.Environment["Seed__Enabled"] = "false"; info.Environment["FaultInjection__Enabled"] = crash ? "true" : "false";
            info.Environment["FaultInjection__CrashAfterStep"] = "AfterHandlerCommit"; info.Environment["DOTNET_DbgEnableMiniDump"] = "0";
            info.Environment.Remove("ConnectionStrings__Sql"); info.Environment.Remove("SQLAZURECONNSTR_Sql");
            var child = Process.Start(info)!; _ = child.StandardOutput.ReadToEndAsync(); _ = child.StandardError.ReadToEndAsync(); return child;
        }
        async Task Healthy()
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                try { if ((await client.GetAsync("/health")).IsSuccessStatusCode) return; } catch (HttpRequestException) { }
                await Task.Delay(250);
            }
            throw new TimeoutException("Synthetic child application did not start.");
        }
    }
}
