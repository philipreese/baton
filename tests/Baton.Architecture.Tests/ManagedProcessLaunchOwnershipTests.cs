using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using Baton.Core;

namespace Baton.Architecture.Tests;

public class ManagedProcessLaunchOwnershipTests
{
    // Inspect resolved IL, not receiver spellings: aliases, arbitrary variable names and async
    // compiler-generated methods must not bypass the process-wide inheritance exclusion (#2677).
    [Fact]
    public void Managed_starts_belong_only_to_the_shared_seam_or_named_controls()
    {
        var fixtureNames = Directory.EnumerateFiles(Path.Combine(RepoRoot.Locate(), "tests"), "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension);
        var assemblies = new[] { "Baton", "Baton.Vendors", "Baton.Cli", "Baton.VendorProbe", "Baton.GateProbe" }
            .Concat(fixtureNames).Distinct().Select(name => Assembly.Load(name!));
        var offenders = assemblies.SelectMany(assembly => assembly.GetTypes())
            .Where(type => type != typeof(ProcessLaunch))
            .SelectMany(Methods)
            .Where(method => !IsNamedControl(method))
            .Where(method => RawStarts(method).Any())
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}")
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Empty(offenders);
        Assert.NotEmpty(Methods(typeof(ProcessLaunch)).SelectMany(RawStarts));
    }

    private static bool IsNamedControl(MethodBase method) =>
        (method.DeclaringType == typeof(ManagedProcessLaunchOwnershipTests)
            && method.Name is nameof(StaticBypass) or nameof(InstanceBypass))
        || (method.DeclaringType == typeof(Baton.Tests.Core.ProcessLaunchTests)
            && method.Name == "RawSiblingBypass");

    [Theory]
    [InlineData(nameof(StaticBypass))]
    [InlineData(nameof(InstanceBypass))]
    public void Guard_detects_static_and_arbitrarily_named_instance_receivers(string name)
    {
        Assert.Single(RawStarts(typeof(ManagedProcessLaunchOwnershipTests).GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Static)!));
    }

    // Never invoked: adversarial ownership controls, not child launches.
    private static Process? StaticBypass(ProcessStartInfo value) => Process.Start(value);
    private static bool InstanceBypass(Process arbitraryReceiver) => arbitraryReceiver.Start();

    private static IEnumerable<MethodBase> Methods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly).Cast<MethodBase>()
            .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static));

    private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes).GetFields()
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opcode => opcode.Value);

    private static IEnumerable<MethodBase> RawStarts(MethodBase method)
    {
        byte[]? bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null) yield break;
        for (int offset = 0; offset < bytes.Length;)
        {
            short code = bytes[offset++];
            if (code == 0xfe) code = unchecked((short)(0xfe00 | bytes[offset++]));
            var opcode = Opcodes[code];
            if (opcode.OperandType == OperandType.InlineMethod)
            {
                var target = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, offset),
                    method.DeclaringType?.GetGenericArguments(),
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (target?.DeclaringType == typeof(Process) && target.Name == nameof(Process.Start))
                    yield return target;
            }
            offset += opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
                _ => 4,
            };
        }
    }
}
