# Step 5: leases, Listen and Respond

Written 8 November 2026. This covers what step 5 built, the decisions I made while building it, benchmark numbers, and questions numbered from 96 with answer lines.

## What's built

- **Leases** (Q9, Q48). A live resource travels down the pipeline as a lease, such as a request waiting for its reply, or an open connection.
  - A message refers to the lease, and the runtime owns it.
  - Messages made from a leased message inherit the lease.
  - The runtime counts the messages in flight that carry it, and closes it when the last one has left the pipeline.
- **Listen** has two inflections, chosen by the address's scheme when the pipeline loads:
  - `Listen "http://0.0.0.0:8080/quote/"` emits each request as an `HttpRequest`. It runs on Kestrel, and takes `https://` addresses with a certificate (Q96).
  - `Listen "tcp://0.0.0.0:7000"` emits each message that arrives on a connection as a `TcpMessage`: a line, or what comes before an end marker, or a fixed number of bytes. With `connections=true` it also emits `TcpConnected` and `TcpDisconnected` (Q97).
- **Respond** answers through the lease: an HTTP response, or text on a TCP connection.
- **Default replies** (Q50). A request that nothing answers gets one once it, and every message made from it, has left the pipeline: 500 unless set otherwise.
- **Bodies** (Q51) are read into memory, up to `max-size`. With `stream=true`, a body is left unread, and one module can read it. That works for request bodies on Listen and response bodies on Fetch, and Respond passes a streamed body straight through, so a pipeline can work as a proxy.
- **Example:** `examples/server.meep` greets you over HTTP, and welcomes you and echoes over TCP. I tried it with `curl` and a raw socket, and Ctrl+C stopped it in 0.02 s with a connection still open.
- **Tests:** 276 TUnit tests, 34 of them new. The server tests use real HTTP, HTTPS and TCP connections. The suite passed 13 runs in a row before your answers, and 8 after. Breaking lease closing, default replies, error replies and conditional message types on purpose made their tests fail, and the code is restored.

## In Meeplang

```
+ Listen "http://0.0.0.0:8080/claims/spam/"
+ Listen "http://0.0.0.0:8080/claims/copyright/"
WriteLine "Claim filed at {msg.Url}"
    Respond "Thank you for your claim. It will be processed in the next 4–5 business days." status=202

Listen "http://0.0.0.0:8080/proxy/" stream=true default-status=503
    Fetch "https://upstream.example{msg.Path}" stream=true
        Respond

Listen "https://0.0.0.0:8443/" certificate="server.pem" key="server.key"
    Respond "Hello over HTTPS"

Listen "tcp://0.0.0.0:7000" connections=true
    Where msg is TcpConnected
        Respond "Welcome."
    Where msg is TcpMessage
        Respond "You said: {msg.Text}"

Listen "tcp://0.0.0.0:2525" end="\r\n.\r\n"
    Respond "250 OK" end="\r\n"
```

- **Listen.HTTP's parameters:**
  - `certificate`, and `key` if the key is in a file of its own: for an `https://` address.
  - `max-size`: the largest body read into memory, 16 MiB by default. A larger body gets a 413.
  - `stream=true`: leave bodies unread.
  - `default-status` and `default-reply`: the reply for a request nothing answers.
- **Listen.TCP's parameters:**
  - `end`: the text that ends each message, such as `"\r\n.\r\n"`. Without it, each line is a message.
  - `length`: the size of every message in bytes, for a protocol whose messages are all the same length.
  - `connections=true`: also emit `TcpConnected` and `TcpDisconnected`.
  - `max-size`: the longest message, 1 MiB by default.
- **Respond:**
  - Its value is what to send, and without one it sends the message itself.
  - `status` and `content-type` set the HTTP status and media type.
  - `error-reply` and `error-status`: the answer to an error, "Something went wrong." and 500 by default. `details=true` sends everything known about the error instead (Q98).
  - `end`: what to write after the text on a TCP connection. Without it, what ends the connection's own messages.
  - `final=true` closes a TCP connection after writing.

## Decisions I made while building

1. **Leases are built on step 2's tracking scopes,** the mechanism behind tracked emits and handled errors.
   - The base class moved into the SDK, so that `Lease` can be public there.
   - A module emits a message with a lease through `ModuleContext.Emit(message, lease)`, and finds a message's leases with `message.Leases`.
   - A lease's maker holds it until its first emit, so it can't close before anything carries it.
   - `Hold()` keeps a lease open while something other than a message needs it, as a TCP connection does while it's being read.
   - `CloseAsync()` closes a lease on purpose, and later writes then find it closed.
   - A lease that fails while closing is reported as an error from the module that emitted it.
2. **HTTP uses Kestrel,** ASP.NET Core's server (Q96, changed after your answer). Listens on the same address and port share one server, and each takes the requests to its own path and below.
3. **`HttpRequest`** has `Method`, `Url`, `Path`, `Query`, `Headers`, `ContentType`, `Body`, `Text` and `RemoteAddress`, so `msg.Query['name']` reads a query value.
4. **TCP:**
   - One `TcpMessage` for each line, with a `\r` before the `\n` dropped, unless `end` or `length` says otherwise (Q97).
   - A connection stays open while the client keeps it open, and after that until every message from it has left the pipeline.
   - A message over `max-size` is an error, and closes the connection.
   - With `connections=true`, `TcpConnected` and `TcpDisconnected` come and go with each client (Q97).
5. **Respond is one module, not one inflection each for HTTP and TCP.** Which one applies depends on the lease a message carries, and neither the message's type nor Respond's parameters say that. So Respond looks at the lease.
   - **Without a value,** Respond sends the message itself:
     - a request's or a fetched response's body, with its media type, streamed through if it was streamed
     - JSON as `application/json`
     - otherwise the message's text
   - **An error** is answered with status 500 and "Something went wrong.", or `error-reply` and `error-status` (Q98).
   - **An HTTP request** can only be answered once. A second Respond is an error.
   - **A TCP connection** can be written to any number of times. Writes from several branches queue up rather than interleave.
   - **Respond is a passthrough module,** like WriteLine and Save, so it never holds messages up.
   - **A message with no request behind it** is an error. If it was already an error, such as Listen's 413 below, Respond's error quotes it.
6. **Default reply:** "No module answered this request.", with status 500. `default-status` and `default-reply` change it.
7. **Stopping:** Listen stops taking new requests and connections, but keeps the open ones until their messages have left, within the pipeline's drain time. So requests in flight still get answered.
8. **A body over `max-size`** gets a 413 at once, and Listen emits an error, so the error policy logs it (Q99).
9. **Streamed bodies:**
   - A second module that tries to read one gets an error suggesting leaving out `stream=true`.
   - Fetch's File inflection refuses `stream=true` when the pipeline loads.
   - A streamed Fetch keeps its connection open with a lease until the body has been read and its messages have left.
10. **Q49:** caches don't hold leases. There's no Cache module yet, so there's nothing to build for that until there is.
11. **New in the SDK and modules:**
    - In the SDK: `Lease`, `IHasBody`, `ModuleContext.Emit(message, lease)`, `Message.Leases`, `[ForErrors]`, and `When` on `[Emits]`
    - Messages: `HttpRequest`, `TcpMessage`, `TcpConnected` and `TcpDisconnected`
    - Modules: `HttpListen`, `TcpListen` and `Respond`
    - Fetch: `stream`, and `IsStreamed` and `OpenBody()` on `HttpResponse`

## Benchmark numbers

Measured with BenchmarkDotNet's short run. This Mac was back to the speed it had in step 2, and the chain benchmarks matched step 2's numbers (0.83 µs for one module), so leases cost ordinary messages nothing. Times and allocations are per message, or per request. The HTTP rows were measured again after moving to Kestrel (Q96), with the lease rows as a check: they came out the same.

| What | Time | Allocated |
|---|---:|---:|
| Source → 4 modules → sink, no lease | 1.15 µs | 100 B |
| The same, every message carrying one shared lease | 1.52 µs | 136 B |
| The same, each message with a lease of its own | 1.75 µs | 560 B |
| An HTTP request and its answer, bare `HttpListener` | 70 µs | 11.2 KB |
| An HTTP request and its answer, bare Kestrel | 71 µs | 2.9 KB |
| An HTTP request and its answer, `Listen → Respond` on `HttpListener` (before your answers) | 82 µs | 13.6 KB |
| An HTTP request and its answer, `Listen → Respond` on Kestrel | 76 µs | 5.0 KB |

The HTTP times include the client, `HttpClient` in the same process, so most of each request is the client and the loopback network.

What they show:
- **Counting a lease costs about 0.07 µs a module,** because every module the message passes updates one shared count. Ordinary messages don't pay this.
- **A lease of its own costs about 0.6 µs and 460 bytes a message.** That's the lease, its count, and finishing it on another thread. For an HTTP request it's under 1% of the round trip.
- **Meep adds about 5.5 µs and 2.1 KB to an HTTP request** on Kestrel, compared with Kestrel answering directly. On `HttpListener` it added 8 µs and 2.4 KB.
- **Kestrel allocates far less than `HttpListener`:** a whole request through Meep now allocates 5.0 KB, less than half of what a bare `HttpListener` did on its own.
- **An early fix:** the first measurement allocated 94 KB a request, because Listen read every body through an 80 KB buffer, even when there was no body. Listen now reads a body into an array of its declared size, or through a pooled buffer when its size isn't declared.

## Questions

96. Listen uses .NET's `HttpListener`. It comes with .NET and is simple, but Microsoft calls it a legacy API, and Listen can't do HTTPS with it yet. Kestrel, ASP.NET Core's server, is faster and does HTTPS and HTTP/2, but it would bring ASP.NET Core into the core modules. Keep `HttpListener` for now, and move to Kestrel when HTTPS or speed calls for it?

    **Answer:** Move to Kestrel now.

97. Listen.TCP emits a message for each line. Should it also emit messages when a connection opens and closes? And should it take other ways of telling where a message ends, such as the end-of-message pattern from your Q9 story, or a length before each message?

    **Answer:** Yes, emit messages for open and close, accept EOM patterns as parameters. An EOM pattern doesn't mean the connection should be closed, because a protocol I had to support in the past continued to send more messages on the same connection. An optional message length parameter should be supported as well, where I understand this would be used for fixed-length message protocols.

98. Respond answers an error with status 500 and the error's description. That's handy for a tool, but it shows internal details, such as addresses and exception text, to whoever made the request. Should Respond send a plain "Something went wrong" instead, unless told to include the details?

    **Answer:** Yes, send a templatable response and only send full details if configured.

99. When a request body is over `max-size`, Listen sends a 413 and emits an error, so it shows up in the log. That error goes down the branch like any other, and if it reaches Respond, Respond reports that it had no request to answer. Would you rather Listen only sent the 413, perhaps with a notice, and kept the error out of the pipeline?

    **Answer:** It should emit an error message, there will be cases where the pipeline author will want to do something with it.

## After your answers

- **Q96: Listen.HTTP runs on Kestrel now.**
  - **How it's hosted:** each server is an empty ASP.NET Core host: Kestrel and nothing else, with no settings files, environment variables or logging of its own. So an `appsettings.json` in the folder you run from can't change it.
  - **Sharing a port:** Kestrel can't share a port between servers the way `HttpListener` did. So Listens on the same address and port share one server, and each takes the requests to its own path and below. The longest path that fits wins.
    - A request no Listen's path fits gets a 404, "Nothing is listening at /path."
    - A request to a Listen that's stopping gets a 503.
    - Two Listens with the same path on one port is an error.
  - **HTTPS:** an `https://` address takes `certificate`, a PEM file or a .pfx without a password, and `key` if the key is in a file of its own.
    - Listens sharing a port must use the same certificate.
    - Without a certificate, an `https://` address is a load error.
    - The core modules now depend on ASP.NET Core's shared framework, which comes with the .NET SDK and runtime. They're still AOT-compatible.
  - **Speed:** see the benchmark numbers above. Meep's own cost per request fell from 8 µs to 5.5 µs, and allocations from 13.6 KB to 5.0 KB a request.
- **Q97: TCP framing and connection messages.**
  - **`end="…"`** gives the text that ends each message, such as `"\r\n.\r\n"`. It isn't part of the message, and the connection stays open for more.
  - **`length=N`** makes every message N bytes. A connection that closes partway through a message is an error, and the part message is dropped.
  - `end` and `length` together is a load error. Without either, each line is a message, as before.
  - **Messages are now `TcpMessage`** (it was `TcpLine`), with `Text`, the bytes as `Body`, `RemoteAddress`, and `Connection`, a number that's the same for every message from one connection.
  - **Respond ends what it writes the way the connection's messages end:** a line break, Listen's `end`, or nothing with `length`. Respond's own `end` overrides that, as an SMTP-style server would need, where replies are lines but messages end with `\r\n.\r\n`.
  - **Strings take two new escapes:** `\0`, and `\uXXXX` for a character by its code, such as `\u0003`. That covers markers made of control characters. A backslash before anything else is still kept, so Windows paths are safe.
  - **`connections=true`** turns on `TcpConnected` and `TcpDisconnected`. They're off by default, because they'd reach every module after Listen, and a plain echo would echo them too.
    - `TcpConnected` carries the connection's lease, so Respond can greet a client before it sends anything.
    - `TcpDisconnected` has a `Reason`: the client closed it, it was closed from this end, it failed, or the pipeline stopped.
  - **Message types that depend on a parameter:** `[Emits(typeof(TcpConnected), When = "Connections")]` in the SDK tells the loader that Listen only emits these when `connections=true`. So the load checks only expect them when they're on.
- **Q98: Respond's answer to an error.**
  - By default it's "Something went wrong.", with status 500.
  - **`error-reply`** is a template worked out from the error, so `error-reply="Couldn't read that: {msg.Error}"` includes the description. **`error-status`** sets the status.
  - **`details=true`** sends everything known about the error instead: its description, and the exception behind it with its stack trace. It's meant for tools and testing.
  - **`[ForErrors]` is new in the SDK.** It marks a parameter worked out only for errors. A module's other parameters are then only for other messages, so `Respond "x is {msg.Text}"` no longer keeps errors out of Respond just because errors have no `Text`.
  - **A side effect:** an error that reaches Respond is answered there, and counts as handled, so the error policy doesn't log it. To log those errors as well, add a branch with `WriteLine` or `Save`. See Q100 in step 6.
- **Q99: kept as built.** A body over `max-size` still gets a 413, and Listen emits an error a pipeline can act on.

