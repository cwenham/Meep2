using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Meep2.Modules;
using Meep2.SDK.Messages;
using Meep2.Tests.Support;

namespace Meep2.Tests.Meeplang;

/// <summary>Listen and Respond over HTTP and TCP, default replies, and streamed bodies (step 5).</summary>
public class ServerTests
{
    private static readonly HttpClient s_client = new() { Timeout = Wait.Limit };

    // Sends a request, trying again while Listen is still starting.
    private static async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> request)
    {
        var deadline = DateTime.UtcNow + Wait.Limit;
        while (true)
        {
            try
            {
                return await s_client.SendAsync(request());
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }
    }

    // Reads exactly this many bytes as UTF-8 text.
    private static async Task<string> ReadAsync(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer);
        return Encoding.UTF8.GetString(buffer);
    }

    private static Task<HttpResponseMessage> GetAsync(string url) => SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url));

    private static Task<HttpResponseMessage> PostAsync(string url, string body, string contentType = "text/plain") =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, contentType) });

    // Connects to Listen.TCP, trying again while it's still starting.
    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var deadline = DateTime.UtcNow + Wait.Limit;
        while (true)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return client;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                client.Dispose();
                await Task.Delay(20);
            }
        }
    }

    [Test]
    public async Task Listen_and_Respond_answer_an_http_request()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{port}}/hello/"
                Respond "Hello, {msg.Query['name']}"
            """);

        using var response = await GetAsync($"http://127.0.0.1:{port}/hello/?name=Ann");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("Hello, Ann");
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("text/plain");
    }

    [Test]
    public async Task Respond_sends_the_status_and_media_type_it_is_given()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{port}}/"
                Respond '{"ok":true}' status=201 content-type="application/json"
            """);

        using var response = await GetAsync($"http://127.0.0.1:{port}/");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("""{"ok":true}""");
    }

    [Test]
    public async Task A_request_nothing_answers_gets_the_default_reply()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{port}}/"
                Where msg.Method == "POST"
                    Respond "posted"
            """);

        using var ignored = await GetAsync($"http://127.0.0.1:{port}/");
        using var posted = await PostAsync($"http://127.0.0.1:{port}/", "x");

        await Assert.That(ignored.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(await ignored.Content.ReadAsStringAsync()).IsEqualTo("No module answered this request.");
        await Assert.That(await posted.Content.ReadAsStringAsync()).IsEqualTo("posted");
    }

    [Test]
    public async Task The_default_reply_can_be_changed()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{port}}/" default-status=404 default-reply="Nothing here"
                Where msg.Path == "/found"
                    Respond "found"
            """);

        using var response = await GetAsync($"http://127.0.0.1:{port}/missing");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("Nothing here");
    }

    [Test]
    public async Task An_error_is_answered_with_500_and_no_details()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{port}}/"
                Extract "$.x"
                    Respond
            """);

        using var response = await PostAsync($"http://127.0.0.1:{port}/", "this isn't JSON");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("Something went wrong.");
        await Wait.Until(() => !running.Run.Unhandled.IsEmpty);
        var logged = running.Run.Unhandled.Single();
        await Assert.That(logged.AnsweredBy?.Verb).IsEqualTo("Respond");
        await Assert.That(logged.EmittedBy?.ToString()).StartsWith("Extract");
    }

    [Test]
    public async Task Answered_errors_are_logged_but_cant_stop_a_server()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{port}}/"
                Extract "$.x"
                    Respond "x is {msg.Text}"
            """);

        // Far more than the 10% of 20 messages that stops a pipeline whose errors go unhandled.
        for (var i = 0; i < 30; i++)
        {
            using var bad = await PostAsync($"http://127.0.0.1:{port}/", "this isn't JSON");
            await Assert.That(bad.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        }
        using var good = await PostAsync($"http://127.0.0.1:{port}/", """{"x":7}""");

        await Assert.That(await good.Content.ReadAsStringAsync()).IsEqualTo("x is 7");
        await Wait.Until(() => running.Run.Unhandled.Count == 30);
        await Assert.That(running.Run.Notices).IsEmpty();
    }

    [Test]
    public async Task An_answered_error_that_a_later_module_handles_isnt_logged()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{port}}/"
                Extract "$.x"
                    Respond
                        Sink
            """);

        using var response = await PostAsync($"http://127.0.0.1:{port}/", "this isn't JSON");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Wait.Until(() => running.Run.At("Sink").Count == 1);
        await Task.Delay(50);
        await Assert.That(running.Run.Unhandled).IsEmpty();
    }

    [Test]
    public async Task The_reply_to_an_error_is_worked_out_from_the_error()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{port}}/"
                Extract "$.x"
                    Respond "x is {msg.Text}" error-reply="Couldn't read that: {msg.Error}" error-status=400
            """);

        using var good = await PostAsync($"http://127.0.0.1:{port}/", """{"x":7}""");
        using var bad = await PostAsync($"http://127.0.0.1:{port}/", "this isn't JSON");

        await Assert.That(await good.Content.ReadAsStringAsync()).IsEqualTo("x is 7");
        await Assert.That(bad.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await bad.Content.ReadAsStringAsync()).StartsWith("Couldn't read that: ");
    }

    [Test]
    public async Task Details_sends_everything_known_about_an_error()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{port}}/"
                Extract "$.x"
                    Respond details=true
            """);

        using var response = await PostAsync($"http://127.0.0.1:{port}/", "this isn't JSON");
        var text = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(text).Contains("JSON");
        await Assert.That(text).Contains("Exception");
    }

    [Test]
    public async Task Listens_on_one_port_take_their_own_paths()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{port}}/a/"
                Respond "A"
            Listen "http://127.0.0.1:{{port}}/a/b/"
                Respond "B"
            """);

        using var a = await GetAsync($"http://127.0.0.1:{port}/a/x");
        using var b = await GetAsync($"http://127.0.0.1:{port}/a/b/c");
        using var bare = await GetAsync($"http://127.0.0.1:{port}/a/b");
        using var none = await GetAsync($"http://127.0.0.1:{port}/z");

        await Assert.That(await a.Content.ReadAsStringAsync()).IsEqualTo("A");
        await Assert.That(await b.Content.ReadAsStringAsync()).IsEqualTo("B");
        await Assert.That(await bare.Content.ReadAsStringAsync()).IsEqualTo("B");
        await Assert.That(none.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await none.Content.ReadAsStringAsync()).IsEqualTo("Nothing is listening at /z.");
    }

    [Test]
    public async Task Listen_takes_https_with_a_certificate()
    {
        using var files = new TemporaryFolder();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var certificatePath = files.Write("server.pem", certificate.ExportCertificatePem());
        var keyPath = files.Write("server.key", key.ExportPkcs8PrivateKeyPem());

        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "https://127.0.0.1:{{port}}/" certificate='{{certificatePath}}' key='{{keyPath}}'
                Respond "secure"
            """);

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented?.Thumbprint == certificate.Thumbprint,
        };
        using var client = new HttpClient(handler) { Timeout = Wait.Limit };
        var deadline = DateTime.UtcNow + Wait.Limit;
        string answer;
        while (true)
        {
            try
            {
                answer = await client.GetStringAsync($"https://127.0.0.1:{port}/");
                break;
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }

        await Assert.That(answer).IsEqualTo("secure");
    }

    [Test]
    public async Task An_https_address_needs_a_certificate()
    {
        var run = await TestRun.Start("""
            Listen "https://127.0.0.1:8443/"
                Respond
            """, run: false);

        await Assert.That(run.Diagnostics).Contains("HTTP needs a certificate for an https:// address, as in certificate=\"server.pem\"");
    }

    [Test]
    public async Task Respond_without_a_value_sends_the_request_body_back()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{port}}/"
                Respond
            """);

        using var response = await PostAsync($"http://127.0.0.1:{port}/", "echo me");

        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("echo me");
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("text/plain");
    }

    [Test]
    public async Task A_body_over_the_limit_is_refused_with_413()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{port}}/" max-size=4
                Respond "too late"
            """);

        using var response = await PostAsync($"http://127.0.0.1:{port}/", "far too long");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Wait.Until(() => !running.Run.Unhandled.IsEmpty);
        await Assert.That(running.Run.Unhandled.Single().Error).StartsWith("Respond had no request to answer with this error: A request to ");
        await Assert.That(running.Run.Unhandled.Single().Error).EndsWith("sent more than 4 bytes, so it was refused with 413.");
    }

    [Test]
    public async Task A_streamed_request_body_goes_straight_back_out()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{port}}/" stream=true
                Respond
            """);
        var big = new string('x', 300_000);

        using var response = await PostAsync($"http://127.0.0.1:{port}/", big);

        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(big);
    }

    [Test]
    public async Task Fetch_can_stream_a_response_through_to_a_client()
    {
        await using var upstream = new TestServer(context => TestServer.Reply(context, 200, "from upstream", "text/csv"));
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{port}}/"
                Fetch "{{upstream.Url}}/data" stream=true
                    Respond
            """);

        using var response = await GetAsync($"http://127.0.0.1:{port}/");

        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("from upstream");
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("text/csv");
    }

    [Test]
    public async Task Answering_a_request_twice_is_an_error()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{port}}/"
                Respond "one"
                Respond "two"
            """);

        using var response = await GetAsync($"http://127.0.0.1:{port}/");
        await Wait.Until(() => !running.Run.Unhandled.IsEmpty);

        var answer = await response.Content.ReadAsStringAsync();
        await Assert.That(answer is "one" or "two").IsTrue();
        await Assert.That(running.Run.Unhandled.Single().Error).Contains("has already been answered, so Respond couldn't answer it again.");
    }

    [Test]
    public async Task Listen_and_Respond_talk_over_tcp()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "tcp://127.0.0.1:{{port}}"
                Respond "you said {msg.Text}"
            """);

        using var client = await ConnectAsync(port);
        using var reader = new StreamReader(client.GetStream());
        await client.GetStream().WriteAsync("hi\r\nthere\n"u8.ToArray());

        await Assert.That(await Wait.For(reader.ReadLineAsync())).IsEqualTo("you said hi");
        await Assert.That(await Wait.For(reader.ReadLineAsync())).IsEqualTo("you said there");
    }

    [Test]
    public async Task Respond_final_closes_a_tcp_connection()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "tcp://127.0.0.1:{{port}}"
                Respond "bye" final=true
            """);

        using var client = await ConnectAsync(port);
        using var reader = new StreamReader(client.GetStream());
        await client.GetStream().WriteAsync("anything\n"u8.ToArray());

        await Assert.That(await Wait.For(reader.ReadLineAsync())).IsEqualTo("bye");
        await Assert.That(await Wait.For(reader.ReadLineAsync())).IsNull();
    }

    [Test]
    public async Task Listen_tells_of_tcp_clients_connecting_and_leaving()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "tcp://127.0.0.1:{{port}}" connections=true
                Where msg is TcpConnected
                    Respond "Welcome"
                Where msg is TcpMessage
                    Respond "you said {msg.Text}"
                Where msg is TcpDisconnected
                    Sink
            """);

        long connection;
        using (var client = await ConnectAsync(port))
        {
            using var reader = new StreamReader(client.GetStream());
            await Assert.That(await Wait.For(reader.ReadLineAsync())).IsEqualTo("Welcome");
            await client.GetStream().WriteAsync("hi\n"u8.ToArray());
            await Assert.That(await Wait.For(reader.ReadLineAsync())).IsEqualTo("you said hi");
            connection = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        }

        await Wait.Until(() => running.Run.At("Sink").Count > 0);
        var left = (TcpDisconnected)running.Run.At("Sink").Single();
        await Assert.That(left.Reason).IsEqualTo("The client closed the connection.");
        await Assert.That(left.RemoteAddress).EndsWith($":{connection}");
    }

    [Test]
    public async Task Tcp_messages_can_end_with_a_marker()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "tcp://127.0.0.1:{{port}}" end="\r\n.\r\n"
                Respond "got {msg.Text}"
            """);

        using var client = await ConnectAsync(port);
        var stream = client.GetStream();
        await stream.WriteAsync("first\r\nline\r\n.\r\nsecond\r\n.\r\n"u8.ToArray());

        // Respond ends its own messages the same way unless told otherwise.
        var expected = "got first\r\nline\r\n.\r\ngot second\r\n.\r\n";
        await Assert.That(await Wait.For(ReadAsync(stream, expected.Length))).IsEqualTo(expected);
    }

    [Test]
    public async Task Tcp_messages_can_have_a_fixed_length()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "tcp://127.0.0.1:{{port}}" length=4
                Respond "{msg.Text}!" end="\n"
            """);

        using var client = await ConnectAsync(port);
        using var reader = new StreamReader(client.GetStream());
        await client.GetStream().WriteAsync("abcdefgh"u8.ToArray());

        await Assert.That(await Wait.For(reader.ReadLineAsync())).IsEqualTo("abcd!");
        await Assert.That(await Wait.For(reader.ReadLineAsync())).IsEqualTo("efgh!");
    }

    [Test]
    public async Task A_fixed_length_message_cut_short_is_an_error()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "tcp://127.0.0.1:{{port}}" length=4
                Sink
            """);

        using (var client = await ConnectAsync(port))
            await client.GetStream().WriteAsync("abcdef"u8.ToArray());

        await Wait.Until(() => running.Run.At("Sink").Count == 2);
        var received = running.Run.At("Sink");
        await Assert.That(((TcpMessage)received[0]).Text).IsEqualTo("abcd");
        await Assert.That(((ErrorMessage)received[1]).Error).EndsWith(" closed its connection 2 bytes into a 4-byte message, so that message was dropped.");
    }

    [Test]
    public async Task Listen_tcp_takes_end_or_length_but_not_both()
    {
        var run = await TestRun.Start("""
            Listen "tcp://127.0.0.1:7000" end="\n" length=4
                Respond
            """, run: false);

        await Assert.That(run.Diagnostics).Contains("TCP takes end or length to tell where a message ends, not both");
    }

    [Test]
    public async Task Connection_messages_are_only_checked_when_they_are_turned_on()
    {
        const string pipeline = """
            Listen "tcp://127.0.0.1:7000"{0}
                Extract "$.x"
            """;

        var without = await TestRun.Start(string.Format(CultureInfo.InvariantCulture, pipeline, ""), run: false);
        var with = await TestRun.Start(string.Format(CultureInfo.InvariantCulture, pipeline, " connections=true"), run: false);

        await Assert.That(without.Diagnostics).IsEmpty();
        await Assert.That(with.Diagnostics).Contains("TcpConnected can reach Extract, but Extract doesn't take it.");
    }

    [Test]
    public async Task Listen_refuses_an_address_none_of_its_inflections_can_use()
    {
        var run = await TestRun.Start("""
            Listen "ftp://example.com"
                Respond
            """, run: false);

        await Assert.That(run.Diagnostics).Contains(
            "(1,8): error: None of Listen's inflections can do this: HTTP needs an http:// or https:// address, not ftp://example.com; TCP needs a tcp:// address with a port, not ftp://example.com.");
    }

    [Test]
    public async Task Respond_needs_a_message_from_a_request()
    {
        var run = await TestRun.Start("""
            Interval 10ms count=1
                Respond "hello"
            """);

        await Assert.That(run.Unhandled.Single().Error).IsEqualTo("Respond needs a message that came from a request, through Listen, but this one didn't.");
    }
}
