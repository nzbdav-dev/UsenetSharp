using UsenetSharp.Clients;
using UsenetSharp.Models;

namespace UsenetSharpTest.Clients;

public class StatPipelinedAsyncTests
{
    private static readonly string[] ValidSegmentIds = new[]
    {
        "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo",
        "439e9msqibLI2Ckyt7z6EMUY@iLqyzimBg7ac.t2n",
        "RdmCrBrzTPnTiYj7ze5Gwyt6@mfdz3EInI86g.bfV",
        "njX6awmG5Rl0lZbBbfll8WtA@M6zC3hmaiMoK.w5x",
        "vAOEczfxpsXMjg0bUPUGO7Bb@KDqE994Bw3O0.BG5"
    };

    [Test]
    public async Task StatPipelinedAsync_WithEmptyBatch_ReturnsEmptyList()
    {
        // Arrange
        var client = new UsenetClient();

        // Act
        var results = await client.StatPipelinedAsync([], CancellationToken.None);

        // Assert -- empty batch short-circuits before touching the connection
        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task StatPipelinedAsync_WithValidSegmentIds_ReturnsExistsInOrder()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);
        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        var batch = ValidSegmentIds.Select(x => (SegmentId)x).ToArray();

        // Act
        var results = await client.StatPipelinedAsync(batch, cancellationToken);

        // Assert -- one response per segment, all present, returned in request order
        Assert.That(results, Has.Count.EqualTo(batch.Length));
        for (var i = 0; i < results.Count; i++)
        {
            Assert.That(results[i].ArticleExists, Is.True, $"Article should exist for {ValidSegmentIds[i]}");
            Assert.That(results[i].ResponseCode, Is.EqualTo(223));
            Assert.That(results[i].ResponseType, Is.EqualTo(UsenetResponseType.ArticleExists));
            Assert.That(results[i].ResponseMessage, Does.Contain(ValidSegmentIds[i]),
                "Each 223 response should echo the message-id it corresponds to");
        }
    }

    [Test]
    public async Task StatPipelinedAsync_WithMixedBatch_MapsEachResponseToItsPosition()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);
        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Interleave a known-missing id between known-good ids to prove per-position mapping.
        var batch = new SegmentId[]
        {
            ValidSegmentIds[0],
            "definitely-missing@nonexistent.invalid",
            ValidSegmentIds[1],
        };

        // Act
        var results = await client.StatPipelinedAsync(batch, cancellationToken);

        // Assert
        Assert.That(results, Has.Count.EqualTo(3));
        Assert.That(results[0].ArticleExists, Is.True);
        Assert.That(results[0].ResponseCode, Is.EqualTo(223));

        Assert.That(results[1].ArticleExists, Is.False);
        Assert.That(results[1].ResponseCode, Is.EqualTo(430));

        Assert.That(results[2].ArticleExists, Is.True);
        Assert.That(results[2].ResponseCode, Is.EqualTo(223));
    }

    [Test]
    public async Task StatPipelinedAsync_MatchesSequentialStatResults()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);
        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        var batch = ValidSegmentIds.Select(x => (SegmentId)x).ToArray();

        // Act -- pipelined batch versus the proven one-at-a-time path
        var pipelined = await client.StatPipelinedAsync(batch, cancellationToken);

        var sequential = new List<UsenetStatResponse>();
        foreach (var id in batch)
            sequential.Add(await client.StatAsync(id, cancellationToken));

        // Assert -- pipelining must not change the observed result for any segment
        Assert.That(pipelined, Has.Count.EqualTo(sequential.Count));
        for (var i = 0; i < pipelined.Count; i++)
        {
            Assert.That(pipelined[i].ResponseCode, Is.EqualTo(sequential[i].ResponseCode));
            Assert.That(pipelined[i].ArticleExists, Is.EqualTo(sequential[i].ArticleExists));
        }
    }
}
