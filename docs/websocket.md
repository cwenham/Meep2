# WebSockets for Listen and Send

Written 11 November 2026, and built on 12 November. Listen has a WebSocket inflection, and Send is a new verb for sending messages anywhere: back to where a message came from (replacing Respond), to every client of a Listen.WebSocket, or to a WebSocket, HTTP or TCP address. This covers what was built, the decisions I made while building it, and questions numbered from 148 with your answers. How the answers were applied is at the end.

## What's built

```
// A chat room: every message from a client goes to everyone connected.
Listen "ws://0.0.0.0:8080/chat/" as Room
    Send "Room" text="{msg.Connection}: {msg.Text}"

// Watching a feed on someone else's server, and answering its pings.
Interval 1m
    Send "wss://feed.example.com/prices" text='{"subscribe": "BTC"}'
        Where msg is WebSocketText
            Where msg.Text == "ping"
                Send text="pong"
            WriteLine "{msg.Text}"

// A web greeting, as Respond used to write it.
Listen "http://127.0.0.1:8080/hello/"
    Send text="Hello, {msg.Query['name']}!"
```

- **`Listen "ws://…"`** (or `wss://…` with `certificate`) accepts WebSocket connections. Each message that arrives is emitted as a `WebSocketText` or `WebSocketBinary`, carrying a lease on its connection. Both have `Url`, `RemoteAddress` and `Connection`.
  - It shares Listen.HTTP's Kestrel server, so a `ws://` and an `http://` Listen can take the same address, port and path.
  - It only takes connections from pages on its own host unless `origins="https://a.example, …"` or `origins="*"` says otherwise. Clients that aren't browsers don't send an `Origin`, so they're always taken.
  - It pings each client every 30 seconds (`ping=…`, or `ping=0s` to stop), and closes connections that stop answering. `idle-timeout=…` also closes connections that have been quiet that long.
  - A message over `max-size` (1 MiB unless set) closes its connection with code 1009.
  - With `connections=true`, it also emits `WebSocketConnected`, with the `Path`, `Query` and `Headers` the client connected with, and `WebSocketDisconnected`, with a `Reason` and `CloseStatus`.
  - When the pipeline stops, each connection closes with code 1001 once the messages it has in the pipeline have finished.
- **`Send` without an address** replies to where a message came from: an HTTP request, or a TCP or WebSocket connection, including one Send opened. It's Respond under a new name. `Respond "Hello"` is now `Send text="Hello"`, and bare `Respond` is now bare `Send`; every other parameter is the same.
- **`Send "Room"`** sends to every client connected to the Listen.WebSocket named `Room` in the same pipeline.
- **`Send "ws://…"`** sends to a WebSocket server, and emits what the server sends back, carrying a lease on the connection.
  - One connection to each address is shared by every Send in the process.
  - Connecting is tried `tries` times (3), `retry-delay` apart (1 second), with the first message and again whenever the connection drops.
  - A message not sent within `timeout` (30 seconds) becomes an error whose parent is the message.
  - `connections=true` emits `WebSocketConnected` and `WebSocketDisconnected` here too.
- **`Send "tcp://…"`** does the same over TCP: lines, or `end` or `length` as on Listen.TCP, emitting `TcpMessage`, and `TcpConnected` and `TcpDisconnected` with `connections=true`.
- **`Send "https://…"`** POSTs each message, and emits the response as an `HttpResponse`, or an `HttpErrorMessage` for a status that isn't a success, as Fetch does for GET. The body is `text`, or the message itself; `content-type` sets its media type.
- **Every Send passes messages on straight away** (it's a passthrough module), and sends on its own thread. Modules after it shouldn't expect the sending to have finished.
- **Text or binary is chosen by the message.** Given `text` goes as text, and so does any message with text. A `WebSocketBinary`, or a body whose media type isn't text of some kind, goes as binary.

The modules are in [src/Meep2.Modules/Send.cs](../src/Meep2.Modules/Send.cs), [WebSocketListen.cs](../src/Meep2.Modules/WebSocketListen.cs), [WebSockets.cs](../src/Meep2.Modules/WebSockets.cs) and [Clients.cs](../src/Meep2.Modules/Clients.cs). The tests are in [tests/Meep2.Tests/Meeplang/WebSocketTests.cs](../tests/Meep2.Tests/Meeplang/WebSocketTests.cs), and [examples/server.meep](../examples/server.meep) now has a chat room.

## Decisions I made while building

- **Send's inflections are Reply, Broadcast, WebSocket, HTTP and TCP**, chosen by what follows `Send`:
  - nothing: Reply
  - a name: Broadcast
  - a `ws://`, `http(s)://` or `tcp://` address: the inflection for it

  As with other inflections, a name used by only one verb works on its own, so `Reply text="hi"` and `Broadcast "Room"` also work.
- **Two additions to the SDK**, both needed by Send and open to plugins:
  - **`ModuleContext.Outlet`** lets a module that handles messages emit at any time, from any thread, as a source can. Send uses it for what servers send back. It stops emitting once the pipeline has finished, so it never keeps a pipeline running.
  - **`ModuleContext.Shared`** is a store each pipeline's modules share by name. Listen.WebSocket puts its connections there, under its `as` name, for `Send "Room"` to find.
- **Send leaves errors alone, except when replying.** Sending an error's text to someone else's server or to a chat room seemed wrong. But a Send at the end of a branch would otherwise count as handling every error it took, and errors would vanish without a trace. So there's also a new **`ModuleContext.LeftAlone`**: an error a module leaves alone isn't handled by it, and still reaches the error policy. Replies still answer errors with `error-reply`, as Respond did.
- **Messages dropped for a slow client are logged as answered errors** (Q153), the way Respond's answered errors are. They show in the log, but don't count towards stopping the pipeline, so slow clients can't stop a server.
- **What a server sends carries a lease on Send's connection**, so a Send without an address answers the server, as with the pings in the example. `final=true` on such a reply closes the shared connection until the next message is sent.
- **Send.HTTP doesn't try again**, unlike the WebSocket and TCP inflections, since `tries` was about reconnecting. A POST that fails becomes an error straight away.
- **A Listen.WebSocket without `as` can't be broadcast to.** It still takes connections, and replies still work.
- **Kestrel's own pings** do the pinging, on both ends, since .NET can now close a connection whose pings go unanswered.

## Questions

### Listen.WebSocket

148. A WebSocket message is either text or binary. Should there be one `WebSocketMessage` with `Text` (null for binary) and `Bytes`, or two message types, `WebSocketText` and `WebSocketBinary`, so `Where` and typed modules can tell them apart?

     **Answer:** Separate types for text and binary.

149. Should an `http://` Listen and a `ws://` Listen be allowed on the same address, port and path? Upgrade requests would go to the WebSocket Listen and the rest to HTTP, as a browser app that serves its page and its socket from `/app/` would want. Or should each path belong to one or the other, with a clash being a load error?

     **Answer:** Allow on the same address, but please advise if this leads to conflicts and complications.

150. Listen.TCP emits `TcpConnected` and `TcpDisconnected` only with `connections=true`. Should Listen.WebSocket do the same with `WebSocketConnected` and `WebSocketDisconnected`? Should the connected message carry the upgrade request's path, query and headers, such as a token in the query or a cookie?

     **Answer:** Yes.

151. Browsers send cookies with a WebSocket from any site, so a page elsewhere could open one as the user (cross-site WebSocket hijacking). Should Listen accept only connections whose `Origin` matches its own host unless `origins="https://a.example, https://b.example"` (or `origins="*"`) says otherwise? Or should it accept every origin, as Listen.HTTP accepts every request?

     **Answer:** Only accept connections whose `Origin` matches its own host, unless exceptions are made in a parameter specified in Meeplang.

152. Respond only reaches the connection a message came from. Chat rooms and live dashboards need to send to every client. Some ways to do it:
     - (a) `Listen … as Room`, then `Send "Room"` sends to every connection that Listen has open, as in the example above.
     - (b) `Respond all=true` sends to every connection on the Listen the message came from. This only works for messages that came from it.
     - (c) Nothing for now: broadcasting waits.

     **Answer:** A.

153. A slow client can make writes pile up. Should each connection have a limit on unsent messages (say 256), and when it's reached should Respond (a) wait, holding up the pipeline, (b) drop the message and log it, or (c) close that connection? (b) or (c) stops one slow browser from holding up everyone, which matters most with broadcasting.

     **Answer:** B.

154. Should Listen send pings to keep connections alive through proxies and find dead ones, every 30 seconds unless `ping` says otherwise? And should it close idle connections after `idle-timeout`, off unless set?

     **Answer:** Yes.

### Send

155. design-questions.md pairs Fetch with Send, without saying how they differ. Should Send be the verb for sending a message somewhere, with inflections chosen by address, and Fetch kept for getting something? That would mean:
     - `Send "ws://…"` sends a frame.
     - `Send "https://…"` POSTs the message as the body (`method=PUT` and others).
     - `Send "tcp://…"` writes to a connection.

     Or should Send be WebSocket-only for now, with HTTP and TCP left until they're wanted?

     **Answer:** Send should do as you say in the examples. For HTTP verbs `Send` would be POST, `Fetch` would be GET, and verbs to come in the future would be `Update` for PUT and `Delete` for DELETE (with an inflection for files). Other inflections in the future would cover database operations.

156. Send.WebSocket needs a connection, and the address is worked out for each message, so it could name a different server each time. Should Send keep one connection open per address, shared by every Send in the process that names it, and reconnect when it drops? Or should each Send module keep connections of its own?

     **Answer:** Keep one connection open per address and share it with any other Send in the process, reconnecting when needed.

157. What should happen to messages that arrive while Send.WebSocket is reconnecting? It could (a) wait for the connection, holding up the pipeline, with a `timeout` after which the message becomes an error, (b) become errors straight away, or (c) be dropped. And should reconnecting back off (1s, 2s, 4s, up to a minute)?

     **Answer:** Preferably its reference would go into a queue for sending and pass down the pipeline immediately, so modules downstream should not expect the Send stage to have completed by the time it receives the message; this will be the pipeline operator's problem to deal with. Timeouts would prompt the creation of a new ErrorMessage, descended from the original message that may still be in flight, and the pipeline author is responsible for tapping these errors and dealing with them.

     Do not use a backoff algorithm for reconnection attempts, just keep it to 1s and give up and emit an error after 3 tries, unless a Send parameter changes this. 

158. A server can send messages back on the connection, often the reason for connecting at all, as with the feed in the example above. Should Send emit them downstream as `WebSocketMessage`s? If so, they could only come out under the Send that opened the connection. Or should Send only send, with receiving left to a separate verb, such as `Connect "wss://…"`, a source that opens a connection and emits what arrives?

     **Answer:** Send can emit a WebSocketMessage on connections or disconnections. A separate `Connect` verb would overcomplicate the language. `Connect` might be used in the future for other things, though.

159. If Send doesn't emit replies (Q158), should it pass each message on after sending it, as Respond does, or end its chain like WriteLine and Save?

     **Answer:** Pass each message on.

160. Should text and binary frames be chosen by the message: binary for a message with a body of bytes, text for everything else? Or should it be a parameter, `binary=true`, on both Send and Respond?

     **Answer:** Chosen by the message.

### Order

161. Should Listen.WebSocket and Respond come first, with Send after, or both together?

     **Answer:** This reveals that I made a mistake specifying both `Respond` and `Send` verbs, because I think they're really the same. `Respond` should be removed and its functionality moved as an inflection into `Send`. Then `Listen.WebSocket` and `Send` inflections together.


## After your answers

- **Q148:** `WebSocketText` and `WebSocketBinary`, so `Where msg is WebSocketBinary` works. `WebSocketBinary` is an `IHasBody`, so a Send without an address posts its bytes to an HTTP address or answers a request with them.
- **Q149:** Allowed. Complications I found, and how they're handled:
  - **Stopping:** a Listen waits for its open requests before it stops, and a WebSocket can stay open for hours. So stopping lets go of each connection, which closes with 1001 ("going away") once its messages have finished.
  - **Routing:** an upgrade request goes to the WebSocket Listen whose path fits best, and to an HTTP Listen if none fits. A plain request goes to an HTTP Listen, and gets 426 ("upgrade required") if only a WebSocket Listen takes its path.
  - **Certificates:** a `ws://` Listen shares a port with `http://` ones, and a `wss://` Listen with `https://` ones, on the same certificate, as two HTTPS Listens already must.
- **Q150:** `connections=true`, with the path, query and headers on `WebSocketConnected`.
- **Q151:** A page on Listen's own host connects. Others need `origins`, and are refused with 403 otherwise. Each refusal is logged as an answered error, so it can't stop the server.
- **Q152:** `Send "Room"`, Send's Broadcast inflection.
- **Q153:** Each connection to Listen queues up to `max-queued` messages (256). Past that, messages to it are dropped and logged.
- **Q154:** `ping=30s` unless set; `idle-timeout` is off unless set.
- **Q155:** Send is POST, with WebSocket and TCP inflections too, and Send.Reply replaces Respond. `Update` (PUT) and `Delete` are left for later.
- **Q156:** One connection per address, shared across the process, and closed once no Send uses it. Background reconnects use the tries and delay of the Send that opened it.
- **Q157:** Messages pass on straight away, and Send works through them in order on its own thread. A message that can't be sent within `timeout` becomes an error whose parent is the message. Connecting is tried 3 times, 1 second apart, without backoff. `tries` and `retry-delay` change that.
- **Q158:** Send emits what the server sends, and `WebSocketConnected` and `WebSocketDisconnected` with `connections=true`. If the connection drops and reconnecting fails, that's logged as an error too.
- **Q159:** Every Send passes each message on.
- **Q160:** Chosen by the message, as described in "What's built".
- **Q161:** Respond is gone, and Send without an address does what it did. The examples and tests use `Send text=…` now. Listen.WebSocket and every Send inflection were built together.
