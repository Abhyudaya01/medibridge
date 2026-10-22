using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MediBridge.Tests;

public class HealthTests : IClassFixture<ClaimsApiFactory>
{
    private readonly ClaimsApiFactory _factory;

    public HealthTests(ClaimsApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_ReturnsOkAndApplicationVersion()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.NotNull(payload);
        Assert.Equal(2, payload.Count);
        Assert.Equal("ok", payload["status"]);
        Assert.Equal("0.7.0", payload["version"]);
    }
}
