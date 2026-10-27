using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace LearnStack.Infrastructure.DataProtection;

/// <summary>Rejects incomplete opted-in models after normal model construction and customization.</summary>
internal sealed class PersonalDataClassificationConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        if (PersonalDataModelCensus.Inspect(modelBuilder.Metadata).Count != 0)
            throw new InvalidOperationException(PersonalDataModelCensus.FailureMessage);
        modelBuilder.HasAnnotation(PersonalDataClassificationExtensions.ValidatedModelAnnotation, true);
    }
}
