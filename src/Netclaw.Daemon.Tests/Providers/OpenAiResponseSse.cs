// -----------------------------------------------------------------------
// <copyright file="OpenAiResponseSse.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Providers.OpenAi;

namespace Netclaw.Daemon.Tests.Providers;

internal static class OpenAiResponseSse
{
    public static IChatClient Client(HttpMessageHandler handler, AuthMethod auth) =>
        Client(handler, auth, NullLoggerFactory.Instance, TimeProvider.System);

    public static IChatClient Client(HttpMessageHandler handler, AuthMethod auth, ILoggerFactory loggerFactory, TimeProvider timeProvider)
    {
        var plugin = new OpenAiProviderPlugin(new OpenAiDescriptor(new HttpClient()), loggerFactory,
            timeProvider) { HttpHandlerOverride = handler };
        return plugin.CreateChatClient(new ProviderEntry
        {
            Type = "openai",
            AuthMethod = auth,
            ApiKey = new SensitiveString("synthetic-key"),
            OAuthAccessToken = new SensitiveString("synthetic-token"),
            OAuthAccountId = new SensitiveString("synthetic-account"),
        }, new ModelReference { Provider = "synthetic", ModelId = "synthetic-model" });
    }

    public static HttpResponseMessage Response(Stream stream) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(stream)
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream") }
        }
    };

    public static string Text() => """
        event: response.created
        data: {"type":"response.created","sequence_number":0,"response":{"id":"resp-text","created_at":1793900000,"model":"synthetic-model","status":"in_progress","output":[]}}

        event: response.output_item.added
        data: {"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"id":"msg-text","type":"message","status":"in_progress","role":"assistant","content":[]}}

        event: response.output_text.delta
        data: {"type":"response.output_text.delta","sequence_number":2,"output_index":0,"item_id":"msg-text","content_index":0,"delta":"Synthetic final answer"}

        event: response.completed
        data: {"type":"response.completed","sequence_number":3,"response":{"id":"resp-text","created_at":1793900000,"model":"synthetic-model","status":"completed","output":[],"usage":{"input_tokens":5,"output_tokens":3,"total_tokens":8}}}


        """;

    public static string Call(string terminal = "response.completed", string arguments = """{"Name":"synthetic","_rationale":"Inspect synthetic data"}""")
    {
        var sequence = 0;
        var result = new StringBuilder();
        void Add(string type, object data) => result.Append("event: ").Append(type)
            .Append("\ndata: ").Append(JsonSerializer.Serialize(data)).Append("\n\n");
        object Item(string args, string status) => new
        {
            id = "fc-synthetic", type = "function_call", call_id = "call-synthetic", name = "load_tool", arguments = args, status
        };
        object ResponseBody(string status) => new
        {
            id = "resp-synthetic", @object = "response", created_at = 1_793_900_000, model = "synthetic-model", status,
            output = status == "in_progress" ? [] : new[] { Item(arguments, "completed") },
            usage = new { input_tokens = 5, output_tokens = 3, total_tokens = 8 },
            error = status == "failed" ? new { code = "server_error", message = "synthetic failure" } : null,
            incomplete_details = status == "incomplete" ? new { reason = "max_output_tokens" } : null
        };
        Add("response.created", new { type = "response.created", sequence_number = sequence++, response = ResponseBody("in_progress") });
        Add("response.output_item.added", new { type = "response.output_item.added", sequence_number = sequence++, output_index = 0, item = Item("", "in_progress") });
        foreach (var delta in new[] { arguments[..(arguments.Length / 2)], arguments[(arguments.Length / 2)..] })
            Add("response.function_call_arguments.delta", new { type = "response.function_call_arguments.delta", sequence_number = sequence++, output_index = 0, item_id = "fc-synthetic", delta });
        Add("response.function_call_arguments.done", new { type = "response.function_call_arguments.done", sequence_number = sequence++, output_index = 0, item_id = "fc-synthetic", arguments });
        Add("response.output_item.done", new { type = "response.output_item.done", sequence_number = sequence++, output_index = 0, item = Item(arguments, "completed") });
        if (terminal.Length > 0)
        {
            Add(terminal, new
            {
                type = terminal, sequence_number = sequence,
                response = ResponseBody(terminal == "response.failed" ? "failed" : terminal == "response.incomplete" ? "incomplete" : "completed")
            });
        }
        return result.ToString();
    }
}

internal sealed class ChannelSseStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<bool> _reads = Channel.CreateUnbounded<bool>();
    private ReadOnlyMemory<byte> _current;
    public bool WasDisposed { get; private set; }
    public void WriteEvents(IEnumerable<string> events) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(string.Join("\n\n", events) + "\n\n"));
    public void Complete() => _chunks.Writer.TryComplete();
    public Task<bool> WaitForReadAsync(CancellationToken ct) => _reads.Reader.ReadAsync(ct).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_current.Length == 0)
        {
            if (_chunks.Reader.TryRead(out var chunk))
            {
                _current = chunk;
                break;
            }
            _reads.Writer.TryWrite(true);
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
        }
        var count = Math.Min(buffer.Length, _current.Length);
        _current[..count].CopyTo(buffer);
        _current = _current[count..];
        return count;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    protected override void Dispose(bool disposing) { WasDisposed = true; Complete(); base.Dispose(disposing); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
