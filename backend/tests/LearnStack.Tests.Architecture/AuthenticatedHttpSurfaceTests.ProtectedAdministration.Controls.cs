using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuthenticatedHttpSurfaceTests
{
    private static void AssertFiniteMetadataControls(ModuleDefinition[] modules, CensusFacts facts)
    {
        var map = Types(modules).ToDictionary(Key, StringComparer.Ordinal);
        foreach (var endpoint in Endpoints)
        {
            var controller = map["LearnStack.Api:" + endpoint.Controller];
            var action = controller.Methods.Single(method => method.Name == endpoint.Action);
            void Dirty(string prefix) => AttachedFilterOffenders(controller, action, map)
                .Should().Contain(error => error.StartsWith(prefix, StringComparison.Ordinal));
            AttachedFilterOffenders(controller, action, map).Should().BeEmpty();
            var response = action.CustomAttributes.Single(attribute => attribute.AttributeType.GetElementType().Name is "ProducesResponseTypeAttribute" or "ProducesResponseTypeAttribute`1");
            var status = response.ConstructorArguments[0];
            response.ConstructorArguments[0] = new CustomAttributeArgument(controller.Module.TypeSystem.Int32, endpoint.Status == 200 ? 201 : 200);
            try { Dirty("Unexpected attached MVC filter metadata:"); }
            finally { response.ConstructorArguments[0] = status; }
            action.CustomAttributes.Remove(response);
            try
            {
                Dirty("Unexpected attached MVC filter metadata:");
                var wrong = new CustomAttribute(controller.Module.ImportReference(typeof(ProducesResponseTypeAttribute<string>).GetConstructor([typeof(int)])!));
                wrong.ConstructorArguments.Add(status);
                action.CustomAttributes.Add(wrong);
                try { Dirty("Unexpected attached MVC filter metadata:"); }
                finally { action.CustomAttributes.Remove(wrong); }
            }
            finally { action.CustomAttributes.Add(response); }
            action.CustomAttributes.Add(response);
            try { Dirty("Unexpected attached MVC filter metadata:"); }
            finally { action.CustomAttributes.RemoveAt(action.CustomAttributes.Count - 1); }

            var operation = action.CustomAttributes.Single(attribute => attribute.AttributeType.FullName == "LearnStack.Api.Authentication.OperationPolicyAttribute");
            var request = operation.ConstructorArguments[0];
            operation.ConstructorArguments[0] = new CustomAttributeArgument(request.Type, controller.Module.TypeSystem.String);
            try { Dirty("Unexpected operation request metadata:"); }
            finally { operation.ConstructorArguments[0] = request; }
            action.CustomAttributes.Remove(operation);
            try { Dirty("Unexpected operation request metadata:"); }
            finally { action.CustomAttributes.Add(operation); }
            action.CustomAttributes.Add(operation);
            try { Dirty("Unexpected operation request metadata:"); }
            finally { action.CustomAttributes.RemoveAt(action.CustomAttributes.Count - 1); }

            if (endpoint.ProtectedOperation is { } key)
            {
                var marker = action.CustomAttributes.Single(attribute => attribute.AttributeType.FullName == IdempotentMetadata);
                var value = marker.ConstructorArguments[0];
                foreach (var wrong in new[] { key == 1 ? 2 : 1, 0, int.MaxValue })
                {
                    marker.ConstructorArguments[0] = new CustomAttributeArgument(value.Type, wrong);
                    try { Dirty("Unexpected attached MVC filter metadata:"); }
                    finally { marker.ConstructorArguments[0] = value; }
                }
                action.CustomAttributes.Remove(marker);
                try { Dirty("Unexpected attached MVC filter metadata:"); }
                finally { action.CustomAttributes.Add(marker); }
                action.CustomAttributes.Add(marker);
                try { Dirty("Unexpected attached MVC filter metadata:"); }
                finally { action.CustomAttributes.RemoveAt(action.CustomAttributes.Count - 1); }
            }
            var genericKey = new CustomAttribute(controller.Module.ImportReference(typeof(LearnStack.Api.Idempotency.IdempotentAttribute).GetConstructor(Type.EmptyTypes)!));
            action.CustomAttributes.Add(genericKey);
            try { Dirty("Unexpected attached MVC filter metadata:"); }
            finally { action.CustomAttributes.Remove(genericKey); }
            AttachedFilterOffenders(controller, action, map).Should().BeEmpty("restoring this exact row restores its own metadata contract");
        }
    }

    private static IEnumerable<string> HttpBypasses(TypeDefinition root, Dictionary<string, TypeDefinition> map) =>
        PublicSurfaceTests.PersistenceOffenders([root], map, type => HttpReferences(type, map),
                type => Key(type) == "LearnStack.Api:" + ProtectedResult && ProtectedDispatcher(type, map))
            .Select(error => "HTTP persistence/handler/helper bypass: " + error);

    private static void AssertProtectedAdministrationControls(ModuleDefinition[] modules, CensusFacts facts)
    {
        var map = Types(modules).ToDictionary(Key, StringComparer.Ordinal);
        var carrier = map["LearnStack.Api:" + ProtectedResult];
        ProtectedDispatcher(carrier, map).Should().BeTrue();
        ProtectedAdministrationOffenders(modules, map).Should().BeEmpty();
        foreach (var endpoint in Endpoints.Where(entry => entry.ProtectedOperation is not null))
        {
            var controller = map["LearnStack.Api:" + endpoint.Controller];
            var action = controller.Methods.Single(method => method.Name == endpoint.Action);
            ProtectedHandoff(controller, action, endpoint).Should().BeTrue();
            var body = ActionBodies(controller, action.Name).Single(method => method.Body.Instructions.Any(instruction => instruction.Operand is MethodReference target && HandoffSignature(target)));
            var handoff = body.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target && HandoffSignature(target));
            var request = Argument(body, handoff, 3)!;
            var original = request.Operand;
            var foreign = Endpoints.First(entry => entry.ProtectedOperation is not null && entry.Request != endpoint.Request);
            request.Operand = controller.Module.ImportReference(map[foreign.Request.Assembly.GetName().Name + ":" + foreign.Request.FullName]
                .Methods.Single(method => method.IsConstructor && method.IsPublic));
            try { ProtectedHandoff(controller, action, endpoint).Should().BeFalse("another accepted command is not this action's request"); }
            finally { request.Operand = original; }
            var index = body.Body.Instructions.IndexOf(handoff);
            body.Body.Instructions.Remove(handoff);
            try { ProtectedHandoff(controller, action, endpoint).Should().BeFalse("missing dispatch cannot qualify by metadata alone"); }
            finally { body.Body.Instructions.Insert(index, handoff); }
            var extra = Instruction.Create(OpCodes.Call, (MethodReference)handoff.Operand);
            body.Body.Instructions.Add(extra);
            try { ProtectedHandoff(controller, action, endpoint).Should().BeFalse("exactly one handoff is permitted"); }
            finally { body.Body.Instructions.Remove(extra); }
        }
        var sends = ActionBodies(carrier, "ExecuteAsync").SelectMany(method => method.Body.Instructions
            .Where(instruction => instruction.Operand is MethodReference { DeclaringType.FullName: Sender, Name: "Send" })
            .Select(instruction => (Method: method, Instruction: instruction))).ToArray();
        var first = sends[0];
        var sendIndex = first.Method.Body.Instructions.IndexOf(first.Instruction);
        first.Method.Body.Instructions.Remove(first.Instruction);
        try { ProtectedDispatcher(carrier, map).Should().BeFalse("all ten typed dispatch branches are required"); }
        finally { first.Method.Body.Instructions.Insert(sendIndex, first.Instruction); }
        var duplicate = Instruction.Create(OpCodes.Callvirt, (MethodReference)first.Instruction.Operand);
        first.Method.Body.Instructions.Add(duplicate);
        try { ProtectedDispatcher(carrier, map).Should().BeFalse("an extra typed send is forbidden even for an admitted command"); }
        finally { first.Method.Body.Instructions.Remove(duplicate); }
        var sameResult = sends.Where(site => ((GenericInstanceMethod)site.Instruction.Operand).GenericArguments[0].FullName
            == ((GenericInstanceMethod)first.Instruction.Operand).GenericArguments[0].FullName).Take(2).ToArray();
        sameResult.Should().HaveCount(2);
        var left = Argument(sameResult[0].Method, sameResult[0].Instruction, 0)!;
        var right = Argument(sameResult[1].Method, sameResult[1].Instruction, 0)!;
        var leftCode = left.OpCode; var leftOperand = left.Operand;
        var rightCode = right.OpCode; var rightOperand = right.Operand;
        left.OpCode = rightCode; left.Operand = rightOperand;
        right.OpCode = leftCode; right.Operand = leftOperand;
        try { ProtectedDispatcher(carrier, map).Should().BeFalse("swapping two same-result request producers must fail even though both type sets and send counts are unchanged"); }
        finally { left.OpCode = leftCode; left.Operand = leftOperand; right.OpCode = rightCode; right.Operand = rightOperand; }

        var controllerRoot = map["LearnStack.Api:LearnStack.Api.Identity.MembershipsController"];
        HttpBypasses(controllerRoot, map).Should().BeEmpty();
        CarrierMetadataReadOnly(carrier).Should().BeTrue("both ordinary call opcodes may read the exact guarded metadata getter");
        AssertCarrierMetadataCapabilityControls(carrier, controllerRoot, map);
        AssertCarrierMetadataFetchControls(carrier, controllerRoot, map);
        var keyRead = ActionBodies(carrier, "ExecuteAsync").SelectMany(method => method.Body.Instructions)
            .Single(instruction => instruction.Operand is MethodReference { DeclaringType.FullName: IdempotentMetadata, Name: "get_ProtectedOperation" });
        var getterOpcode = keyRead.OpCode;
        keyRead.OpCode = OpCodes.Ldftn;
        try { CarrierMetadataReadOnly(carrier).Should().BeFalse("a method pointer is not the validated metadata property read"); }
        finally { keyRead.OpCode = getterOpcode; }
        var factoryCall = Instruction.Create(OpCodes.Callvirt, map["LearnStack.Api:" + IdempotentMetadata].Methods.Single(method => method.Name == "CreateInstance"));
        first.Method.Body.Instructions.Add(factoryCall);
        try
        {
            CarrierMetadataReadOnly(carrier).Should().BeFalse("the approved metadata read never admits an actual factory invocation in the carrier");
            HttpBypasses(controllerRoot, map).Should().Contain(error => error.StartsWith("HTTP persistence/handler/helper bypass:", StringComparison.Ordinal));
        }
        finally { first.Method.Body.Instructions.Remove(factoryCall); }

        foreach (var owner in new[] { controllerRoot, carrier })
        {
            var field = new FieldDefinition("HiddenDatabase", FieldAttributes.Private, owner.Module.ImportReference(typeof(Npgsql.NpgsqlDataSource)));
            owner.Fields.Add(field);
            try { HttpBypasses(controllerRoot, map).Should().Contain(error => error.StartsWith("HTTP persistence/handler/helper bypass:", StringComparison.Ordinal)); }
            finally { owner.Fields.Remove(field); }
        }
        var actionBody = ActionBodies(controllerRoot, "SetProfile").Single(method => method.Name == "MoveNext");
        foreach (var target in new[]
                 {
                     map["LearnStack.Api:" + IdempotentMetadata].Methods.Single(method => method.IsConstructor && method.Parameters.Count == 0),
                     map["LearnStack.Api:" + IdempotentMetadata].Methods.Single(method => method.Name == "CreateInstance"),
                     map["LearnStack.Api:LearnStack.Api.Idempotency.ProtectedIdempotencyFilter"].Methods.Single(method => method.IsConstructor && !method.IsStatic)
                 })
        {
            var instruction = Instruction.Create(target.IsConstructor ? OpCodes.Newobj : OpCodes.Callvirt, target);
            actionBody.Body.Instructions.Add(instruction);
            try { HttpBypasses(controllerRoot, map).Should().Contain(error => error.StartsWith("HTTP persistence/handler/helper bypass:", StringComparison.Ordinal), "attached key metadata never authorizes an actual factory or filter invocation"); }
            finally { actionBody.Body.Instructions.Remove(instruction); }
        }
        var helper = new TypeDefinition("SurfaceProbe", "KeyedHelper", TypeAttributes.NotPublic, controllerRoot.Module.TypeSystem.Object);
        controllerRoot.Module.Types.Add(helper);
        var helperKey = Key(helper);
        map.Add(helperKey, helper);
        var edge = new FieldDefinition("KeyedHelper", FieldAttributes.Private, helper);
        controllerRoot.Fields.Add(edge);
        try
        {
            HttpBypasses(controllerRoot, map).Should().BeEmpty();
            foreach (var capability in new[] { controllerRoot.Module.ImportReference(typeof(Npgsql.NpgsqlDataSource)), controllerRoot.Module.ImportReference(typeof(MediatR.ISender)) })
            {
                var field = new FieldDefinition("Capability", FieldAttributes.Private, capability);
                helper.Fields.Add(field);
                try { HttpBypasses(controllerRoot, map).Should().Contain(error => error.StartsWith("HTTP persistence/handler/helper bypass:", StringComparison.Ordinal)); }
                finally { helper.Fields.Remove(field); }
            }
            var foreignCall = new MethodDefinition("Forward", MethodAttributes.Static | MethodAttributes.Assembly, controllerRoot.Module.TypeSystem.Void);
            foreignCall.Parameters.Add(new ParameterDefinition(map["LearnStack.SharedKernel:" + TenantContext]));
            foreignCall.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            helper.Methods.Add(foreignCall);
            var call = Instruction.Create(OpCodes.Call, foreignCall);
            actionBody.Body.Instructions.Add(call);
            try { ReadOnlyContextBoundary(controllerRoot, map).Should().BeFalse("a context cannot be passed to an arbitrary helper"); }
            finally { actionBody.Body.Instructions.Remove(call); }
        }
        finally { controllerRoot.Fields.Remove(edge); map.Remove(helperKey); controllerRoot.Module.Types.Remove(helper); }
        var context = map["LearnStack.SharedKernel:" + TenantContext];
        ReadOnlyContextBoundary(controllerRoot, map).Should().BeTrue();
        var getter = context.Methods.Single(method => method.Name == "get_TenantId");
        var returnType = getter.ReturnType;
        getter.ReturnType = context.Module.TypeSystem.Object;
        try { ReadOnlyContextBoundary(controllerRoot, map).Should().BeFalse("same getter names do not approve a changed capability-bearing return type"); }
        finally { getter.ReturnType = returnType; }
        var descriptor = map["LearnStack.Api:" + ProtectedDescriptor].Methods.Single(method => method.Name == "For");
        var creation = descriptor.Body.Instructions.First(instruction => instruction.Operand is MethodReference target && target.DeclaringType.FullName == ProtectedDescriptor && target.Name == ".ctor");
        var kind = Argument(descriptor, creation, 2)!;
        var kindCode = kind.OpCode; var kindOperand = kind.Operand;
        kind.OpCode = OpCodes.Ldc_I4_3; kind.Operand = null;
        try { DescriptorOffenders(map).Should().Contain(error => error.StartsWith("Unexpected protected descriptor:", StringComparison.Ordinal)); }
        finally { kind.OpCode = kindCode; kind.Operand = kindOperand; }

        var descriptorOwner = descriptor.DeclaringType;
        var operationFor = descriptorOwner.Methods.Single(method => method.Name == "OperationFor");
        var returnedEnum = operationFor.Body.Instructions.First(instruction => Integer(instruction) == 1);
        var enumCode = returnedEnum.OpCode; var enumOperand = returnedEnum.Operand;
        returnedEnum.OpCode = OpCodes.Ldc_I4_2; returnedEnum.Operand = null;
        try { DescriptorRelationOffenders(descriptorOwner, map).Should().Contain(error => error.Contains("request-to-enum relation", StringComparison.Ordinal)); }
        finally { returnedEnum.OpCode = enumCode; returnedEnum.Operand = enumOperand; }
        var switchInstruction = descriptor.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Switch);
        var branches = (Instruction[])switchInstruction.Operand;
        (branches[0], branches[1]) = (branches[1], branches[0]);
        try { DescriptorRelationOffenders(descriptorOwner, map).Should().Contain(error => error.Contains("enum-to-request relation", StringComparison.Ordinal)); }
        finally { (branches[0], branches[1]) = (branches[1], branches[0]); }
        AssertDescriptorSignatureControls(descriptorOwner, map);
        var descriptorSource = File.ReadAllText(Path.Combine(RepositoryPaths.BackendSrc(), "LearnStack.Api/Idempotency/ProtectedAdministrationOperation.cs"));
        var carrierSource = File.ReadAllText(Path.Combine(RepositoryPaths.BackendSrc(), "LearnStack.Api/Idempotency/ProtectedAdministrationResult.cs"));
        ProtectedSourceOffenders(descriptorSource, carrierSource).Should().BeEmpty();
        ProtectedSourceOffenders(descriptorSource.Replace("SetMembershipProfileCommand => ProtectedAdministrationOperation.SetMembershipProfile", "SetMembershipProfileCommand => ProtectedAdministrationOperation.SetOwnMembershipProfile", StringComparison.Ordinal), carrierSource)
            .Should().Contain("Unexpected protected descriptor source: OperationFor");
        ProtectedSourceOffenders(descriptorSource, carrierSource.Replace("Membership(await sender.Send(command, cancellationToken), command.MembershipId)", "Membership(await sender.Send(command, cancellationToken), null)", StringComparison.Ordinal))
            .Should().Contain(error => error.StartsWith("Unexpected protected typed dispatcher source:", StringComparison.Ordinal));
        NativeScopeAdmissionTests.AssertHttpEntryControls(modules);
        ProtectedAdministrationOffenders(modules, map).Should().BeEmpty();
        HttpBypasses(controllerRoot, map).Should().BeEmpty("all executable capability mutants were restored");
    }
    private static void AssertCarrierMetadataFetchControls(TypeDefinition carrier, TypeDefinition controller,
        Dictionary<string, TypeDefinition> map)
    {
        CarrierMetadataReadOnly(carrier).Should().BeTrue();
        var fetch = ActionBodies(carrier, "ExecuteAsync").SelectMany(method => method.Body.Instructions).Single(CarrierMetadataFetch);
        var original = (GenericInstanceMethod)fetch.Operand;
        void Dirty()
        {
            CarrierMetadataFetch(fetch).Should().BeFalse();
            CarrierMetadataReadOnly(carrier).Should().BeFalse();
            HttpBypasses(controller, map).Should().Contain(error => error.StartsWith("HTTP persistence/handler/helper bypass:", StringComparison.Ordinal));
        }
        foreach (var mutation in new[] { "assembly", "instance", "explicit-this", "return-type", "return-owner", "arity", "parameter", "calling-convention", "argument-wrapper" })
        {
            var element = original.ElementMethod;
            var declaring = mutation == "assembly"
                ? new TypeReference(element.DeclaringType.Namespace, element.DeclaringType.Name, carrier.Module,
                    new AssemblyNameReference("SurfaceProbe.ForeignMetadata", new Version(1, 0))) : element.DeclaringType;
            var replacement = new MethodReference(element.Name, carrier.Module.TypeSystem.Void, declaring)
            {
                HasThis = mutation == "instance" ? !element.HasThis : element.HasThis,
                ExplicitThis = mutation == "explicit-this" ? !element.ExplicitThis : element.ExplicitThis,
                CallingConvention = mutation == "calling-convention" ? MethodCallingConvention.VarArg : element.CallingConvention,
            };
            var parameter = new GenericParameter("T", replacement);
            replacement.GenericParameters.Add(parameter);
            replacement.ReturnType = mutation == "return-type" ? carrier.Module.TypeSystem.Object : parameter;
            if (mutation == "return-owner")
            {
                var unrelated = new MethodReference(element.Name, carrier.Module.TypeSystem.Void, declaring);
                var foreignParameter = new GenericParameter("T", unrelated);
                unrelated.GenericParameters.Add(foreignParameter);
                replacement.ReturnType = foreignParameter;
            }
            if (mutation == "arity") replacement.GenericParameters.Add(new GenericParameter("TExtra", replacement));
            if (mutation == "parameter") replacement.Parameters.Add(new ParameterDefinition(carrier.Module.TypeSystem.Object));
            var changed = new GenericInstanceMethod(replacement);
            changed.GenericArguments.Add(mutation == "argument-wrapper" ? new ArrayType(original.GenericArguments[0]) : original.GenericArguments[0]);
            fetch.Operand = changed;
            try { Dirty(); } finally { fetch.Operand = original; }
            CarrierMetadataReadOnly(carrier).Should().BeTrue("restoring the actual fetch also restores any approved direct metadata local");
            HttpBypasses(controller, map).Should().BeEmpty();
        }
        var originalOpcode = fetch.OpCode;
        foreach (var opcode in new[] { OpCodes.Call, OpCodes.Ldftn, OpCodes.Ldvirtftn })
        {
            fetch.OpCode = opcode;
            try { Dirty(); } finally { fetch.OpCode = originalOpcode; }
            CarrierMetadataReadOnly(carrier).Should().BeTrue();
            HttpBypasses(controller, map).Should().BeEmpty();
        }
    }

    private static void AssertCarrierMetadataCapabilityControls(TypeDefinition carrier, TypeDefinition controller,
        Dictionary<string, TypeDefinition> map)
    {
        var metadata = map["LearnStack.Api:" + IdempotentMetadata];
        var factory = new GenericInstanceType(carrier.Module.ImportReference(typeof(Func<>)));
        factory.GenericArguments.Add(metadata);
        var body = ActionBodies(carrier, "ExecuteAsync").First();
        void Dirty()
        {
            CarrierMetadataReadOnly(carrier).Should().BeFalse("only the approved metadata fetch/getter occurrences may lose their graph edges");
            HttpBypasses(controller, map).Should().Contain(error => error.StartsWith("HTTP persistence/handler/helper bypass:", StringComparison.Ordinal));
        }
        CarrierMetadataReadOnly(carrier).Should().BeTrue();
        foreach (var capability in new TypeReference[] { metadata, factory, new ArrayType(metadata) })
        {
            var local = new VariableDefinition(capability);
            body.Body.Variables.Add(local);
            try { Dirty(); } finally { body.Body.Variables.Remove(local); }
            var field = new FieldDefinition("RetainedMetadataCapability", FieldAttributes.Private, capability);
            carrier.Fields.Add(field);
            try { Dirty(); } finally { carrier.Fields.Remove(field); }
            foreach (var signature in new[] { "parameter", "return" })
            {
                var member = new MethodDefinition("MetadataCapability", MethodAttributes.Private | MethodAttributes.Static,
                    signature == "return" ? capability : carrier.Module.TypeSystem.Void);
                if (signature == "parameter") member.Parameters.Add(new ParameterDefinition(capability));
                else member.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                member.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                carrier.Methods.Add(member);
                try { Dirty(); } finally { carrier.Methods.Remove(member); }
            }
        }
        // This ordinary MethodReference is the concrete escape missed by checking
        // only direct types and GenericInstanceMethod.GenericArguments.
        var materialize = new MethodDefinition("Materialize", MethodAttributes.Private | MethodAttributes.Static, carrier.Module.TypeSystem.Object);
        materialize.Parameters.Add(new ParameterDefinition(factory));
        materialize.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        materialize.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt,
            new MethodReference("Invoke", factory.ElementType.GenericParameters[0], factory) { HasThis = true }));
        materialize.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        carrier.Methods.Add(materialize);
        try { Dirty(); } finally { carrier.Methods.Remove(materialize); }
        CarrierMetadataReadOnly(carrier).Should().BeTrue();
        HttpBypasses(controller, map).Should().BeEmpty("every capability mutant was removed");
    }

    private static void AssertDescriptorSignatureControls(TypeDefinition owner, Dictionary<string, TypeDefinition> map)
    {
        DescriptorRelationOffenders(owner, map).Should().BeEmpty();
        void Dirty(Instruction instruction, MethodReference replacement, string relation)
        {
            var original = instruction.Operand;
            instruction.Operand = replacement;
            try { DescriptorRelationOffenders(owner, map).Should().Contain(error => error.Contains(relation, StringComparison.Ordinal)); }
            finally { instruction.Operand = original; }
            DescriptorRelationOffenders(owner, map).Should().BeEmpty("the actual method-reference mutation was restored");
        }
        static MethodReference Copy(MethodReference original, TypeReference? declaring = null, TypeReference? returns = null)
        {
            var copy = new MethodReference(original.Name, returns ?? original.ReturnType, declaring ?? original.DeclaringType)
            { HasThis = original.HasThis, ExplicitThis = original.ExplicitThis, CallingConvention = original.CallingConvention };
            foreach (var parameter in original.Parameters) copy.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
            return copy;
        }
        TypeReference ForeignOwner(TypeReference original) => new(original.Namespace, original.Name, owner.Module,
            new AssemblyNameReference("SurfaceProbe.ForeignOwner", new Version(1, 0)));
        var factory = owner.Methods.Single(method => method.Name == "For");
        var creation = factory.Body.Instructions.First(instruction => instruction.OpCode == OpCodes.Newobj
            && instruction.Operand is MethodReference target && target.DeclaringType.FullName == ProtectedDescriptor);
        var constructor = (MethodReference)creation.Operand;
        Dirty(creation, Copy(constructor, ForeignOwner(constructor.DeclaringType)), "enum-to-request relation");
        var wrongConstructor = Copy(constructor);
        wrongConstructor.Parameters[0].ParameterType = owner.Module.TypeSystem.Int32;
        Dirty(creation, wrongConstructor, "enum-to-request relation");

        var expectedVersion = owner.Methods.Single(method => method.Name == "ExpectedVersion");
        var read = expectedVersion.Body.Instructions.First(instruction => instruction.Operand is MethodReference
        { Name: "get_ExpectedVersion" or "get_ExpectedRoleVersion" });
        var getter = (MethodReference)read.Operand;
        Dirty(read, Copy(getter, returns: owner.Module.TypeSystem.Int32), "request-to-precondition relation");
        Dirty(read, Copy(getter, ForeignOwner(getter.DeclaringType)), "request-to-precondition relation");
        foreach (var name in new[] { "OperationFor", "ExpectedVersion" })
        {
            var method = owner.Methods.Single(candidate => candidate.Name == name);
            var nullable = method.Body.Instructions.First(instruction => instruction.OpCode == OpCodes.Newobj
                && instruction.Operand is MethodReference target && target.DeclaringType.GetElementType().FullName == "System.Nullable`1");
            var target = (MethodReference)nullable.Operand;
            var wrongType = new GenericInstanceType(target.DeclaringType.GetElementType());
            wrongType.GenericArguments.Add(owner.Module.TypeSystem.Int32);
            var relation = name == "OperationFor" ? "request-to-enum relation" : "request-to-precondition relation";
            Dirty(nullable, Copy(target, wrongType), relation);
            var wrongParameter = Copy(target);
            wrongParameter.Parameters[0].ParameterType = owner.Module.TypeSystem.Object;
            Dirty(nullable, wrongParameter, relation);
        }
        var typeFromHandle = factory.Body.Instructions.First(instruction => instruction.Operand is MethodReference
        { Name: "GetTypeFromHandle", DeclaringType.FullName: "System.Type" });
        Dirty(typeFromHandle, Copy((MethodReference)typeFromHandle.Operand, returns: owner.Module.TypeSystem.Object), "enum-to-request relation");
        var refusal = factory.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Newobj
            && instruction.Operand is MethodReference { DeclaringType.FullName: "System.ArgumentOutOfRangeException" });
        var wrongException = Copy((MethodReference)refusal.Operand);
        wrongException.Parameters[0].ParameterType = owner.Module.TypeSystem.Object;
        Dirty(refusal, wrongException, "undefined operations refuse");
    }

}
