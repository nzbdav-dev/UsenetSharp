using System.Runtime.ExceptionServices;
using System.Text;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    /// <summary>
    /// Performs a STAT existence check for many segments using NNTP command pipelining (RFC 3977 §3.5).
    ///
    /// All STAT commands in the batch are written back-to-back and flushed once, then the responses
    /// are read in order. Because each STAT reply is a single line and replies are returned in the
    /// same order the commands were sent, this collapses what would be one network round-trip per
    /// segment into a single round-trip for the whole batch.
    ///
    /// The entire batch runs as one logical operation while holding the per-connection command lock,
    /// so it composes with the rest of the client exactly like a single command. If anything goes
    /// wrong mid-batch the underlying stream is left in an indeterminate state (partially written
    /// commands or unread responses), so the connection is marked unhealthy and must be discarded by
    /// the caller — never reused.
    /// </summary>
    /// <param name="segmentIds">The segments to STAT, checked in order.</param>
    /// <param name="cancellationToken">Cancels waiting for the connection's command lock.</param>
    /// <returns>One <see cref="UsenetStatResponse"/> per input segment, in the same order.</returns>
    public async Task<IReadOnlyList<UsenetStatResponse>> StatPipelinedAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        CancellationToken cancellationToken
    )
    {
        if (segmentIds.Count == 0)
            return [];

        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnhealthy();
            ThrowIfNotConnected();

            // A single idle-based deadline for the whole batch. One timer per command would create a
            // storm of timers under many concurrent connections, and a fixed total-batch budget would
            // false-fire when a batch legitimately queues behind other connections' work. Instead we
            // bound the gap between consecutive replies and re-arm on each one: a busy-but-alive
            // connection keeps resetting it, while a genuinely stalled connection still trips it.
            var idleTimeout = TimeSpan.FromSeconds(20);
            using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            batchCts.CancelAfter(idleTimeout);
            var token = batchCts.Token;

            try
            {
                // Phase 1 -- write the whole batch in one direct stream write. Going through the
                // AutoFlush StreamWriter would fragment a large batch into several small TLS records
                // and throttle the pipeline; one buffer write (with TCP_NODELAY) puts it on the wire
                // as a single blast so the server pipelines it. The writer holds no buffered data, so
                // bypassing it stays in sync; Latin1 matches its encoding (one byte per char).
                var payload = new StringBuilder(segmentIds.Count * 32);
                foreach (var segmentId in segmentIds)
                    payload.Append("STAT <").Append((string)segmentId).Append(">\r\n");
                var payloadBytes = Encoding.Latin1.GetBytes(payload.ToString());
                await _stream!.WriteAsync(payloadBytes, token).ConfigureAwait(false);
                await _stream!.FlushAsync(token).ConfigureAwait(false);

                // Phase 2 -- read one response line per command, in order.
                var responses = new UsenetStatResponse[segmentIds.Count];
                for (var i = 0; i < segmentIds.Count; i++)
                {
                    var response = await _reader!.ReadLineAsync(token).ConfigureAwait(false);
                    // Re-arm the idle deadline: progress was made, so the clock restarts for the
                    // next reply rather than counting down against the whole batch.
                    batchCts.CancelAfter(idleTimeout);
                    if (response is null)
                        throw new UsenetProtocolException(
                            "Connection closed while reading pipelined STAT responses.");

                    var responseCode = ParseResponseCode(response);
                    var articleExists = responseCode == (int)UsenetResponseType.ArticleExists;

                    // Desync guard: a 223 reply echoes the message-id ("223 0 <message-id>").
                    // If the echoed id does not match the command we sent at this position then the
                    // response stream is misaligned and every later mapping would be wrong, so we
                    // abort the whole batch rather than return bogus existence results.
                    if (articleExists && !ResponseEchoesSegmentId(response, segmentIds[i]))
                        throw new UsenetProtocolException(
                            "Pipelined STAT responses are out of order; aborting batch.");

                    responses[i] = new UsenetStatResponse
                    {
                        ResponseCode = responseCode,
                        ResponseMessage = response,
                        ArticleExists = articleExists,
                    };
                }

                return responses;
            }
            catch (Exception e)
            {
                // Translate our own batch-timeout into a TimeoutException (a real caller cancel or a
                // disconnect via _cts propagates as-is).
                if (e is OperationCanceledException && batchCts.IsCancellationRequested
                    && !_cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    e = new TimeoutException("Timeout during pipelined STAT batch.");

                // The stream position is now indeterminate (unwritten commands and/or unread
                // pipelined responses). Poison the connection so it is never silently reused with a
                // desynced stream; the caller is expected to discard it.
                lock (this)
                {
                    _backgroundException ??= ExceptionDispatchInfo.Capture(e);
                }

                throw e;
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private static bool ResponseEchoesSegmentId(string response, SegmentId segmentId)
    {
        var id = segmentId.Value;
        // Nothing to verify against (empty id) -- trust the in-order guarantee.
        if (id.IsEmpty) return true;
        return response.AsSpan().IndexOf(id, StringComparison.Ordinal) >= 0;
    }
}
