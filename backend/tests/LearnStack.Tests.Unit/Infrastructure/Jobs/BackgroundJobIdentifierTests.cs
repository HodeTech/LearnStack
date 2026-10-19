using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobIdentifierTests
{
    [Theory]
    [InlineData(typeof(BackgroundJobId))]
    [InlineData(typeof(BackgroundJobAttemptId))]
    [InlineData(typeof(BackgroundJobOccurrenceId))]
    [InlineData(typeof(BackgroundJobRecipientId))]
    public void Job_identity_uses_the_full_mask_safe_initialization_and_no_implicit_or_minting_api(Type type)
    {
        var sample = Guid.Parse("019712ac-1234-7000-8000-0000000000ab");
        var id = type.GetMethod("From", [typeof(Guid)])!.Invoke(null, [sample]);
        type.GetNestedTypes().Select(nested => nested.Name).Should().Contain(["EfCoreValueConverter", "EfCoreValueComparer"]);
        JsonSerializer.Deserialize(JsonSerializer.Serialize(id, type), type).Should().Be(id);
        TypeDescriptor.GetConverter(type).ConvertFromString(null, CultureInfo.InvariantCulture, sample.ToString("D")).Should().Be(id);
        ((IStronglyTypedId<Guid>)id!).Value.Should().Be(sample);
        ((IStronglyTypedId<Guid>)Activator.CreateInstance(type)!).IsInitialized().Should().BeFalse();
        type.GetMethods(BindingFlags.Public | BindingFlags.Static).Select(method => method.Name)
            .Should().NotContain(["op_Implicit", "New", "NewId", "Create"]);
    }
}
