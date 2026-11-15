using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using LanPod.Services;

internal static class RouterRestartRegression
{
    internal static async Task<int> RunAsync()
    {
        var tokenField = typeof(XiaomiRouterClient).GetField("_token", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var handler = new RebootHandler();
        using var client = new XiaomiRouterClient(new HttpClient(handler));
        client.Configure("http://router-test.invalid", "synthetic");
        tokenField.SetValue(client, "synthetic-session");
        await client.RestartRouterAsync();
        Require(handler.Calls == 1 && handler.Method == HttpMethod.Post &&
            handler.Path == "/cgi-bin/luci/;stok=synthetic-session/api/xqsystem/reboot" &&
            handler.Body == "client=web" && tokenField.GetValue(client) is null,
            "The restart request, session cleanup or submission count was incorrect.");

        tokenField.SetValue(client, "synthetic-session");
        handler.ResponseCode = 500;
        var rejected = false;
        try { await client.RestartRouterAsync(); }
        catch (Exception exception) when (exception.GetType().Name == "RouterApiException") { rejected = true; }
        Require(rejected && handler.Calls == 2, "An API failure was hidden or retried as a second reboot.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        rejected = false;
        try { await client.RestartRouterAsync(cancellation.Token); }
        catch (OperationCanceledException) { rejected = true; }
        Require(rejected && handler.Calls == 2, "Cancellation still submitted a reboot request.");
        Console.WriteLine("Router restart PASS: isolated HTTP verifies endpoint, POST payload, one submission, API errors, session reset and cancellation.");
        return 0;
    }

    private sealed class RebootHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal int ResponseCode { get; set; }
        internal HttpMethod? Method { get; private set; }
        internal string? Path { get; private set; }
        internal string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent($"{{\"code\":{ResponseCode}}}", Encoding.UTF8, "application/json") };
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
