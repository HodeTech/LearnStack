using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed class ProductionAssembliesTests
{
    [Fact]
    public void Project_census_includes_every_project_name_and_excludes_generated_projects()
    {
        var directory = Directory.CreateTempSubdirectory("learnstack-project-census-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "LearnStack.Existing.csproj"), "<Project />");
            var added = Directory.CreateDirectory(Path.Combine(directory.FullName, "module"));
            File.WriteAllText(Path.Combine(added.FullName, "OtherProduction.csproj"), "<Project />");
            var generated = Directory.CreateDirectory(Path.Combine(added.FullName, "obj", "nested"));
            File.WriteAllText(Path.Combine(generated.FullName, "Generated.csproj"), "<Project />");

            ProductionAssemblies.Names(directory.FullName).Should()
                .Equal(["LearnStack.Existing", "OtherProduction"],
                    "a differently named production project must not silently escape every assembly census");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
