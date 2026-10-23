using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

public sealed class ImportedMetadataContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentImportedRunProjectsBooleanAcceptedByContract(bool mentorConfirmed)
    {
        var wire = Wire.Run(ImportedRun(mentorConfirmed));

        Assert.Equal(mentorConfirmed, wire["import_metadata"]!["mentor_confirmed"]!.GetValue<bool>());
        ContractSchema.Validate("$defs/Run", wire, "Imported run with mentor confirmation");
    }

    [Fact]
    public void LegacyImportedMetadataWithoutMentorConfirmationRemainsAccepted()
    {
        var wire = Wire.Run(ImportedRun(mentorConfirmed: true));
        Assert.True(wire["import_metadata"]!.AsObject().Remove("mentor_confirmed"));

        ContractSchema.Validate("$defs/Run", wire, "Legacy imported run without mentor confirmation");
    }

    [Fact]
    public void MentorConfirmationRejectsAStringInsteadOfBoolean()
    {
        var metadata = Wire.Run(ImportedRun(mentorConfirmed: false))["import_metadata"]!.AsObject();
        metadata["mentor_confirmed"] = "false";

        Assert.False(ContractSchema.Evaluate("$defs/RunImportMetadata", metadata).IsValid);
    }

    private static MentorRun ImportedRun(bool mentorConfirmed)
    {
        var now = new DateTimeOffset(2026, 9, 4, 1, 2, 0, TimeSpan.Zero);
        return new MentorRun
        {
            RunId = Guid.NewGuid().ToString("D"),
            Revision = 1,
            Source = RunSource.Import,
            Result = RunResult.Completed,
            EnteredAtUtc = now.AddMinutes(-2),
            EndedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ImportMetadata = new RunImportMetadata("JSON", null, null, null, now,
                "synthetic-source-fingerprint", mentorConfirmed),
        };
    }
}
