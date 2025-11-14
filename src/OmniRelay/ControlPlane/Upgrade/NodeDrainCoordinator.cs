using System.Collections.Immutable;

namespace OmniRelay.ControlPlane.Upgrade;

/// <summary>Coordinates drain/upgrade workflows across registered participants.</summary>
public sealed class NodeDrainCoordinator
{
    private readonly object _stateLock = new();
    private readonly List<ParticipantRegistration> _participants = new();
    private NodeDrainState _state = NodeDrainState.Active;
    private string? _reason;
    private DateTimeOffset _updatedAt = DateTimeOffset.MinValue;

    /// <summary>Registers a participant for drain orchestration.</summary>
    public void RegisterParticipant(string name, INodeDrainParticipant participant)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Participant name cannot be null or whitespace.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(participant);

        lock (_stateLock)
        {
            if (_participants.Any(existing => ReferenceEquals(existing.Participant, participant)))
            {
                return;
            }

            _participants.Add(new ParticipantRegistration(name.Trim(), participant));
        }
    }

    /// <summary>Gets the current drain snapshot.</summary>
    public NodeDrainSnapshot Snapshot()
    {
        lock (_stateLock)
        {
            return CreateSnapshot();
        }
    }

    /// <summary>Initiates node drain, awaiting all participants before returning.</summary>
    public async Task<NodeDrainSnapshot> BeginDrainAsync(string? reason, CancellationToken cancellationToken)
    {
        ParticipantRegistration[] participants;
        lock (_stateLock)
        {
            if (_state == NodeDrainState.Draining)
            {
                throw new InvalidOperationException("Node drain is already in progress.");
            }

            if (_state == NodeDrainState.Drained)
            {
                return CreateSnapshot();
            }

            _state = NodeDrainState.Draining;
            _reason = string.IsNullOrWhiteSpace(reason) ? null : reason!.Trim();
            _updatedAt = DateTimeOffset.UtcNow;
            participants = _participants.ToArray();

            foreach (var registration in participants)
            {
                registration.State = NodeDrainParticipantState.Draining;
                registration.LastError = null;
                registration.UpdatedAt = _updatedAt;
            }
        }

        foreach (var registration in participants)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await registration.Participant.DrainAsync(cancellationToken).ConfigureAwait(false);
                lock (_stateLock)
                {
                    registration.State = NodeDrainParticipantState.Drained;
                    registration.LastError = null;
                    registration.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
            catch (Exception ex)
            {
                lock (_stateLock)
                {
                    registration.State = NodeDrainParticipantState.Failed;
                    registration.LastError = ex.Message;
                    registration.UpdatedAt = DateTimeOffset.UtcNow;
                    _state = NodeDrainState.Failed;
                    _updatedAt = DateTimeOffset.UtcNow;
                }

                throw;
            }
        }

        lock (_stateLock)
        {
            _state = NodeDrainState.Drained;
            _updatedAt = DateTimeOffset.UtcNow;
            return CreateSnapshot();
        }
    }

    /// <summary>Resumes normal operation after a completed drain.</summary>
    public async Task<NodeDrainSnapshot> ResumeAsync(CancellationToken cancellationToken)
    {
        ParticipantRegistration[] participants;
        lock (_stateLock)
        {
            if (_state == NodeDrainState.Active)
            {
                return CreateSnapshot();
            }

            if (_state == NodeDrainState.Draining)
            {
                throw new InvalidOperationException("Cannot resume while a drain is in progress.");
            }

            if (_state == NodeDrainState.Failed)
            {
                throw new InvalidOperationException("Drain previously failed. Restart the process before resuming.");
            }

            participants = _participants.ToArray();
        }

        foreach (var registration in participants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await registration.Participant.ResumeAsync(cancellationToken).ConfigureAwait(false);
            lock (_stateLock)
            {
                registration.State = NodeDrainParticipantState.Active;
                registration.LastError = null;
                registration.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        lock (_stateLock)
        {
            _state = NodeDrainState.Active;
            _reason = null;
            _updatedAt = DateTimeOffset.UtcNow;
            return CreateSnapshot();
        }
    }

    private NodeDrainSnapshot CreateSnapshot()
    {
        var participants = _participants
            .Select(registration => new NodeDrainParticipantSnapshot(
                registration.Name,
                registration.State,
                registration.LastError,
                registration.UpdatedAt))
            .ToImmutableArray();

        return new NodeDrainSnapshot(_state, _reason, _updatedAt == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : _updatedAt, participants);
    }

    private sealed class ParticipantRegistration
    {
        public ParticipantRegistration(string name, INodeDrainParticipant participant)
        {
            Name = name;
            Participant = participant;
        }

        public string Name { get; }
        public INodeDrainParticipant Participant { get; }

        public NodeDrainParticipantState State { get; set; } = NodeDrainParticipantState.Active;

        public string? LastError { get; set; }

        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
