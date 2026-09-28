using Api.Domain;

namespace Api.Store;

/// <summary>
/// The outcome of <see cref="InMemoryStore.TryCreate"/>. The endpoint maps it to an HTTP status.
/// </summary>
public record CreateResult
{
    public required CreateOutcome Outcome { get; init; }

    /// <summary>
    /// The inserted reservation, set only when <see cref="Outcome"/> is <see cref="CreateOutcome.Created"/>.
    /// </summary>
    public Reservation? Reservation { get; init; }
}

/// <summary>Why a reservation was or wasn't created.</summary>
public enum CreateOutcome
{
    /// <summary>Every slot had a free spot, and the reservation was inserted.</summary>
    Created,

    /// <summary>At least one slot was full; nothing was inserted.</summary>
    Full,

    /// <summary>The start was no longer in the future when the lock was acquired; nothing was inserted.</summary>
    StartPassed,
}
