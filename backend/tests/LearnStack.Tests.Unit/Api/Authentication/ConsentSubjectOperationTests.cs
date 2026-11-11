using System.Reflection;
using LearnStack.Api.Authentication;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

public sealed class ConsentSubjectOperationTests
{
    private const string Path = "/api/v1/tenancy/membership-field-consents/own/scopes";

    [Fact]
    public void Closed_purpose_rejects_wrong_method_action_and_policy()
    {
        var operation = ConsentSubjectOperation.ForPath(Path)!;
        var http = new DefaultHttpContext();
        http.Request.Path = Path;
        http.Request.Method = "GET";
        http.SetEndpoint(Endpoint(nameof(ConsentSubjectController.Scopes), typeof(ListOwnFieldConsentScopesQuery)));
        Assert.True(operation.Matches(http));
        http.Request.Method = "POST";
        Assert.False(operation.Matches(http));
        http.Request.Method = "GET";
        http.SetEndpoint(Endpoint(nameof(ConsentSubjectController.History), typeof(ListOwnFieldConsentScopesQuery)));
        Assert.False(operation.Matches(http));
        http.SetEndpoint(Endpoint(nameof(ConsentSubjectController.Scopes), typeof(ListOwnMembershipFieldConsentsQuery)));
        Assert.False(operation.Matches(http));
        http.Request.Path = "/api/v1/tenancy/membership-field-consents/own/history";
        Assert.False(operation.Matches(http));
    }

    private static Endpoint Endpoint(string action, Type policy)
    {
        var type = typeof(ConsentSubjectController);
        var descriptor = new ControllerActionDescriptor { ControllerTypeInfo = type.GetTypeInfo(), MethodInfo = type.GetMethod(action)! };
        return new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(descriptor, new OperationPolicyAttribute(policy)), "test");
    }
}
