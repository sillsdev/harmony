using Crdt.Core;
using Crdt.Db;
using Crdt.Sample;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Crdt.Tests;

public class RepositoryTests : IAsyncLifetime
{
    private readonly ServiceProvider _services;
    private CrdtRepository _repository;

    public RepositoryTests()
    {
        _services = new ServiceCollection()
            .AddCrdtDataSample(":memory:")
            .BuildServiceProvider();
        
        _repository = _services.GetRequiredService<CrdtRepository>();
    }

    public async Task InitializeAsync()
    {
        var crdtDbContext = _services.GetRequiredService<CrdtDbContext>();
        await crdtDbContext.Database.OpenConnectionAsync();
        await crdtDbContext.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
    }

    [Fact]
    public void CanGetLatestDateTime()
    {
        _repository.GetLatestDateTime().Should().BeNull();
    }

    [Fact]
    public async Task ReturnsCommitDateTime()
    {
        var expectedDateTime = new HybridDateTime(new DateTime(2000, 1, 1), 0);
        await _repository.AddCommit(new Commit()
        {
            ClientId = Guid.NewGuid(),
            HybridDateTime = expectedDateTime
        });
        _repository.GetLatestDateTime().Should().Be(expectedDateTime);
    }

    [Fact]
    public async Task ReturnsLatestCommitDateTime()
    {
        var expectedDateTime = new HybridDateTime(new DateTime(2000, 1, 1), 0);
        await _repository.AddCommit(new Commit()
        {
            ClientId = Guid.NewGuid(),
            HybridDateTime = expectedDateTime
        });
        await _repository.AddCommit(new Commit()
        {
            ClientId = Guid.NewGuid(),
            HybridDateTime = new HybridDateTime(new DateTime(2000, 1, 2), 0)
        });
        _repository.GetLatestDateTime().Should().Be(expectedDateTime);
    }

    [Fact]
    public async Task ReturnsLatestCommitDateTimeByCount()
    {
        var expectedDateTime = new HybridDateTime(new DateTime(2000, 1, 1), 1);
        await _repository.AddCommit(new Commit()
        {
            ClientId = Guid.NewGuid(),
            HybridDateTime = expectedDateTime with { Counter = 0 }
        });
        await _repository.AddCommit(new Commit()
        {
            ClientId = Guid.NewGuid(),
            HybridDateTime = expectedDateTime with { Counter = 1 }
        });
        _repository.GetLatestDateTime().Should().Be(expectedDateTime);
    }

    [Fact]
    public async Task OrdersCommitsByDate()
    {
        var commit1Time = new HybridDateTime(new DateTime(2000, 1, 1), 0);
        var commit2Time = new HybridDateTime(new DateTime(2000, 1, 2), 0);
        await _repository.AddCommits([
            new Commit
            {
                ClientId = Guid.NewGuid(),
                HybridDateTime = commit1Time
            },
            new Commit
            {
                ClientId = Guid.NewGuid(),
                HybridDateTime = commit2Time
            }
        ]);
        var commits = await _repository.CurrentCommits().ToArrayAsync();
        commits.Select(c => c.HybridDateTime).Should().ContainInConsecutiveOrder(commit1Time, commit2Time);
    }

    [Fact]
    public async Task OrdersCommitsByCounter()
    {
        var commit1Time = new HybridDateTime(new DateTime(2000, 1, 1), 0);
        var commit2Time = new HybridDateTime(new DateTime(2000, 1, 1), 1);
        await _repository.AddCommits([
            new Commit
            {
                ClientId = Guid.NewGuid(),
                HybridDateTime = commit1Time
            },
            new Commit
            {
                ClientId = Guid.NewGuid(),
                HybridDateTime = commit2Time
            }
        ]);
        var commits = await _repository.CurrentCommits().ToArrayAsync();
        commits.Select(c => c.HybridDateTime).Should().ContainInConsecutiveOrder(commit1Time, commit2Time);
    }
}