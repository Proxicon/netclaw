// -----------------------------------------------------------------------
// <copyright file="OpenAiResponseStreamClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using OpenAI.Responses;

namespace Netclaw.Providers.OpenAi;

/// <summary>
/// Requires a successful Responses terminal event before the session can dispatch its tools.
/// The SDK adapter exposes failed and incomplete events as ordinary updates.
/// </summary>
internal sealed class OpenAiResponseStreamClient(
    IChatClient innerClient,
    ILogger logger,
    TimeProvider timeProvider) : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var start = timeProvider.GetTimestamp();
        var updates = 0;
        var functionItems = 0;
        var argumentDeltas = 0;
        var argumentDeltaChars = 0;
        var argumentDoneBytes = 0;
        var itemDoneArgumentBytes = 0;
        var parsedCalls = 0;
        var emptyCalls = 0;
        var parseErrors = 0;
        var terminalFunctionItems = 0;
        var terminalArgumentBytes = 0;
        var outcome = "interrupted";
        logger.LogInformation("response_stream_start");

        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                updates++;
                if (updates == 1)
                    logger.LogInformation("response_stream_first_update elapsedMs={ElapsedMs:F0}",
                        timeProvider.GetElapsedTime(start).TotalMilliseconds);

                switch (update.RawRepresentation)
                {
                    case StreamingResponseOutputItemAddedUpdate { Item: FunctionCallResponseItem }:
                        functionItems++;
                        if (functionItems == 1)
                            logger.LogInformation("response_stream_first_function elapsedMs={ElapsedMs:F0}",
                                timeProvider.GetElapsedTime(start).TotalMilliseconds);
                        break;
                    case StreamingResponseFunctionCallArgumentsDeltaUpdate delta:
                        argumentDeltas++;
                        argumentDeltaChars += delta.Delta?.Length ?? 0;
                        if (argumentDeltas == 1)
                            logger.LogInformation("response_stream_first_arguments elapsedMs={ElapsedMs:F0}",
                                timeProvider.GetElapsedTime(start).TotalMilliseconds);
                        break;
                    case StreamingResponseFunctionCallArgumentsDoneUpdate done:
                        argumentDoneBytes += done.FunctionArguments?.ToMemory().Length ?? 0;
                        break;
                    case StreamingResponseOutputItemDoneUpdate { Item: FunctionCallResponseItem item }:
                        itemDoneArgumentBytes += item.FunctionArguments?.ToMemory().Length ?? 0;
                        break;
                    case StreamingResponseFailedUpdate:
                        outcome = "failed";
                        throw Failure(outcome);
                    case StreamingResponseIncompleteUpdate incomplete:
                        var reason = incomplete.Response.IncompleteStatusDetails?.Reason;
                        outcome = reason == ResponseIncompleteStatusReason.MaxOutputTokens ? "incomplete_output_limit"
                            : reason == ResponseIncompleteStatusReason.ContentFilter ? "incomplete_content_filter" : "incomplete";
                        throw Failure(outcome);
                    case StreamingResponseErrorUpdate:
                        outcome = "error";
                        throw Failure(outcome);
                    case StreamingResponseCompletedUpdate completed:
                        if (completed.Response.Status != ResponseStatus.Completed)
                        {
                            outcome = "invalid_terminal";
                            throw Failure(outcome);
                        }
                        foreach (var item in completed.Response.OutputItems.OfType<FunctionCallResponseItem>())
                        {
                            terminalFunctionItems++;
                            terminalArgumentBytes += item.FunctionArguments?.ToMemory().Length ?? 0;
                        }
                        outcome = "completed";
                        break;
                }

                foreach (var call in update.Contents.OfType<FunctionCallContent>())
                {
                    parsedCalls++;
                    if (call.Arguments is not { Count: > 0 }) emptyCalls++;
                    if (call.Exception is not null) parseErrors++;
                    logger.LogInformation(
                        "response_stream_function callIndex={CallIndex} argumentCount={ArgumentCount} parseError={ParseError} elapsedMs={ElapsedMs:F0}",
                        parsedCalls, call.Arguments?.Count ?? 0, call.Exception is not null,
                        timeProvider.GetElapsedTime(start).TotalMilliseconds);
                }

                yield return update;

                // A terminal event ends the response. The SDK can otherwise wait for HTTP EOF after it emits usage.
                // response-terminal-release:begin
                if (outcome == "completed")
                    yield break;
                // response-terminal-release:end
            }

            // EOF alone does not prove a complete response. The reader retains all tool calls until this enumeration succeeds.
            cancellationToken.ThrowIfCancellationRequested();
            outcome = "missing_terminal";
            throw Failure(outcome);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested) outcome = "cancelled";
            logger.LogInformation(
                "response_stream_end outcome={Outcome} elapsedMs={ElapsedMs:F0} updates={Updates} functionItems={FunctionItems} argumentDeltas={ArgumentDeltas} argumentDeltaChars={ArgumentDeltaChars} argumentDoneBytes={ArgumentDoneBytes} itemDoneArgumentBytes={ItemDoneArgumentBytes} terminalFunctionItems={TerminalFunctionItems} terminalArgumentBytes={TerminalArgumentBytes} parsedCalls={ParsedCalls} emptyCalls={EmptyCalls} parseErrors={ParseErrors}",
                outcome, timeProvider.GetElapsedTime(start).TotalMilliseconds, updates, functionItems,
                argumentDeltas, argumentDeltaChars, argumentDoneBytes, itemDoneArgumentBytes,
                terminalFunctionItems, terminalArgumentBytes, parsedCalls, emptyCalls, parseErrors);
        }
    }

    private static ProviderException Failure(string outcome) => new(
        "The model provider did not complete its response. Netclaw stopped this model call. No tools from this response ran.",
        $"OpenAI Responses stream ended without success: {outcome}.");
}
