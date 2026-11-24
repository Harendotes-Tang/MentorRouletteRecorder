using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>A client identity that never changes, for a selector's <see cref="ProfileSelector.Current"/>.</summary>
/// <param name="Region">Region of the client.</param>
/// <param name="GameBuild">Build of the client, or null.</param>
internal sealed record FixedGameBuildSource(Region Region, string? GameBuild) : IGameBuildSource;
