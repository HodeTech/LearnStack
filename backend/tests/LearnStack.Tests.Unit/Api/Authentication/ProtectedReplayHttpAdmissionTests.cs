using LearnStack.Infrastructure.Audit;
using System.Reflection;
using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using LearnStack.Api.Identity;
using LearnStack.Api.Tenancy;
using LearnStack.Application.Audit;
using LearnStack.Application.Authorization;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

/// <summary>Real HTTP candidate/coordinator control; PostgreSQL and application disclosure have separate integration proofs.</summary>
public sealed class ProtectedReplayHttpAdmissionTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("01930000-0000-7000-8000-00000000ef01"));
    private static readonly UserId Actor = UserId.From(Guid.Parse("01930000-0000-7000-8000-00000000ef02"));
    private const string Key = "protected-http-gate";

    [Theory]
    [InlineData(ProtectedAdministrationOperation.SetMembershipProfile)]
    [InlineData(ProtectedAdministrationOperation.SetOwnMembershipProfile)]
    [InlineData(ProtectedAdministrationOperation.ChangeMembershipStatus)]
    [InlineData(ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments)]
    [InlineData(ProtectedAdministrationOperation.ChangeRoleGrants)]
    [InlineData(ProtectedAdministrationOperation.AdoptRoleGrantManifest)]
    [InlineData(ProtectedAdministrationOperation.CreateOrganization)]
    [InlineData(ProtectedAdministrationOperation.RenameOrganization)]
    [InlineData(ProtectedAdministrationOperation.ArchiveOrganization)]
    [InlineData(ProtectedAdministrationOperation.SelectDefaultOrganization)]
    public async Task Every_completed_operation_uses_only_its_current_disclosure_policy_and_never_claims_after_http_admission(
        ProtectedAdministrationOperation operation)
    {
        using var harness = await Harness.CreateAsync(operation);
        var original = Stored(harness.Descriptor);
        harness.Store.ReadCompletedAsync(Tenant, Key, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(original);
        harness.Authorizer.AllowedType = ProtectedReplayHttpAdmission.ReceiptRequest(operation);
        Assert.True((await harness.AdmitAsync()).IsSuccess);
        Assert.Equal(harness.Authorizer.AllowedType, Assert.Single(harness.Authorizer.Requests));
        // The row can expire, be recycled, or become unavailable after the lookup.
        // The already admitted snapshot remains disclosure-only and detached.
        original.Body[0] = 0;
        harness.Store.ReadCompletedAsync(Tenant, Key, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IdempotentResponse?)null);
        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<IRequest<Result<None>>>(), Arg.Any<CancellationToken>()).Returns(Result.Ok(None.Value));
        var cleanup = new CompletedCleanup();
        var filter = new ProtectedIdempotencyFilter(harness.Store, harness.Context, cleanup, sender,
            NullLogger<ProtectedIdempotencyFilter>.Instance, operation);
        var context = ResourceContext(harness.Http);
        await filter.OnResourceExecutionAsync(context, () => throw new InvalidOperationException("Replay cannot dispatch the mutation."));
        Assert.Equal("{\"version\":\"8\"}", System.Text.Encoding.UTF8.GetString(((MemoryStream)harness.Http.Response.Body).ToArray()));
        Assert.Equal("true", harness.Http.Response.Headers[IdempotentAttribute.ReplayedHeaderName].ToString());
        AssertNoMutableCalls(harness.Store);
        Assert.Single(sender.ReceivedCalls());
    }

    [Fact]
    public async Task Absent_completion_checks_the_original_mutation_policy_and_denial_creates_no_claim_or_read_binding()
    {
        using var harness = await Harness.CreateAsync(ProtectedAdministrationOperation.SetMembershipProfile);
        harness.Authorizer.AllowedType = ProtectedReplayHttpAdmission.ReceiptRequest(harness.Descriptor.Operation);
        var result = await harness.AdmitAsync();
        Assert.Equal("forbidden", result.Error!.Code);
        Assert.Equal(harness.Descriptor.RequestType, Assert.Single(harness.Authorizer.Requests));
        Assert.Equal(ProtectedHttpAdmissionKind.Refused, (await harness.ConsumeAsync()).Kind);
        AssertNoMutableCalls(harness.Store);
    }

    [Fact]
    public async Task Absent_completion_can_bind_mutation_only_after_the_original_policy_allows_it()
    {
        using var harness = await Harness.CreateAsync(ProtectedAdministrationOperation.SetMembershipProfile);
        harness.Authorizer.AllowedType = harness.Descriptor.RequestType;
        Assert.True((await harness.AdmitAsync()).IsSuccess);
        Assert.Equal(harness.Descriptor.RequestType, Assert.Single(harness.Authorizer.Requests));
        Assert.Equal(ProtectedHttpAdmissionKind.Mutation, (await harness.ConsumeAsync()).Kind);
        AssertNoMutableCalls(harness.Store);
    }

    [Fact]
    public async Task Existing_completion_does_not_use_mutation_permission_when_current_disclosure_is_denied()
    {
        using var harness = await Harness.CreateAsync(ProtectedAdministrationOperation.SetMembershipProfile);
        harness.Store.ReadCompletedAsync(Tenant, Key, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Stored(harness.Descriptor));
        harness.Authorizer.AllowedType = harness.Descriptor.RequestType;
        Assert.Equal("forbidden", (await harness.AdmitAsync()).Error!.Code);
        Assert.Equal(ProtectedReplayHttpAdmission.ReceiptRequest(harness.Descriptor.Operation), Assert.Single(harness.Authorizer.Requests));
        Assert.Equal(ProtectedHttpAdmissionKind.Refused, (await harness.ConsumeAsync()).Kind);
        AssertNoMutableCalls(harness.Store);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("duplicate")]
    [InlineData("wrong-operation")]
    public async Task Noncanonical_idempotency_metadata_refuses_before_store_or_policy_evaluation(string defect)
    {
        using var harness = await Harness.CreateAsync(ProtectedAdministrationOperation.RenameOrganization);
        var endpoint = harness.Http.GetEndpoint()!;
        var metadata = endpoint.Metadata.Where(item => item is not IdempotentAttribute).ToList();
        if (defect == "duplicate")
        {
            metadata.Add(new IdempotentAttribute(ProtectedAdministrationOperation.RenameOrganization));
            metadata.Add(new IdempotentAttribute(ProtectedAdministrationOperation.RenameOrganization));
        }
        if (defect == "wrong-operation") metadata.Add(new IdempotentAttribute(ProtectedAdministrationOperation.ArchiveOrganization));
        harness.Http.SetEndpoint(new Endpoint(endpoint.RequestDelegate, new EndpointMetadataCollection(metadata), endpoint.DisplayName));
        Assert.Equal("forbidden", (await harness.AdmitAsync()).Error!.Code);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.ReceivedCalls());
        Assert.Equal(ProtectedHttpAdmissionKind.Refused, (await harness.ConsumeAsync()).Kind);
    }

    [Theory]
    [InlineData("missing-action-declaration")]
    [InlineData("missing-filter")]
    [InlineData("controller-filter-scope")]
    [InlineData("global-filter-scope")]
    [InlineData("earlier-filter-order")]
    [InlineData("later-filter-order")]
    [InlineData("independent-action-declaration")]
    [InlineData("independent-filter")]
    [InlineData("independent-endpoint-copy")]
    [InlineData("additional-action-declaration")]
    [InlineData("additional-filter")]
    public async Task Inconsistent_mvc_key_declarations_cannot_admit_replay_or_mutation(string defect)
    {
        using var harness = await Harness.CreateAsync(ProtectedAdministrationOperation.RenameOrganization);
        var endpoint = harness.Http.GetEndpoint()!;
        var action = endpoint.Metadata.GetRequiredMetadata<ControllerActionDescriptor>();
        var keyed = Assert.Single(action.EndpointMetadata.OfType<IdempotentAttribute>());
        var independent = new IdempotentAttribute(ProtectedAdministrationOperation.RenameOrganization);
        switch (defect)
        {
            case "missing-action-declaration":
                action.EndpointMetadata = action.EndpointMetadata.Where(item => item is not IdempotentAttribute).ToList();
                break;
            case "missing-filter":
                action.FilterDescriptors = [];
                break;
            case "controller-filter-scope":
                action.FilterDescriptors = [new FilterDescriptor(keyed, 20)];
                break;
            case "global-filter-scope":
                action.FilterDescriptors = [new FilterDescriptor(keyed, 10)];
                break;
            case "earlier-filter-order":
                action.FilterDescriptors = [new FilterDescriptor(keyed, 30) { Order = -1 }];
                break;
            case "later-filter-order":
                action.FilterDescriptors = [new FilterDescriptor(keyed, 30) { Order = 1 }];
                break;
            case "independent-action-declaration":
                action.EndpointMetadata = action.EndpointMetadata.Select(item => item is IdempotentAttribute ? independent : item).ToList();
                break;
            case "independent-filter":
                action.FilterDescriptors = [new FilterDescriptor(independent, 30)];
                break;
            case "independent-endpoint-copy":
                var metadata = endpoint.Metadata.ToList();
                metadata[metadata.FindLastIndex(item => item is IdempotentAttribute)] = independent;
                harness.Http.SetEndpoint(new Endpoint(endpoint.RequestDelegate, new EndpointMetadataCollection(metadata), endpoint.DisplayName));
                break;
            case "additional-action-declaration":
                action.EndpointMetadata = [.. action.EndpointMetadata, independent];
                break;
            case "additional-filter":
                action.FilterDescriptors = [.. action.FilterDescriptors, new FilterDescriptor(independent, 30)];
                break;
        }

        // The remaining endpoint, operation, credential and input are valid.
        // A broken MVC declaration cannot even perform the nonmutating lookup.
        Assert.Equal("forbidden", (await harness.AdmitAsync()).Error!.Code);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.ReceivedCalls());
        Assert.Equal(ProtectedHttpAdmissionKind.Refused, (await harness.ConsumeAsync()).Kind);
        Assert.Equal(0, harness.Http.Response.Body.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_foreign_method_alias_or_different_action_cannot_reuse_valid_endpoint_declarations(bool foreignAlias)
    {
        using var harness = await Harness.CreateAsync(ProtectedAdministrationOperation.SetMembershipProfile);
        var action = harness.Http.GetEndpoint()!.Metadata.GetRequiredMetadata<ControllerActionDescriptor>();
        action.MethodInfo = foreignAlias
            ? typeof(OwnMembershipController).GetMethod(nameof(OwnMembershipController.SetProfile))!
            : typeof(MembershipsController).GetMethod(nameof(MembershipsController.ReplaceRoles))!;

        Assert.Equal("forbidden", (await harness.AdmitAsync()).Error!.Code);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.ReceivedCalls());
        Assert.Equal(ProtectedHttpAdmissionKind.Refused, (await harness.ConsumeAsync()).Kind);
        Assert.Equal(0, harness.Http.Response.Body.Length);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("tenant")]
    [InlineData("organization")]
    [InlineData("origin")]
    [InlineData("context")]
    [InlineData("credential")]
    [InlineData("source-closed")]
    [InlineData("endpoint")]
    [InlineData("key")]
    [InlineData("body")]
    [InlineData("precondition")]
    [InlineData("method")]
    [InlineData("path")]
    [InlineData("query")]
    public async Task A_changed_bound_input_or_source_refuses_without_falling_back_to_mutation(string changed)
    {
        using var harness = await Harness.CreateAsync(ProtectedAdministrationOperation.RenameOrganization);
        harness.Store.ReadCompletedAsync(Tenant, Key, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Stored(harness.Descriptor));
        Assert.True((await harness.AdmitAsync()).IsSuccess);
        var context = harness.Context;
        switch (changed)
        {
            case "actor": context.UserId.Returns(UserId.From(Guid.CreateVersion7())); break;
            case "tenant": context.TenantId.Returns(TenantId.From(Guid.CreateVersion7())); break;
            case "organization": context.OrganizationId.Returns(OrganizationId.From(Guid.CreateVersion7())); break;
            case "origin": context.Origin.Returns(TenantContextOrigin.HostAndClaim); break;
            case "context": context = Context(); break;
            case "credential": harness.Http.Features.Get<TenantCredentialFeature>()!.Identity = null; break;
            case "source-closed": harness.Candidates.Dispose(); break;
            case "endpoint": harness.Http.SetEndpoint(Endpoint(harness.Descriptor.Operation)); break;
            case "key": harness.Http.Request.Headers[IdempotentAttribute.HeaderName] = "different-key"; break;
            case "body": harness.Http.Request.Body = new MemoryStream("{\"name\":\"changed\"}"u8.ToArray()); break;
            case "precondition": harness.Http.Request.Headers.IfMatch = "\"8\""; break;
            case "method": harness.Http.Request.Method = "POST"; break;
            case "path": harness.Http.Request.Path = "/changed"; break;
            case "query": harness.Http.Request.QueryString = new QueryString("?changed=true"); break;
        }
        var filter = new ProtectedIdempotencyFilter(harness.Store, context, Substitute.For<IIdempotencyBusinessOwnerCleanup>(),
            Substitute.For<ISender>(), NullLogger<ProtectedIdempotencyFilter>.Instance, harness.Descriptor.Operation);
        var resource = ResourceContext(harness.Http);
        await filter.OnResourceExecutionAsync(resource, () => throw new InvalidOperationException("Changed replay cannot dispatch."));
        var refusal = Assert.IsType<ProblemDetailsActionResult>(resource.Result);
        Assert.Equal("forbidden", refusal.Error.Code);
        AssertNoMutableCalls(harness.Store);
        Assert.Equal(0, harness.Http.Response.Body.Length);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wildcard")]
    [InlineData("weak")]
    [InlineData("list")]
    [InlineData("noncanonical")]
    [InlineData("duplicate")]
    public async Task Invalid_preconditions_are_400_validation_before_lookup_or_policy_evaluation(string defect)
    {
        using var harness = await Harness.CreateAsync(ProtectedAdministrationOperation.RenameOrganization);
        harness.Http.Request.Headers.IfMatch = defect switch
        {
            "wildcard" => "*",
            "weak" => "W/\"7\"",
            "list" => "\"7\",\"8\"",
            "noncanonical" => "\"07\"",
            "duplicate" => new Microsoft.Extensions.Primitives.StringValues(["\"7\"", "\"7\""]),
            _ => default,
        };
        var result = await harness.AdmitAsync();
        Assert.Equal("validation_failed", result.Error!.Code);
        Assert.Equal(StatusCodes.Status400BadRequest, HttpStatusMap.For(result.Error));
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.ReceivedCalls());
    }

    [Fact]
    public async Task Actual_body_bytes_are_bounded_before_fingerprinting_or_store_lookup()
    {
        using var harness = await Harness.CreateAsync(ProtectedAdministrationOperation.RenameOrganization);
        harness.Http.Request.Body = new MemoryStream(new byte[AdministrationHttpInput.MaxBodyBytes + 1]);
        var failure = await Assert.ThrowsAsync<BadHttpRequestException>(() => harness.AdmitAsync());
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, failure.StatusCode);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.ReceivedCalls());
        Assert.Equal(0, harness.Http.Request.Body.Position);
    }

    [Fact]
    public async Task Binding_is_single_use_and_metadata_or_browser_hints_cannot_create_one()
    {
        using var harness = await Harness.CreateAsync(ProtectedAdministrationOperation.RenameOrganization);
        harness.Http.Items["IdempotencyReplay"] = Stored(harness.Descriptor);
        harness.Http.Request.Headers["Idempotency-Replayed"] = "true";
        Assert.Equal(ProtectedHttpAdmissionKind.Refused, (await harness.ConsumeAsync()).Kind);
        harness.Store.ReadCompletedAsync(Tenant, Key, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Stored(harness.Descriptor));
        Assert.True((await harness.AdmitAsync()).IsSuccess);
        Assert.Equal(ProtectedHttpAdmissionKind.Replay, (await harness.ConsumeAsync()).Kind);
        Assert.Equal(ProtectedHttpAdmissionKind.Refused, (await harness.ConsumeAsync()).Kind);
        var feature = typeof(ProtectedReplayHttpAdmission).GetNestedType("Admission", BindingFlags.NonPublic)!;
        Assert.True(feature.IsSealed);
        Assert.True(feature.IsNestedPrivate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removed_or_transplanted_private_binding_cannot_authorize_a_production_mutation(bool transplant)
    {
        using var original = await Harness.CreateAsync(ProtectedAdministrationOperation.RenameOrganization);
        original.Store.ReadCompletedAsync(Tenant, Key, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Stored(original.Descriptor));
        Assert.True((await original.AdmitAsync()).IsSuccess);
        var featureType = typeof(ProtectedReplayHttpAdmission).GetNestedType("Admission", BindingFlags.NonPublic)!;
        var binding = original.Http.Features[featureType];
        Assert.NotNull(binding);
        if (transplant)
        {
            using var replacement = await Harness.CreateAsync(ProtectedAdministrationOperation.RenameOrganization);
            replacement.Http.Features[featureType] = binding;
            Assert.Equal(ProtectedHttpAdmissionKind.Refused, (await replacement.ConsumeAsync()).Kind);
            Assert.Empty(replacement.Store.ReceivedCalls());
        }
        else
        {
            original.Http.Features[featureType] = null;
            Assert.Equal(ProtectedHttpAdmissionKind.Refused, (await original.ConsumeAsync()).Kind);
        }
    }

    private static ResourceExecutingContext ResourceContext(HttpContext http) =>
        new(new ActionContext(http, new RouteData(), new ActionDescriptor()), [], []);

    private static IdempotentResponse Stored(ProtectedAdministrationDescriptor descriptor)
    {
        var target = new IdempotencyReplayTarget(descriptor.TargetKind,
            descriptor.TargetKind == IdempotencyReplayTargetKind.Tenant ? Tenant.Value : Guid.CreateVersion7());
        var headers = new Dictionary<string, IReadOnlyList<string>>
        {
            ["ETag"] = ["\"8\""],
            ["Content-Type"] = ["application/json; charset=utf-8"],
        };
        if (descriptor.Creates) headers["Location"] = [ProtectedAdministrationResult.LocationFor(target.Id)];
        return new(descriptor.Creates ? 201 : 200, "application/json; charset=utf-8", headers,
            ProtectedAdministrationResult.BodyFor(8), target);
    }

    private static ITenantContext Context()
    {
        var context = Substitute.For<ITenantContext>();
        context.IsResolved.Returns(true);
        context.TenantId.Returns(Tenant);
        context.UserId.Returns(Actor);
        context.Origin.Returns(TenantContextOrigin.ClaimAndMembership);
        return context;
    }

    private static Endpoint Endpoint(ProtectedAdministrationOperation operation)
    {
        var (controller, action, name) = operation switch
        {
            ProtectedAdministrationOperation.SetMembershipProfile => (typeof(MembershipsController), "SetProfile", "SetMembershipProfile"),
            ProtectedAdministrationOperation.SetOwnMembershipProfile => (typeof(OwnMembershipController), "SetProfile", "SetOwnMembershipProfile"),
            ProtectedAdministrationOperation.ChangeMembershipStatus => (typeof(MembershipsController), "ChangeStatus", "ChangeMembershipStatus"),
            ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments => (typeof(MembershipsController), "ReplaceRoles", "ReplaceMembershipRoleAssignments"),
            ProtectedAdministrationOperation.ChangeRoleGrants => (typeof(RolesController), "ChangeGrants", "ChangeRoleGrants"),
            ProtectedAdministrationOperation.AdoptRoleGrantManifest => (typeof(RolesController), "AdoptManifest", "AdoptRoleGrantManifest"),
            ProtectedAdministrationOperation.CreateOrganization => (typeof(OrganizationsController), "Create", "CreateManagedOrganization"),
            ProtectedAdministrationOperation.RenameOrganization => (typeof(OrganizationsController), "Rename", "RenameOrganization"),
            ProtectedAdministrationOperation.ArchiveOrganization => (typeof(OrganizationsController), "Archive", "ArchiveOrganization"),
            ProtectedAdministrationOperation.SelectDefaultOrganization => (typeof(DefaultOrganizationController), "Select", "SelectDefaultOrganization"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        var method = controller.GetMethod(action)!;
        var verb = operation is ProtectedAdministrationOperation.CreateOrganization or ProtectedAdministrationOperation.ArchiveOrganization
            or ProtectedAdministrationOperation.AdoptRoleGrantManifest ? "POST" : "PUT";
        var attributes = method.GetCustomAttributes(inherit: true).ToList();
        var keyed = Assert.Single(attributes.OfType<IdempotentAttribute>());
        var descriptor = new ControllerActionDescriptor
        {
            ControllerTypeInfo = controller.GetTypeInfo(),
            MethodInfo = method,
            ControllerName = controller.Name,
            ActionName = action,
            EndpointMetadata = attributes,
            FilterDescriptors = [new FilterDescriptor(keyed, 30)],
        };
        return new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(
            attributes.Concat(controller.GetCustomAttributes(inherit: true)).Concat(new object[]
            {
                // Match the real MVC action/filter projection, not a one-copy approximation.
                keyed, new EndpointNameMetadata(name), new HttpMethodMetadata([verb]), descriptor,
            })), name);
    }

    private static void AssertNoMutableCalls(IIdempotencyStore store) =>
        Assert.DoesNotContain(store.ReceivedCalls(), call => call.GetMethodInfo().Name
            is nameof(IIdempotencyStore.TryClaimAsync) or nameof(IIdempotencyStore.CompleteAsync) or nameof(IIdempotencyStore.AbandonAsync));

    private sealed class CompletedCleanup : IIdempotencyBusinessOwnerCleanup
    {
        public ValueTask<bool> EndBusinessOwnerAsync() => ValueTask.FromResult(true);
    }

    private sealed class Harness : IDisposable
    {
        internal required DefaultHttpContext Http { get; init; }
        internal required ITenantContext Context { get; init; }
        internal required OperationHttpClassifier Classifier { get; init; }
        internal required OperationPolicy Policy { get; init; }
        internal required ProtectedAdministrationDescriptor Descriptor { get; init; }
        internal required TenantAuthorizationCandidateSource Candidates { get; init; }
        internal required OperationAuthorizationCoordinator Coordinator { get; init; }
        internal required Authorizer Authorizer { get; init; }
        internal IIdempotencyStore Store { get; } = Substitute.For<IIdempotencyStore>();
        internal Task<Result<None>> AdmitAsync() => ProtectedReplayHttpAdmission.AuthorizeAsync(Http, Policy, Classifier,
            Context, Store, Candidates, () => Coordinator);
        internal async Task<ProtectedHttpAdmission> ConsumeAsync()
        {
            long? expected = Descriptor.Creates ? null : 7;
            var fingerprint = await ProtectedIdempotencyFilter.ComputeFingerprintAsync(Http, Context, Descriptor, expected, default);
            return ProtectedReplayHttpAdmission.Consume(Http, Context, Descriptor, Key, fingerprint, expected);
        }

        internal static async Task<Harness> CreateAsync(ProtectedAdministrationOperation operation)
        {
            var http = new DefaultHttpContext();
            http.SetEndpoint(Endpoint(operation));
            http.Request.Method = http.GetEndpoint()!.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods[0];
            http.Request.Path = "/gate-proof";
            http.Request.Headers[IdempotentAttribute.HeaderName] = Key;
            if (operation != ProtectedAdministrationOperation.CreateOrganization) http.Request.Headers.IfMatch = "\"7\"";
            http.Request.Body = new MemoryStream("{}"u8.ToArray());
            http.Response.Body = new MemoryStream();
            http.Features.Set(HostClassification.Platform("platform.example"));
            http.Features.Set(new TenantCredentialFeature
            {
                VerifiedSubject = new VerifiedProviderSubject("https://issuer.example/realms/learnstack", "gate-subject", null, Tenant, null),
            });
            var binding = new TenantIdentityBindingAdmissionMiddleware(_ => Task.CompletedTask,
                NullLogger<TenantIdentityBindingAdmissionMiddleware>.Instance);
            await using var limiter = new TenantSubjectRateLimiter();
            await new TenantSubjectAdmissionMiddleware(request => binding.InvokeAsync(request, new ExistingBinding()), limiter).InvokeAsync(http);
            var candidates = new TenantAuthorizationCandidateSource(new FixedAccessor { HttpContext = http });
            var context = ProtectedReplayHttpAdmissionTests.Context();
            var catalog = new OperationPolicyCatalog([new IdentityOperationPolicySource(), new TenancyOperationPolicySource()]);
            var classifier = new OperationHttpClassifier(catalog);
            Assert.Equal(HttpOperationClassification.Application, classifier.Classify(http.GetEndpoint(), out var policy));
            OperationAuthorizationCoordinator? coordinator = null;
            var evidence = new AuthorizationEvidenceCoordinator(Substitute.For<IAuditStore>(), new AuditStateCapture(),
                Substitute.For<IUnitOfWork>(), new SystemClock(), new SystemGuidFactory(), () => coordinator!, [],
                NullLogger<AuthorizationEvidenceCoordinator>.Instance);
            var authorizer = new Authorizer();
            coordinator = new(catalog, authorizer, context, [candidates], [], evidence);
            return new()
            {
                Http = http,
                Context = context,
                Classifier = classifier,
                Policy = policy!,
                Descriptor = ProtectedAdministrationDescriptor.For(operation),
                Candidates = candidates,
                Coordinator = coordinator,
                Authorizer = authorizer
            };
        }
        public void Dispose() { Coordinator.Dispose(); Candidates.Dispose(); }
    }

    private sealed class ExistingBinding : ITenantIdentityBindingResolver
    {
        public Task<IdentityBindingResult> ResolveExistingAsync(VerifiedProviderSubject subject, CancellationToken cancellationToken = default) =>
            Task.FromResult(IdentityBindingResult.Bound(Actor, created: false));
    }

    private sealed class FixedAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class Authorizer : ProtectedOperationDecision.MintingSource, IProtectedOperationAuthorizer
    {
        internal List<Type> Requests { get; } = [];
        internal Type? AllowedType { get; set; }
        public Task<ProtectedOperationDecision> AuthorizeAsync(OperationInvocation invocation, OperationPolicy policy,
            AuthorizationCandidate candidate, CancellationToken cancellationToken = default)
        {
            Requests.Add(policy.RequestType);
            var allowed = AllowedType is null || policy.RequestType == AllowedType;
            return Task.FromResult(ForDecision(invocation, candidate,
                allowed ? ProtectedOperationOutcome.Allowed : ProtectedOperationOutcome.Forbidden,
                allowed ? [Guid.CreateVersion7()] : [], matchedClause: allowed && policy.Kind == OperationPolicyKind.Protected ? policy.Alternatives[0] : null));
        }
    }
}
