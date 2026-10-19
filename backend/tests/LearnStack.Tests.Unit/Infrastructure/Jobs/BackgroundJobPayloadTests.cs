using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobPayloadTests
{
    private static readonly Guid Sample = Guid.Parse("019712ac-1234-7000-8000-0000000000ab");

    [Fact]
    public void Fresh_payload_options_refuse_resolver_modifiers_before_first_serialization()
    {
        // A fresh factory result prevents another test's serialization from accidentally locking the resolver for us.
        var options = (JsonSerializerOptions)typeof(BackgroundJobPayloadContract)
            .GetMethod("CreateOptions", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        var resolver = (DefaultJsonTypeInfoResolver)options.TypeInfoResolver!;
        options.IsReadOnly.Should().BeTrue();
        var mutation = () => resolver.Modifiers.Add(typeInfo => typeInfo.Properties.Clear());
        mutation.Should().Throw<InvalidOperationException>();
        resolver.Modifiers.Should().BeEmpty();
    }

    [Fact]
    public void Approved_identifiers_and_nullable_identifiers_round_trip_through_the_exact_type()
    {
        var parameters = new Identifiers(Sample, TenantId.From(Sample), BackgroundJobId.From(Sample),
            BackgroundJobAttemptId.From(Sample), BackgroundJobOccurrenceId.From(Sample), BackgroundJobRecipientId.From(Sample), null);
        var json = BackgroundJobPayloadContract.Serialize(parameters, typeof(Identifiers), BackgroundJobPayloadKind.Parameters);
        BackgroundJobPayloadContract.Read(json, typeof(Identifiers), BackgroundJobPayloadKind.Parameters).Should().Be(parameters);
        json.Should().Contain(Sample.ToString("D"));
        BackgroundJobPayloadContract.JsonOptions.IsReadOnly.Should().BeTrue();
        var mutation = () => BackgroundJobPayloadContract.JsonOptions.PropertyNameCaseInsensitive = true;
        mutation.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(typeof(TextParameters))]
    [InlineData(typeof(NumberParameters))]
    [InlineData(typeof(CollectionParameters))]
    [InlineData(typeof(ObjectParameters))]
    [InlineData(typeof(MutableParameters))]
    [InlineData(typeof(IgnoredParameters))]
    [InlineData(typeof(CallbackParameters))]
    [InlineData(typeof(OpenParameters))]
    [InlineData(typeof(IdentifierPlan))]
    [InlineData(typeof(CustomGetterParameters))]
    [InlineData(typeof(CustomInitParameters))]
    public void Unapproved_payload_shapes_are_rejected_at_registration(Type type)
    {
        var action = () => BackgroundJobPayloadContract.ValidateShape(type, BackgroundJobPayloadKind.Parameters);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Empty_and_uninitialized_identifiers_are_refused_before_writing()
    {
        var empty = () => BackgroundJobPayloadContract.Serialize(new SingleIdentifier(Guid.Empty),
            typeof(SingleIdentifier), BackgroundJobPayloadKind.Parameters);
        empty.Should().Throw<ArgumentException>();
#pragma warning disable VOG009 // Dirty control: admission must refuse an uninitialized identifier even below compiler checks.
        var uninitialized = () => BackgroundJobPayloadContract.Serialize(new JobIdentifier(default),
            typeof(JobIdentifier), BackgroundJobPayloadKind.Parameters);
#pragma warning restore VOG009
        uninitialized.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Id\":null}")]
    [InlineData("{\"Id\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"Id\":\"019712AC-1234-7000-8000-0000000000AB\"}")]
    [InlineData("{\"Id\":\"019712ac1234700080000000000000ab\"}")]
    [InlineData("{\"id\":\"019712ac-1234-7000-8000-0000000000ab\"}")]
    [InlineData("{\"Id\":\"019712ac-1234-7000-8000-0000000000ab\",\"TenantId\":\"019712ac-1234-7000-8000-0000000000ab\"}")]
    [InlineData("{\"Id\":\"019712ac-1234-7000-8000-0000000000ab\",\"Id\":\"019712ac-1234-7000-8000-0000000000ab\"}")]
    public void Stored_wire_requires_exact_members_and_canonical_nonempty_identifiers(string json)
    {
        var action = () => BackgroundJobPayloadContract.Read(json, typeof(SingleIdentifier), BackgroundJobPayloadKind.Parameters);
        action.Should().Throw<Exception>();
    }

    [Fact]
    public void A_constructor_that_rewrites_stored_identifiers_fails_the_round_trip()
    {
        var json = "{\"Id\":\"" + Sample.ToString("D") + "\"}";
        var action = () => BackgroundJobPayloadContract.Read(json, typeof(RewrittenIdentifier), BackgroundJobPayloadKind.Parameters);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_stateful_getter_is_refused_before_reading_stored_identifiers()
    {
        CustomGetterParameters.Reads = 0;
        var json = "{\"Id\":\"" + Sample.ToString("D") + "\"}";
        var action = () => BackgroundJobPayloadContract.Read(json, typeof(CustomGetterParameters), BackgroundJobPayloadKind.Parameters);
        action.Should().Throw<ArgumentException>();
        CustomGetterParameters.Reads.Should().Be(0, "job identifier DTOs cannot execute custom accessors");
    }

    [Fact]
    public void Nonpositional_auto_properties_preserve_the_closed_identifier_DTO_convention()
    {
        var parameters = new AutoPropertyParameters { Id = Sample };
        var json = BackgroundJobPayloadContract.Serialize(parameters, typeof(AutoPropertyParameters), BackgroundJobPayloadKind.Parameters);
        BackgroundJobPayloadContract.Read(json, typeof(AutoPropertyParameters), BackgroundJobPayloadKind.Parameters)
            .Should().Be(parameters);
    }

    [Theory]
    [InlineData(BackgroundJobPayloadKind.Parameters, BackgroundJobPayloadLimits.Parameters)]
    [InlineData(BackgroundJobPayloadKind.Plan, BackgroundJobPayloadLimits.Plan)]
    [InlineData(BackgroundJobPayloadKind.Outcome, BackgroundJobPayloadLimits.Outcome)]
    public void Byte_budgets_are_inclusive_and_measure_utf8(BackgroundJobPayloadKind kind, int limit)
    {
        BackgroundJobPayloadContract.EnsureWithinBudget(new string('x', limit), kind);
        var tooLong = () => BackgroundJobPayloadContract.EnsureWithinBudget(new string('x', limit + 1), kind);
        tooLong.Should().Throw<ArgumentException>();
        var unicode = () => BackgroundJobPayloadContract.EnsureWithinBudget(new string('é', limit / 2 + 1), kind);
        unicode.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Stored_wire_enforces_its_budget_before_deserialization()
    {
        var json = JsonSerializer.Serialize(new SingleIdentifier(Sample));
        BackgroundJobPayloadContract.Read(json.PadRight(BackgroundJobPayloadLimits.Parameters),
            typeof(SingleIdentifier), BackgroundJobPayloadKind.Parameters).Should().Be(new SingleIdentifier(Sample));
        var action = () => BackgroundJobPayloadContract.Read(json.PadRight(BackgroundJobPayloadLimits.Parameters + 1),
            typeof(SingleIdentifier), BackgroundJobPayloadKind.Parameters);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Fixed_occurrence_input_round_trips_but_cannot_bind_the_ordinary_scheduler()
    {
        var parameters = new BackgroundJobOccurrenceParameters(BackgroundJobOccurrenceId.From(Sample));
        var json = BackgroundJobPayloadContract.Serialize(parameters, parameters.GetType(), BackgroundJobPayloadKind.Parameters);
        BackgroundJobPayloadContract.Read(json, parameters.GetType(), BackgroundJobPayloadKind.Parameters).Should().Be(parameters);
        foreach (var method in typeof(IBackgroundJobScheduler).GetMethods())
        {
            var close = () => method.MakeGenericMethod(typeof(BackgroundJobOccurrenceParameters));
            close.Should().Throw<ArgumentException>();
        }
    }

    public sealed record Identifiers(Guid Id, TenantId TenantId, BackgroundJobId JobId,
        BackgroundJobAttemptId AttemptId, BackgroundJobOccurrenceId OccurrenceId,
        BackgroundJobRecipientId RecipientId, OrganizationId? OrganizationId) : IBackgroundJobParameters;
    public sealed record SingleIdentifier(Guid Id) : IBackgroundJobParameters;
    public sealed record JobIdentifier(BackgroundJobId Id) : IBackgroundJobParameters;
    public sealed record TextParameters(string Value) : IBackgroundJobParameters;
    public sealed record NumberParameters(int Value) : IBackgroundJobParameters;
    public sealed record CollectionParameters(Guid[] Ids) : IBackgroundJobParameters;
    public sealed record ObjectParameters(object Value) : IBackgroundJobParameters;
    public sealed record MutableParameters : IBackgroundJobParameters { public Guid Id { get; set; } }
    public sealed record IgnoredParameters([property: JsonIgnore] Guid Id) : IBackgroundJobParameters;
    public sealed record CallbackParameters(Guid Id) : IBackgroundJobParameters, IJsonOnSerializing
    {
        public void OnSerializing() { }
    }
    public record OpenParameters(Guid Id) : IBackgroundJobParameters;
    public sealed record IdentifierPlan(Guid Id) : IBackgroundJobPlan;
    public sealed record AutoPropertyParameters : IBackgroundJobParameters
    {
        public Guid Id { get; init; }
    }
    public sealed record CustomGetterParameters : IBackgroundJobParameters
    {
        public static int Reads { get; set; }
        public Guid Id
        {
            get => ++Reads <= 2 ? field : Guid.Parse("019712ac-1234-7000-8000-0000000000ac");
            init => field = value;
        }
    }
    public sealed record CustomInitParameters : IBackgroundJobParameters
    {
        public Guid Id { get; init => field = value; }
    }
    public sealed record RewrittenIdentifier : IBackgroundJobParameters
    {
        public RewrittenIdentifier(Guid id) => Id = Guid.Parse("019712ac-1234-7000-8000-0000000000ac");
        public Guid Id { get; init; }
    }
}
