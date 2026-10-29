using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnStack.Tests.Integration;

public sealed partial class AuthenticatedHttpSurfaceRuntimeTests
{
    private sealed record EffectiveFilter(string Assembly, string Type, int Scope, int Order,
        int FilterOrder, bool Reusable, Type? Implementation, int Arguments, int? Status, Type? ResponseType,
        ProtectedAdministrationOperation? ProtectedOperation = null);

    private static void AssertActionFilters(ControllerActionDescriptor action)
    {
        var approved = Approved(action);
        var response = approved.Response;
        var produces = response == typeof(void) ? typeof(ProducesResponseTypeAttribute)
            : typeof(ProducesResponseTypeAttribute<>).MakeGenericType(response);
        var actual = action.FilterDescriptors.Select(descriptor =>
        {
            var filter = descriptor.Filter;
            var factory = filter as TypeFilterAttribute;
            var output = filter as ProducesResponseTypeAttribute;
            return new EffectiveFilter(filter.GetType().Assembly.GetName().Name!, filter.GetType().FullName!,
                descriptor.Scope, descriptor.Order, filter is IOrderedFilter ordered ? ordered.Order : 0,
                filter is IFilterFactory provider && provider.IsReusable,
                factory?.ImplementationType ?? (filter as ServiceFilterAttribute)?.ServiceType,
                factory?.Arguments?.Length ?? 0, output?.StatusCode, output?.Type,
                (filter as IdempotentAttribute)?.ProtectedOperation);
        });
        List<EffectiveFilter> expected =
        [
            Framework("Microsoft.AspNetCore.Mvc.ModelBinding.UnsupportedContentTypeFilter", 10, -3000, -3000),
            Framework("Microsoft.AspNetCore.Mvc.Infrastructure.ClientErrorResultFilterFactory", 30, -2000, -2000, true),
            Framework("Microsoft.AspNetCore.Mvc.Infrastructure.ModelStateInvalidFilterFactory", 30, -2000, -2000, true),
            Framework(typeof(TypeFilterAttribute).FullName!, 10, 0, 0) with { Implementation = typeof(ProblemDetailsNormalizationFilter) },
            Framework(typeof(ApiControllerAttribute).FullName!, 20, 0, 0),
        ];
        if (approved.Protected is { } operation) expected.Add(new EffectiveFilter("LearnStack.Api",
            "LearnStack.Api.Idempotency.IdempotentAttribute", 30, 0, 0, false, null, 0, null, null, operation));
        expected.Add(Framework(produces.FullName!, 30, 0, 0) with
        { Status = approved.Protected == ProtectedAdministrationOperation.CreateOrganization ? 201 : 200, ResponseType = response });
        actual.Should().Equal(expected, "Fix: preserve the exact effective action filters, factories, ordering and declared response metadata");

        static EffectiveFilter Framework(string type, int scope, int order, int filterOrder, bool reusable = false) =>
            new("Microsoft.AspNetCore.Mvc.Core", type, scope, order, filterOrder, reusable, null, 0, null, null);
    }

    private static void AssertActionFilterControls(ControllerActionDescriptor action)
    {
        AssertActionFilters(action);
        var original = action.FilterDescriptors.ToArray();
        foreach (var filter in new IFilterMetadata[]
        {
            new UnexpectedAuthorizationFilter(),
            new TypeFilterAttribute(typeof(UnexpectedAuthorizationFilter)),
            new ServiceFilterAttribute(typeof(UnexpectedAuthorizationFilter)),
            typeof(InheritedFilterProbe).GetCustomAttributes(inherit: true).OfType<IFilterMetadata>().Single(),
        })
        {
            action.FilterDescriptors.Add(new FilterDescriptor(filter, 30));
            try { Refuses(); }
            finally { Restore(); }
        }
        action.FilterDescriptors.RemoveAt(0);
        try { Refuses(); }
        finally { Restore(); }
        var descriptor = action.FilterDescriptors[0];
        var order = descriptor.Order;
        descriptor.Order++;
        try { Refuses(); }
        finally { descriptor.Order = order; }

        var normalizer = action.FilterDescriptors.Select(item => item.Filter).OfType<TypeFilterAttribute>().Single();
        var arguments = normalizer.Arguments;
        normalizer.Arguments = ["unexpected"];
        try { Refuses(); }
        finally { normalizer.Arguments = arguments; }
        normalizer.IsReusable = true;
        try { Refuses(); }
        finally { normalizer.IsReusable = false; }
        if (Approved(action).Protected is not null)
        {
            var keyed = action.FilterDescriptors.Single(item => item.Filter is IdempotentAttribute);
            var index = action.FilterDescriptors.IndexOf(keyed);
            action.FilterDescriptors[index] = new FilterDescriptor(new IdempotentAttribute(), 30);
            try { Refuses(); }
            finally { Restore(); }
        }
        AssertActionFilters(action);

        void Refuses()
        {
            Action assert = () => AssertActionFilters(action);
            assert.Should().Throw<Xunit.Sdk.XunitException>("effective attached filter changes cannot hide behind unchanged global options/providers");
        }
        void Restore()
        {
            action.FilterDescriptors.Clear();
            foreach (var filter in original) action.FilterDescriptors.Add(filter);
            AssertActionFilters(action);
        }
    }

    [Theory]
    [InlineData("type-filter")]
    [InlineData("service-filter")]
    [InlineData("inherited-filter")]
    public async Task Attached_filter_conventions_change_effective_actions_without_changing_globals(string shape)
    {
        var filter = shape switch
        {
            "type-filter" => new TypeFilterAttribute(typeof(UnexpectedAuthorizationFilter)),
            "service-filter" => new ServiceFilterAttribute(typeof(UnexpectedAuthorizationFilter)),
            "inherited-filter" => typeof(InheritedFilterProbe).GetCustomAttributes(inherit: true).OfType<IFilterMetadata>().Single(),
            _ => throw new InvalidOperationException("Unknown control"),
        };
        await using var factory = new SurfaceFactory("Production", configureMvc: options =>
            options.Conventions.Add(new ExtraActionConvention(filter, null)));
        using var client = factory.CreateClient();
        AssertGlobalFilters(factory.Services.GetRequiredService<IOptions<MvcOptions>>().Value.Filters);
        AssertFilterProviders(factory.Services.GetServices<IFilterProvider>());
        var actions = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()).OfType<ControllerActionDescriptor>().ToArray();
        AssertActionInventory(actions);
        foreach (var action in actions)
        {
            Action assert = () => AssertActionFilters(action);
            assert.Should().Throw<Xunit.Sdk.XunitException>();
        }
        var context = new AuthorizationFilterContext(new ActionContext(new DefaultHttpContext(), new RouteData(), actions[0]), []);
        new UnexpectedAuthorizationFilter().OnAuthorization(context);
        context.Result.Should().BeOfType<UnauthorizedResult>("this attached capability changes executable authorization, not just documentation");
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("requirement")]
    public async Task Real_endpoint_conventions_cannot_augment_the_authorization_metadata_contract(string shape)
    {
        object metadata = shape == "policy"
            ? new AuthorizationPolicyBuilder().RequireAssertion(_ => false).Build()
            : new UnexpectedRequirementData();
        await using var factory = new SurfaceFactory("Production", configureMvc: options =>
            options.Conventions.Add(new ExtraActionConvention(null, metadata)));
        using var client = factory.CreateClient();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is not null).ToArray();
        AssertActionInventory(endpoints.Select(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()!));
        foreach (var endpoint in endpoints)
        {
            var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()!;
            endpoint.Metadata.Should().Contain(metadata);
            Action assert = () => AssertEndpointAuthorization(action, endpoint.Metadata);
            assert.Should().Throw<Xunit.Sdk.XunitException>();
            AssertEndpointAuthorization(action, endpoint.Metadata.Where(item => !ReferenceEquals(item, metadata)));
        }
    }

    private sealed class ExtraActionConvention(IFilterMetadata? filter, object? metadata) : IApplicationModelConvention
    {
        public void Apply(ApplicationModel application)
        {
            ArgumentNullException.ThrowIfNull(application);
            foreach (var controller in application.Controllers)
                foreach (var action in controller.Actions)
                {
                    if (filter is not null) action.Filters.Add(filter);
                    if (metadata is not null)
                        foreach (var selector in action.Selectors) selector.EndpointMetadata.Add(metadata);
                }
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
    private sealed class AttachedAuthorizationAttribute : Attribute, IAuthorizationFilter
    {
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.Result = new UnauthorizedResult();
        }
    }

    [AttachedAuthorization]
    private class FilterProbe;
    private sealed class InheritedFilterProbe : FilterProbe;

    private sealed class UnexpectedRequirementData : IAuthorizationRequirementData
    {
        public IEnumerable<IAuthorizationRequirement> GetRequirements() =>
            new AuthorizationPolicyBuilder().RequireAssertion(_ => false).Build().Requirements;
    }
}
