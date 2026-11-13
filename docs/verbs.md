# Verbs and inflections

The verbs Meeplang has so far, as of 12 November 2026, from the `[Verb]` attributes in `src/Meep2.Modules`. A verb with more than one inflection has a class for each. The runtime picks one by the message's type or by the parameters (usually the address's scheme), and `Verb.Inflection` names one directly, as in `Fetch.HTTP`.

Parameters are written in kebab case (`max-size`, `save-every`). The positional parameter, written straight after the verb without a name, is shown in the Positional column.

## Summary

| Verb | Inflections | Kind |
|---|---|---|
| Interval | — | Source |
| Read | — | Source |
| Listen | HTTP, TCP, WebSocket | Source |
| Fetch | HTTP, File | Inflection |
| Extract | — | Inflection |
| Enumerate | — | Inflection |
| Where | — | Filter (emits its input) |
| Tap | — | Emits its input |
| Batch | — | Inflection |
| Zip | — | Inflection |
| Distribute | — | Routes its input |
| Learn | — | Emits its input |
| Classify | — | Inflection |
| Forget | — | Emits its input |
| WriteLine | — | Passthrough |
| Save | — | Passthrough |
| Send | Reply, Broadcast, HTTP, TCP, WebSocket | Passthrough |

17 verbs, 25 inflections in all.

## Sources

### Interval
Emits a `Tick` straight away, then every period, until stopped or until it has emitted `count` ticks. Can be reconfigured while running.

- Positional: `period`
- Parameters: `count`, `count-start`, `payload`
- Example: `Interval 500ms count=3`

### Read
Reads a text file once and emits it as `FileText`.

- Positional: `path`
- Example: `Read "~/notes.txt"`

### Listen
Receives messages from the network, each carrying a lease so `Send` can reply. Chosen by the address's scheme. All three can be reconfigured while running.

| Inflection | Addresses | Emits | Parameters |
|---|---|---|---|
| HTTP | `http://`, `https://` | `HttpRequest` | `certificate`, `key`, `max-size`, `stream`, `default-status`, `default-reply` |
| TCP | `tcp://` | `TcpMessage`; `TcpConnected`/`TcpDisconnected` with `connections` | `end`, `length`, `connections`, `max-size` |
| WebSocket | `ws://`, `wss://` | `WebSocketText`, `WebSocketBinary`; `WebSocketConnected`/`WebSocketDisconnected` with `connections` | `certificate`, `key`, `origins`, `connections`, `max-size`, `ping`, `idle-timeout`, `max-queued` |

- Positional: `url`
- Example: `Listen "ws://0.0.0.0:8080/chat/" as Room`

## Getting and reshaping data

### Fetch
Fetches something for each message it receives.

| Inflection | Addresses | Emits | Parameters |
|---|---|---|---|
| HTTP | `http://`, `https://` (GET) | `HttpResponse` | `timeout`, `stream`, `max-size` |
| File | file paths, `file://` | `FileText` | `timeout`, `max-size` |

- Positional: `url`
- Example: `Fetch "https://api.example.com/stops/{msg.StopId}"`

### Extract
Reads a message's text as JSON and emits a `JsonMessage` for each value a JSONPath query matches.

- Positional: `path`
- Example: `Extract "$.departures[*]"`

### Enumerate
Emits an item from a book in the library for each message it receives, as a `BookItem`, or every item with `all=true`. Without `index` it steps through the book in order; the index counts from 0 and wraps round both ways. Book names are matched ignoring case. Built-in books: Lorem Ipsum, Common Passwords, Common Last Names, Countries, ISO 3166 Countries, ISO 3166 Codes, US States, US Abbreviations, Reindeer, Encodings. Plugins add more with `[LibraryBook]`. With `all=true`, the loader warns if a module after it has too small a buffer for the book.

- Positional: `book`
- Parameters: `index`, `all`
- Example: `Enumerate "Countries" index=msg.Count`

## Flow

### Where
Passes on only the messages a condition is true for. Messages the condition doesn't fit skip it.

- Positional: `condition`
- Example: `Where msg.minutesAway < 30`

### Tap
Receives everything another named module emits, as if indented under it.

- Positional: `from` (a module name)
- Example: `Tap Schedule @errors`

### Batch
Gathers messages into a `Batched` message: so many at a time, or whatever came within a while of the first.

- Positional: `size`
- Parameters: `within`
- Example: `Batch 100 within=1s`

### Zip
Pairs a message from its first input with one from its second, as a `Zipped` message, in order or by a shared key.

- Parameters: `by`, `limit`, `within`
- Example: `Zip by msg.OrderId`

### Distribute
Spreads messages across copies of the branch below it; messages with the same key take the same copy.

- Positional: `copies`
- Parameters: `by`
- Example: `Distribute 4 by msg.CustomerId`

## Classification

### Learn
Teaches a named naive Bayes tree that each message belongs to a class, then passes it on.

- Positional: `tree`
- Parameters: `class` (required), `text`, `file`, `save-every`
- Example: `Learn "topics" class="Science"`

### Classify
Emits a `ClassifiedMessage` with the class and score a named tree gives each message.

- Positional: `tree`
- Parameters: `text`, `file`, `min-training`, `against`
- Example: `Classify "mail" against="Ham"`

### Forget
Makes a named tree forget everything, or one class, then passes the message on.

- Positional: `tree`
- Parameters: `class`, `file`
- Example: `Forget "topics" class="Science"`

## Output

### WriteLine
Writes a line for each message to standard output.

- Positional: `text`
- Example: `WriteLine "{msg.route} in {msg.minutesAway} min"`

### Save
Saves each message's text to a file, appending by default.

- Positional: `path`
- Parameters: `mode` (`Append` or `Replace`)
- Example: `Save "~/bus/{msg.Created:yyyy-MM-dd}.jsonl"`

### Send
Sends messages on. Every Send is a passthrough: messages carry on downstream before the send has finished. Chosen by whether there's an address, and its scheme.

| Inflection | Positional | Emits | Parameters |
|---|---|---|---|
| Reply | none: replies through the message's lease | — | `text`, `status`, `content-type`, `error-reply`, `error-status`, `details`, `end`, `final` |
| Broadcast | `to`: a Listen.WebSocket's name | — | `text` |
| HTTP | `url`: `http://`, `https://` (POST) | `HttpResponse` | `text`, `content-type`, `timeout`, `max-size` |
| TCP | `url`: `tcp://` | `TcpMessage`; `TcpConnected`/`TcpDisconnected` with `connections` | `text`, `end`, `length`, `timeout`, `tries`, `retry-delay`, `connections`, `max-size` |
| WebSocket | `url`: `ws://`, `wss://` | `WebSocketText`, `WebSocketBinary`; `WebSocketConnected`/`WebSocketDisconnected` with `connections` | `text`, `timeout`, `tries`, `retry-delay`, `connections`, `max-size`, `ping`, `max-queued` |

- Examples: `Send text="Hello"`, `Send "Room"`, `Send "https://api.example.com/claims"`

## Not counted here

- `Parse`, `Build` and `Host` in `src/Meep2.Meeplang/Hosting/HostingModules.cs` are modules the host uses to run pipelines, not verbs written in Meeplang.
- `Shout` in `tests/Meep2.TestPlugin` is a test plugin's verb.
