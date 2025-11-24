using AwesomeAssertions;
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
        snapshot.State.Should().Be(NodeDrainState.Drained);
        snapshot.Participants.Should().HaveCount(1);
        snapshot.Participants[0].State.Should().Be(NodeDrainParticipantState.Drained);

        var resumed = await coordinator.ResumeAsync(CancellationToken.None);
        resumed.State.Should().Be(NodeDrainState.Active);
        resumed.Participants.Should().HaveCount(1);
        resumed.Participants[0].State.Should().Be(NodeDrainParticipantState.Active);
    }

    private sealed class FakeParticipant : INodeDrainParticipant
    {
        public ValueTask DrainAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
