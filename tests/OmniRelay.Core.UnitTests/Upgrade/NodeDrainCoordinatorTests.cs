using OmniRelay.ControlPlane.Upgrade;
using Xunit;

namespace OmniRelay.Core.UnitTests.Upgrade;

public sealed class NodeDrainCoordinatorTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask BeginDrainAndResumeTransitionsState()
    {
        var coordinator = new NodeDrainCoordinator();
        var participant = new FakeParticipant();
        coordinator.RegisterParticipant("http", participant);

        var snapshot = await coordinator.BeginDrainAsync("rolling upgrade", CancellationToken.None);
        Assert.Equal(NodeDrainState.Drained, snapshot.State);
        Assert.Single(snapshot.Participants);
        Assert.Equal(NodeDrainParticipantState.Drained, snapshot.Participants[0].State);

        var resumed = await coordinator.ResumeAsync(CancellationToken.None);
        Assert.Equal(NodeDrainState.Active, resumed.State);
        Assert.Single(resumed.Participants);
        Assert.Equal(NodeDrainParticipantState.Active, resumed.Participants[0].State);
    }

    private sealed class FakeParticipant : INodeDrainParticipant
    {
        public ValueTask DrainAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
