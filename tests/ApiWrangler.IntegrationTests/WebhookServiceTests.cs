using System.Security.Cryptography;
using System.Text;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class WebhookServiceTests
{
    [Fact]
    public void WebhookService_VerifySignature_HmacSha256Hex_MatchesExactRawBytes()
    {
        string secret = "top-secret-signing-key";
        byte[] payload = Encoding.UTF8.GetBytes("{\"event\": \"payment_succeeded\", \"amount\": 4200}");

        // Compute valid signature
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        string validHex = "sha256=" + Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant();

        bool valid = WebhookService.VerifySignature(
            WebhookSignatureType.HmacSha256Hex,
            payload,
            validHex,
            secret,
            out string? diag
        );

        Assert.True(valid);
        Assert.Null(diag);

        // Verification fails if body is modified/reserialized with whitespace differences
        byte[] tamperedPayload = Encoding.UTF8.GetBytes("{\"event\":\"payment_succeeded\",\"amount\":4200}");
        bool invalid = WebhookService.VerifySignature(
            WebhookSignatureType.HmacSha256Hex,
            tamperedPayload,
            validHex,
            secret,
            out string? diagTampered
        );

        Assert.False(invalid);
        Assert.NotNull(diagTampered);
    }

    [Fact]
    public async Task WebhookService_Capture_RecordsRawBytesAndHeaders()
    {
        await using var service = new WebhookService();
        service.AddListener(new WebhookListenerDefinition(
            Id: "stripe-listener",
            Name: "Stripe Webhooks",
            PathPrefix: "/webhooks/stripe",
            SignatureType: WebhookSignatureType.None,
            ResponseStatusCode: 200,
            ResponseBody: "{\"received\": true}"
        ));

        await service.StartAsync();

        try
        {
            using var client = new HttpClient();
            var content = new StringContent("{\"id\": \"evt_123\"}", Encoding.UTF8, "application/json");
            content.Headers.Add("X-Custom-Provider", "Stripe-Mock");

            var response = await client.PostAsync(service.ListeningUrl + "/webhooks/stripe/charge", content);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

            Assert.Single(service.Events);
            var captured = service.Events[0];
            Assert.Equal("stripe-listener", captured.ListenerId);
            Assert.Equal("POST", captured.Method);
            Assert.Equal("/webhooks/stripe/charge", captured.Path);
            Assert.Contains("Stripe-Mock", captured.Headers["X-Custom-Provider"]);
            Assert.Equal("{\"id\": \"evt_123\"}", captured.BodyString);
            Assert.False(captured.IsReplay);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task WebhookService_Replay_BytePreservingAndDestinationPolicy()
    {
        await using var targetEngine = new MockEngine();
        targetEngine.AddRule(new MockRule(
            Id: "target-api",
            Name: "Target API Endpoint",
            Method: "POST",
            PathPattern: "/internal/events",
            ResponseStatusCode: 200,
            ResponseBody: "{\"status\": \"acknowledged\"}"
        ));
        await targetEngine.StartAsync();

        await using var service = new WebhookService();
        service.AddListener(new WebhookListenerDefinition(
            Id: "listener-policy",
            Name: "Policy Controlled Listener",
            PathPrefix: "/webhooks/internal",
            AllowedForwardingDestinations: ["http://127.0.0.1"] // Only local loopback allowed
        ));
        await service.StartAsync();

        try
        {
            using var client = new HttpClient();

            // 1. Send webhook to capture it
            var content = new StringContent("raw original payload bytes", Encoding.UTF8, "text/plain");
            await client.PostAsync(service.ListeningUrl + "/webhooks/internal/event", content);
            var captured = Assert.Single(service.Events);

            // 2. Replay to allowed target
            string targetUrl = targetEngine.ListeningUrl + "/internal/events";
            var replayReq = new WebhookReplayRequest(captured.Id, targetUrl);
            var replayResult = await service.ReplayAsync(client, replayReq);

            Assert.True(replayResult.Success);
            Assert.Equal(200, replayResult.StatusCode);

            // Verify replay audit event was logged
            Assert.Equal(2, service.Events.Count);
            var replayEvent = service.Events[1];
            Assert.True(replayEvent.IsReplay);
            Assert.Equal(captured.Id, replayEvent.ParentEventId);
            Assert.Equal("raw original payload bytes", replayEvent.BodyString);

            // 3. Replay to forbidden external target should be blocked by destination policy
            var forbiddenReq = new WebhookReplayRequest(captured.Id, "http://malicious-external-proxy.com/leak");
            var forbiddenResult = await service.ReplayAsync(client, forbiddenReq);

            Assert.False(forbiddenResult.Success);
            Assert.Equal(403, forbiddenResult.StatusCode);
            Assert.Contains("forbidden by listener forwarding destination policy", forbiddenResult.ErrorMessage);
        }
        finally
        {
            await service.StopAsync();
            await targetEngine.StopAsync();
        }
    }
}

