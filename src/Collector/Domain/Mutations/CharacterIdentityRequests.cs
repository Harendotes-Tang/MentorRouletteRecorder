namespace MentorRecorder.Collector.Domain.Mutations;

/// <summary>What the player says about a character the Collector told apart.</summary>
public enum CharacterIdentityAction
{
    /// <summary>It is the character of a profile.</summary>
    Bind,

    /// <summary>Keep no progress for it and stop asking.</summary>
    Ignore,

    /// <summary>Ask about an ignored one again.</summary>
    AskAgain,
}

/// <summary>A validated <c>ResolveCharacterIdentity</c> request.</summary>
/// <param name="RequestId">Envelope request id; the idempotency key.</param>
/// <param name="IdentityId">Identity the answer is about.</param>
/// <param name="Action">The answer.</param>
/// <param name="ProfileId">Profile for <see cref="CharacterIdentityAction.Bind"/>; null otherwise.</param>
/// <param name="DisplayName">Name supplied when confirming an upgraded profile, or null for ordinary binding.</param>
public sealed record ResolveCharacterIdentityCommand(
    string RequestId,
    long IdentityId,
    CharacterIdentityAction Action,
    long? ProfileId,
    string? DisplayName = null);

/// <summary>Result of an answer about one identity.</summary>
/// <param name="IdentityId">Identity the answer was about.</param>
/// <param name="ProfileId">Profile it is bound to afterwards, or null.</param>
/// <param name="AssignedRunCount">Runs that moved to the profile.</param>
/// <param name="IdempotentReplay">True when the request had been applied before.</param>
public sealed record CharacterIdentityOutcome(
    long IdentityId,
    long? ProfileId,
    int AssignedRunCount,
    bool IdempotentReplay);

/// <summary>What the Collector made of a character logging in.</summary>
/// <param name="IdentityId">
/// The identity recorded for the character, or null when it could not be recorded (the region
/// already keeps as many as it may).
/// </param>
/// <param name="ProfileId">Profile the character is bound to, or null while nobody has said.</param>
/// <param name="StatisticsChanged">
/// True when a current profile, binding count or pending registration changed.
/// </param>
public sealed record CharacterIdentityObservation(long? IdentityId, long? ProfileId, bool StatisticsChanged);
