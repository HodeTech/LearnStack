using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

internal static class EndpointCompositionControls
{
    // Mutate the real production mapping, then restore it before the next
    // detector runs. These instructions are inspected, never executed.
    internal static void Plant(ModuleDefinition api, Action<string, string> assertDirty)
    {
        var main = api.GetType("Program").Methods.Single(method => method.Name == "<Main>$");
        var health = main.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method && method.Name == "MapGet");
        var path = EndpointComposition.ArgumentProducer(main, health, 1)
            ?? throw new InvalidOperationException("The live health mapping must have a verifiable path producer.");
        var processor = main.Body.GetILProcessor();
        var original = path.Operand;
        var concat = Instruction.Create(OpCodes.Call, api.ImportReference(typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!));
        var suffix = Instruction.Create(OpCodes.Ldstr, "/healthz");
        path.Operand = "/prefix";
        processor.InsertAfter(path, suffix);
        processor.InsertAfter(suffix, concat);
        try { assertDirty("Unexpected infrastructure path/handler/signature:", "a computed path cannot borrow the last /healthz literal"); }
        finally
        {
            processor.Remove(concat);
            processor.Remove(suffix);
            path.Operand = original;
        }

        var variable = new VariableDefinition(api.TypeSystem.String);
        main.Body.Variables.Add(variable);
        var store = Instruction.Create(OpCodes.Stloc, variable);
        var load = Instruction.Create(OpCodes.Ldloc, variable);
        processor.InsertAfter(path, store);
        processor.InsertAfter(store, load);
        try { assertDirty("Unexpected infrastructure path/handler/signature:", "a variable path remains unverifiable even when initialized with /healthz"); }
        finally
        {
            processor.Remove(load);
            processor.Remove(store);
            main.Body.Variables.Remove(variable);
        }

        var decoy = Instruction.Create(OpCodes.Ldstr, "/healthz");
        var discard = Instruction.Create(OpCodes.Pop);
        path.Operand = "/extra";
        processor.InsertBefore(health, decoy);
        processor.InsertBefore(health, discard);
        try { assertDirty("Unexpected infrastructure path/handler/signature:", "a discarded /healthz literal is not the mapping argument"); }
        finally
        {
            processor.Remove(discard);
            processor.Remove(decoy);
            path.Operand = original;
        }

        var controllerMapping = main.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method && method.Name == "MapControllers");
        var duplicateControllers = Instruction.Create(OpCodes.Call, (MethodReference)controllerMapping.Operand);
        main.Body.Instructions.Add(duplicateControllers);
        try { assertDirty("Require exactly one infrastructure mapping:", "a duplicate MapControllers cannot borrow the one production composition site"); }
        finally { main.Body.Instructions.Remove(duplicateControllers); }

        var helper = new TypeDefinition("SurfaceProbe", "HiddenEndpointComposition", TypeAttributes.Public | TypeAttributes.Sealed, api.TypeSystem.Object);
        var body = new MethodDefinition("Configure", MethodAttributes.Public | MethodAttributes.Static, api.TypeSystem.Void);
        helper.Methods.Add(body);
        api.Types.Add(helper);
        var dataSources = typeof(IEndpointRouteBuilder).GetProperty(nameof(IEndpointRouteBuilder.DataSources))!.GetMethod!;
        var addSource = typeof(ICollection<EndpointDataSource>).GetMethod(nameof(ICollection<EndpointDataSource>.Add))!;
        var addPart = typeof(MvcCoreMvcBuilderExtensions).GetMethod(nameof(MvcCoreMvcBuilderExtensions.AddApplicationPart))!;
        var endpoints = typeof(EndpointRoutingApplicationBuilderExtensions).GetMethod(nameof(EndpointRoutingApplicationBuilderExtensions.UseEndpoints))!;
        var run = typeof(RunExtensions).GetMethod(nameof(RunExtensions.Run), [typeof(IApplicationBuilder), typeof(RequestDelegate)])!;
        try
        {
            body.Body.Instructions.Add(Instruction.Create(OpCodes.Call, (MethodReference)controllerMapping.Operand));
            try { assertDirty("Unapproved endpoint mapping:", "a helper cannot introduce another MapControllers site"); }
            finally { body.Body.Instructions.Clear(); }
            foreach (var capability in new[] { dataSources, addSource, addPart, endpoints, run })
            {
                body.Body.Instructions.Add(Instruction.Create(OpCodes.Call, api.ImportReference(capability)));
                if (capability == dataSources) body.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, api.ImportReference(addSource)));
                try { assertDirty("Unapproved endpoint mapping:", "hidden runtime endpoint composition through " + capability.DeclaringType!.FullName + "." + capability.Name); }
                finally { body.Body.Instructions.Clear(); }
            }
        }
        finally { api.Types.Remove(helper); }
    }
}
