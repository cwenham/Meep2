using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Meep2.Modules;
using Meep2.SDK.Messages;
using Meep2.Tests.Support;

namespace Meep2.Tests.Meeplang;

/// <summary>Listen.WebSocket, and Send's WebSocket, Broadcast, HTTP and TCP inflections (docs/websocket.md).</summary>
public class WebSocketTests
{
    // Connects a WebSocket client, trying again while Listen is still starting.
    private static async Task<ClientWebSocket> ConnectAsync(string url, string? origin = null)
    {
        var deadline = DateTime.UtcNow + Wait.Limit;
        while (true)
        {
            var client = new ClientWebSocket();
            if (origin is not null)
                client.Options.SetRequestHeader("Origin", origin);
            try
            {
                await client.ConnectAsync(new Uri(url), CancellationToken.None);
                return client;
            }
            catch (WebSocketException) when (DateTime.UtcNow < deadline)
            {
                client.Dispose();
                await Task.Delay(20);
            }
        }
    }

    // Waits until something answers at an address, as Send.HTTP doesn't try again.
    private static Task WaitForHttpAsync(string url) => Wait.Until(async () =>
    {
        try
        {
            using var http = new HttpClient();
            using var response = await http.GetAsync(url);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    });

    private static Task SendTextAsync(ClientWebSocket client, string text) =>
        client.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    // Reads one whole message, failing after Wait.Limit.
    private static async Task<(WebSocketMessageType Type, byte[] Bytes)> ReceiveAsync(ClientWebSocket client)
    {
        using var timeout = new CancellationTokenSource(Wait.Limit);
        var buffer = new byte[64 * 1024];
        var total = 0;
        while (true)
        {
            var result = await client.ReceiveAsync(buffer.AsMemory(total), timeout.Token);
            total += result.Count;
            if (result.MessageType == WebSocketMessageType.Close || result.EndOfMessage)
                return (result.MessageType, buffer[..total]);
        }
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket client)
    {
        var (type, bytes) = await ReceiveAsync(client);
        if (type != WebSocketMessageType.Text)
            throw new InvalidOperationException($"Expected text, but got {type}.");
        return Encoding.UTF8.GetString(bytes);
    }

    [Test]
    public async Task Listen_and_Send_reply_over_a_websocket()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "ws://127.0.0.1:{{port}}/echo/"
                Send text="you said {msg.Text}"
            """);

        using var client = await ConnectAsync($"ws://127.0.0.1:{port}/echo/");
        await SendTextAsync(client, "hi");
        await SendTextAsync(client, "there");

        await Assert.That(await ReceiveTextAsync(client)).IsEqualTo("you said hi");
        await Assert.That(await ReceiveTextAsync(client)).IsEqualTo("you said there");
    }

    [Test]
    public async Task Binary_messages_stay_binary()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "ws://127.0.0.1:{{port}}/"
                Send
            """);

        using var client = await ConnectAsync($"ws://127.0.0.1:{port}/");
        await client.SendAsync(new byte[] { 0, 1, 2, 255 }, WebSocketMessageType.Binary, true, CancellationToken.None);
        var (type, bytes) = await ReceiveAsync(client);

        await Assert.That(type).IsEqualTo(WebSocketMessageType.Binary);
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0, 1, 2, 255 });
    }

    [Test]
    public async Task Text_and_binary_messages_are_told_apart()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "ws://127.0.0.1:{{port}}/"
                Sink
            """);

        using var client = await ConnectAsync($"ws://127.0.0.1:{port}/");
        await SendTextAsync(client, "words");
        await client.SendAsync(new byte[] { 7 }, WebSocketMessageType.Binary, true, CancellationToken.None);

        await Wait.Until(() => running.Run.At("Sink").Count == 2);
        var received = running.Run.At("Sink");
        await Assert.That(((WebSocketText)received[0]).Text).IsEqualTo("words");
        await Assert.That(((WebSocketBinary)received[1]).Body.ToArray()).IsEquivalentTo(new byte[] { 7 });
        await Assert.That(((WebSocketText)received[0]).Connection).IsEqualTo(((WebSocketBinary)received[1]).Connection);
    }

    [Test]
    public async Task Send_to_a_listen_by_name_reaches_every_client()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "ws://127.0.0.1:{{port}}/chat/" as Room
                Send "Room" text="{msg.Text}!"
            """);

        using var ann = await ConnectAsync($"ws://127.0.0.1:{port}/chat/");
        using var bob = await ConnectAsync($"ws://127.0.0.1:{port}/chat/");
        await Task.Delay(100); // both connections are in the room before anyone speaks
        await SendTextAsync(ann, "hello");

        await Assert.That(await ReceiveTextAsync(ann)).IsEqualTo("hello!");
        await Assert.That(await ReceiveTextAsync(bob)).IsEqualTo("hello!");
    }

    [Test]
    public async Task Send_to_a_name_nothing_has_is_an_error()
    {
        var run = await TestRun.Start("""
            Numbers to=1
                Send "Nowhere"
            """);

        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(run.Unhandled.Single().Error).IsEqualTo("Send has nowhere called Nowhere to send to. Name a Listen.WebSocket with as Nowhere, in this pipeline.");
    }

    [Test]
    public async Task Http_and_websockets_share_a_path()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{port}}/app/"
                Send text="a page"
            Listen "ws://127.0.0.1:{{port}}/app/"
                Send text="a socket"
            Listen "ws://127.0.0.1:{{port}}/only-sockets/"
                Send text="a socket"
            """);

        using var client = await ConnectAsync($"ws://127.0.0.1:{port}/app/");
        await SendTextAsync(client, "hi");
        await Assert.That(await ReceiveTextAsync(client)).IsEqualTo("a socket");

        using var http = new HttpClient();
        await Assert.That(await http.GetStringAsync($"http://127.0.0.1:{port}/app/")).IsEqualTo("a page");
        using var refused = await http.GetAsync($"http://127.0.0.1:{port}/only-sockets/");
        await Assert.That((int)refused.StatusCode).IsEqualTo(426);
    }

    [Test]
    public async Task Pages_from_other_sites_are_refused()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "ws://127.0.0.1:{{port}}/strict/"
                Send text="ok"
            Listen "ws://127.0.0.1:{{port}}/open/" origins="https://friend.example"
                Send text="ok"
            """);

        using var own = await ConnectAsync($"ws://127.0.0.1:{port}/strict/", origin: $"http://127.0.0.1:{port}");
        await Assert.That(own.State).IsEqualTo(WebSocketState.Open);

        using var stranger = new ClientWebSocket();
        stranger.Options.SetRequestHeader("Origin", "https://evil.example");
        stranger.Options.CollectHttpResponseDetails = true;
        await Assert.That(async () => await stranger.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/strict/"), CancellationToken.None)).Throws<WebSocketException>();
        await Assert.That(stranger.HttpStatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        using var friend = await ConnectAsync($"ws://127.0.0.1:{port}/open/", origin: "https://friend.example");
        await Assert.That(friend.State).IsEqualTo(WebSocketState.Open);

        await Wait.Until(() => !running.Run.Unhandled.IsEmpty);
        await Assert.That(running.Run.Unhandled.Single().Error).Contains("pages from https://evil.example may not connect here");
    }

    [Test]
    public async Task Listen_tells_of_websocket_clients_connecting_and_leaving()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "ws://127.0.0.1:{{port}}/" connections=true
                Where msg is WebSocketConnected
                    Send text="Welcome, {msg.Query['name']}"
                Where msg is WebSocketDisconnected
                    Sink
            """);

        using (var client = await ConnectAsync($"ws://127.0.0.1:{port}/room?name=Ann"))
        {
            await Assert.That(await ReceiveTextAsync(client)).IsEqualTo("Welcome, Ann");
            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }

        await Wait.Until(() => running.Run.At("Sink").Count > 0);
        var left = (WebSocketDisconnected)running.Run.At("Sink").Single();
        await Assert.That(left.Reason).IsEqualTo("The client closed the connection.");
        await Assert.That(left.CloseStatus).IsEqualTo(1000);
    }

    [Test]
    public async Task A_message_too_long_closes_the_connection()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "ws://127.0.0.1:{{port}}/" max-size=4
                Send
            """);

        using var client = await ConnectAsync($"ws://127.0.0.1:{port}/");
        await SendTextAsync(client, "far too long");
        var (type, _) = await ReceiveAsync(client);

        await Assert.That(type).IsEqualTo(WebSocketMessageType.Close);
        await Assert.That(client.CloseStatus).IsEqualTo(WebSocketCloseStatus.MessageTooBig);
    }

    [Test]
    public async Task Idle_connections_are_closed()
    {
        var port = TestServer.FreePort();
        await using var running = TestRun.Begin($$"""
            Listen "ws://127.0.0.1:{{port}}/" idle-timeout=200ms
                Send
            """);

        using var client = await ConnectAsync($"ws://127.0.0.1:{port}/");
        var (type, _) = await ReceiveAsync(client);

        await Assert.That(type).IsEqualTo(WebSocketMessageType.Close);
        await Assert.That(client.CloseStatusDescription).IsEqualTo("The connection was idle.");
    }

    [Test]
    public async Task Stopping_closes_connections_as_going_away()
    {
        var port = TestServer.FreePort();
        var running = TestRun.Begin($$"""
            Listen "ws://127.0.0.1:{{port}}/"
                Send
            """);
        using var client = await ConnectAsync($"ws://127.0.0.1:{port}/");

        var stopping = running.DisposeAsync().AsTask();
        var (type, _) = await ReceiveAsync(client);
        await Wait.For(stopping);

        await Assert.That(type).IsEqualTo(WebSocketMessageType.Close);
        await Assert.That(client.CloseStatus).IsEqualTo(WebSocketCloseStatus.EndpointUnavailable);
    }

    [Test]
    public async Task Send_connects_to_a_websocket_and_emits_what_comes_back()
    {
        var port = TestServer.FreePort();
        await using var server = TestRun.Begin($$"""
            Listen "ws://127.0.0.1:{{port}}/echo/"
                Send text="echo {msg.Text}"
            """);
        await using var client = TestRun.Begin($$"""
            Interval 1h
            Interval 50ms count=2
                Send "ws://127.0.0.1:{{port}}/echo/" text="tick {msg.Count}" connections=true
                    Where msg is not Tick
                        Sink
            """);

        await Wait.Until(() => client.Run.At("Sink").OfType<WebSocketText>().Count() == 2);
        var received = client.Run.At("Sink");
        await Assert.That(received[0]).IsTypeOf<WebSocketConnected>();
        await Assert.That(Wait.Join(received.OfType<WebSocketText>().Select(t => t.Text))).IsEqualTo("echo tick 1,echo tick 2");
        await Assert.That(received.OfType<WebSocketText>().First().Url).IsEqualTo($"ws://127.0.0.1:{port}/echo/");
    }

    [Test]
    public async Task Send_replies_to_a_server_through_the_connection_it_opened()
    {
        var port = TestServer.FreePort();
        await using var server = TestRun.Begin($$"""
            Listen "ws://127.0.0.1:{{port}}/"
                Where msg.Text == "ping"
                    Send text="pong?"
                Where msg.Text != "ping"
                    Sink
            """);
        await using var client = TestRun.Begin($$"""
            Interval 1h
            Interval 50ms count=1
                Send "ws://127.0.0.1:{{port}}/" text="ping"
                    Where msg is WebSocketText
                        Send text="pong"
            """);

        await Wait.Until(() => server.Run.At("Sink").Count > 0);
        await Assert.That(((WebSocketText)server.Run.At("Sink").Single()).Text).IsEqualTo("pong");
    }

    [Test]
    public async Task Send_gives_up_after_its_tries_with_an_error_from_the_message()
    {
        var port = TestServer.FreePort();
        var run = await TestRun.Start($$"""
            Numbers to=1
                Send "ws://127.0.0.1:{{port}}/" tries=2 retry-delay=10ms
            """);

        var error = run.Unhandled.Single();
        await Assert.That(error.Error).StartsWith($"Couldn't send to ws://127.0.0.1:{port}/: couldn't connect after 2 tries");
        await Assert.That(error.Parents.Single()).IsTypeOf<Number>();
    }

    [Test]
    public async Task Send_passes_messages_on_and_leaves_errors_alone()
    {
        var port = TestServer.FreePort();
        await using var server = TestRun.Begin($$"""
            Listen "ws://127.0.0.1:{{port}}/"
                Sink
            """);
        var run = await TestRun.Start($$"""
            Numbers to=2
                Where 1 / (msg.Value - 2) < 0
                    Send "ws://127.0.0.1:{{port}}/"
            """);

        await Assert.That(run.Diagnostics).IsEmpty();
        await Wait.Until(() => server.Run.At("Sink").Count > 0);
        await Assert.That(((WebSocketText)server.Run.At("Sink").Single()).Text).Contains("Value = 1");
        await Assert.That(run.Unhandled.Count).IsEqualTo(1); // the error reached the policy, though Send was last
    }

    [Test]
    public async Task Send_posts_to_an_http_address_and_emits_the_response()
    {
        var port = TestServer.FreePort();
        await using var server = TestRun.Begin($$"""
            Listen "http://127.0.0.1:{{port}}/claims/"
                Send text="got {msg.Method} {msg.Text}" status=201
            """);
        await WaitForHttpAsync($"http://127.0.0.1:{port}/claims/");
        await using var client = TestRun.Begin($$"""
            Interval 1h
            Interval 50ms count=1
                Send "http://127.0.0.1:{{port}}/claims/" text='{"id":7}' content-type="application/json"
                    Where msg is HttpResponse
                        Sink
            """);

        await Wait.Until(() => client.Run.At("Sink").Count > 0);
        var response = (HttpResponse)client.Run.At("Sink").Single();
        await Assert.That(response.StatusCode).IsEqualTo(201);
        await Assert.That(response.Text).IsEqualTo("""got POST {"id":7}""");
    }

    [Test]
    public async Task Send_writes_to_a_tcp_address_and_emits_what_comes_back()
    {
        var port = TestServer.FreePort();
        await using var server = TestRun.Begin($$"""
            Listen "tcp://127.0.0.1:{{port}}"
                Send text="you said {msg.Text}"
            """);
        await using var client = TestRun.Begin($$"""
            Interval 1h
            Interval 50ms count=2
                Send "tcp://127.0.0.1:{{port}}" text="tick {msg.Count}"
                    Where msg is TcpMessage
                        Sink
            """);

        await Wait.Until(() => client.Run.At("Sink").Count == 2);
        await Assert.That(Wait.Join(client.Run.At("Sink").Cast<TcpMessage>().Select(m => m.Text))).IsEqualTo("you said tick 1,you said tick 2");
    }

    [Test]
    public async Task Sends_to_one_address_share_a_connection()
    {
        var port = TestServer.FreePort();
        await using var server = TestRun.Begin($$"""
            Listen "tcp://127.0.0.1:{{port}}" connections=true
                Where msg is TcpConnected
                    Sink
            """);
        await using var client = TestRun.Begin($$"""
            Interval 1h
            Interval 50ms count=2
                Send "tcp://127.0.0.1:{{port}}" text="a"
                Send "tcp://127.0.0.1:{{port}}" text="b"
            """);

        await Task.Delay(500);
        await Assert.That(server.Run.At("Sink").Count).IsEqualTo(1);
    }

    [Test]
    public async Task Send_chooses_its_inflection_by_address()
    {
        var run = await TestRun.Start("""
            Numbers to=1
                Send "ftp://example.com"
            """, run: false);

        await Assert.That(run.Diagnostics).Contains("None of Send's inflections can do this");
    }
}
