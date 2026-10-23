using System.Reflection;
using Spacewise.Core;

namespace Spacewise.Tests.Unit;

// Core is domain and policy only (spec §7.1): Win32 lives in Spacewise.Platform.Windows.
public class ArchitectureTests
{
    private static readonly Assembly Core = typeof(IScanner).Assembly;

    [Fact]
    public void Core_declares_no_platform_invoke()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var pinvoke = Core.GetTypes()
            .SelectMany(t => t.GetMethods(all))
            .Where(m => (m.Attributes & MethodAttributes.PinvokeImpl) != 0)
            .Select(m => $"{m.DeclaringType}.{m.Name}");
        Assert.Empty(pinvoke);
    }

    [Theory]
    [InlineData("Spacewise.Platform.Windows")]
    [InlineData("Spacewise.Desktop")]
    [InlineData("PresentationFramework")]
    [InlineData("Microsoft.Win32.Registry")]
    public void Core_does_not_reference(string assembly) =>
        Assert.DoesNotContain(Core.GetReferencedAssemblies(), a => a.Name == assembly);

    // Data is portable storage: DPAPI and other Win32 stay in Platform.Windows.
    [Theory]
    [InlineData("Spacewise.Platform.Windows")]
    [InlineData("Spacewise.Desktop")]
    [InlineData("System.Security.Cryptography.ProtectedData")]
    public void Data_does_not_reference(string assembly) =>
        Assert.DoesNotContain(typeof(Spacewise.Data.SqliteDatabase).Assembly.GetReferencedAssemblies(), a => a.Name == assembly);

    [Fact]
    public void Core_has_no_mutation_capability() =>
        Assert.DoesNotContain(Core.GetTypes(), t => t.Name.Contains("Executor") || t.Name.Contains("Delete"));
}
