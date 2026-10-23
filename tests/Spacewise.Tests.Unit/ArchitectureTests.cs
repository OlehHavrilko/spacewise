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

    // The catalog is data plus matching: no storage, no Win32, no executor.
    [Theory]
    [InlineData("Spacewise.Platform.Windows")]
    [InlineData("Spacewise.Data")]
    [InlineData("Spacewise.Desktop")]
    public void Catalog_does_not_reference(string assembly) =>
        Assert.DoesNotContain(typeof(Spacewise.Catalog.CatalogLoader).Assembly.GetReferencedAssemblies(), a => a.Name == assembly);

    [Fact]
    public void Catalog_declares_no_platform_invoke_or_mutation()
    {
        Type[] types = typeof(Spacewise.Catalog.CatalogLoader).Assembly.GetTypes();
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        Assert.DoesNotContain(types.SelectMany(t => t.GetMethods(all)), m => (m.Attributes & MethodAttributes.PinvokeImpl) != 0);
        Assert.DoesNotContain(types, t => t.Name.Contains("Executor") || t.Name.Contains("Delete"));
    }

    // PlanExecutor only orchestrates: file removal stays behind IFileRemover in Platform.Windows.
    [Fact]
    public void Core_has_no_mutation_capability() =>
        Assert.DoesNotContain(Core.GetTypes(), t => (t.Name.Contains("Executor") && t.Name != nameof(PlanExecutor)) || t.Name.Contains("Delete"));
}
